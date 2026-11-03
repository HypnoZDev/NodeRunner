using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class DeletionTests
{
    private const uint ParentMapName = 0x1000, ChildMapName = 0x2000;
    private const uint OtherLod = 0x10, BuildingLod = 0x11, BuildingA = 0x20, BuildingB = 0x21, Bench = 0x30;

    /// <summary>One entity of a test map: model, parent index, level, number of children, flags (8 = parent in the parent map).</summary>
    private readonly record struct Item(uint Archetype, int ParentIndex, LodLevel Level, int ChildCount = 0, uint Flags = 0);

    /// <summary>A minimal binary .ymap: the map header (CMapData), the list of entities and the entities (CEntityDef).</summary>
    private static byte[] BuildYmap(uint name, uint parent, params Item[] items)
    {
        var document = new MetaDocument { RootBlock = 0 };
        document.Structures.Add(Structure(3545841574, 512)); // CMapData
        document.Structures.Add(Structure(3461354627, 128)); // CEntityDef

        var map = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(8), name);
        BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(12), parent);
        var entities = new byte[items.Length * 128];
        var pointers = new byte[items.Length * 8];
        for (int i = 0; i < items.Length; i++)
        {
            var e = entities.AsSpan(i * 128);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], items[i].Archetype);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], items[i].Flags);
            BinaryPrimitives.WriteSingleLittleEndian(e[32..], i * 10f);  // Position X.
            BinaryPrimitives.WriteSingleLittleEndian(e[60..], 1f);       // Rotation W.
            BinaryPrimitives.WriteSingleLittleEndian(e[64..], 1f);       // Scale XY.
            BinaryPrimitives.WriteSingleLittleEndian(e[68..], 1f);       // Scale Z.
            BinaryPrimitives.WriteInt32LittleEndian(e[72..], items[i].ParentIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(e[84..], (uint)items[i].Level);
            BinaryPrimitives.WriteInt32LittleEndian(e[88..], items[i].ChildCount);
            BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(i * 8), MetaDocument.Pointer(1, i * 128));
        }
        BinaryPrimitives.WriteUInt64LittleEndian(map.AsSpan(96), MetaDocument.Pointer(2, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(104), (ushort)items.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(106), (ushort)items.Length);

        document.Blocks.Add(new MetaDocument.Block(3545841574, map));
        document.Blocks.Add(new MetaDocument.Block(3461354627, entities));
        document.Blocks.Add(new MetaDocument.Block(7, pointers)); // Pointers.
        return document.Write().Content;
    }

    private static MetaDocument.StructureInfo Structure(uint hash, int size)
    {
        var info = new byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(info, hash);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0x18), size);
        return new MetaDocument.StructureInfo(info, []);
    }

    /// <summary>
    /// Parent map: a LOD standing for another district, then the LOD of two buildings. Child map: a bench without
    /// LOD, then the two buildings (their LOD is n° 1 of the parent map).
    /// </summary>
    private static (MapLibrary Library, MapData Parent, MapData Child, byte[] ParentFile, byte[] ChildFile) Sample()
    {
        var parentFile = BuildYmap(ParentMapName, 0,
            new Item(OtherLod, -1, LodLevel.Lod),
            new Item(BuildingLod, -1, LodLevel.Lod, ChildCount: 2));
        var childFile = BuildYmap(ChildMapName, ParentMapName,
            new Item(Bench, -1, LodLevel.OrphanHd),
            new Item(BuildingA, 1, LodLevel.Hd, Flags: 8),
            new Item(BuildingB, 1, LodLevel.Hd, Flags: 8));
        var reader = new YmapReader();
        var parent = reader.Read(parentFile);
        var child = reader.Read(childFile);
        var library = MapLibrary.Create((parent, "parent.ymap"), (child, "child.ymap"));
        return (library, parent, child, parentFile, childFile);
    }

    [Fact]
    public void A_parent_standing_for_other_objects_stays()
    {
        var (_, parent, child, _, _) = Sample();

        var plan = DeletionPlan.For(child.Entities[1]);

        Assert.Equal([child.Entities[1]], plan.Entities);
        Assert.Equal([parent.Entities[1]], plan.KeptParents);
    }

    [Fact]
    public void A_parent_left_without_children_goes_too()
    {
        var (library, parent, child, _, _) = Sample();
        library.SetDeleted([child.Entities[1]], true);

        var plan = DeletionPlan.For(child.Entities[2]);

        Assert.Equal([child.Entities[2], parent.Entities[1]], plan.Entities);
        Assert.Empty(plan.KeptParents);
    }

    [Fact]
    public void Deleting_a_lod_takes_its_children()
    {
        var (_, parent, child, _, _) = Sample();

        var plan = DeletionPlan.For(parent.Entities[1]);

        Assert.Equal([parent.Entities[1], child.Entities[1], child.Entities[2]], plan.Entities);
    }

    [Fact]
    public void Deleting_both_buildings_takes_their_shared_lod()
    {
        var (_, parent, child, _, _) = Sample();

        var plan = DeletionPlan.For([child.Entities[1], child.Entities[2]]);

        Assert.Equal([child.Entities[1], child.Entities[2], parent.Entities[1]], plan.Entities);
        Assert.Empty(plan.KeptParents);
        Assert.Same(child.Entities[2], plan.Target);
    }

    [Fact]
    public void A_group_holding_a_lod_and_its_child_lists_each_object_once()
    {
        var (_, parent, child, _, _) = Sample();

        var plan = DeletionPlan.For([child.Entities[1], parent.Entities[1], child.Entities[0]]);

        Assert.Equal([child.Entities[1], parent.Entities[1], child.Entities[0], child.Entities[2]], plan.Entities);
        Assert.Equal([child.Entities[2]], plan.Children);
        Assert.Empty(plan.Parents);
    }

    [Fact]
    public void Undoing_a_group_deletion_selects_the_group_again()
    {
        var (library, _, child, _, _) = Sample();
        var edit = DeleteEdit.Apply(library, DeletionPlan.For([child.Entities[0], child.Entities[1]]), "Supprimer");

        edit.Undo();

        Assert.Equal([child.Entities[0], child.Entities[1]], ((IEdit)edit).Selection);
        Assert.False(child.Entities[1].IsDeleted);
    }

    [Fact]
    public void Undo_brings_the_objects_back()
    {
        var (library, _, child, _, _) = Sample();
        var edit = DeleteEdit.Apply(library, DeletionPlan.For(child.Entities[0]), "Supprimer");
        Assert.True(child.Entities[0].IsDeleted);

        edit.Undo();

        Assert.False(child.Entities[0].IsDeleted);
    }

    [Fact]
    public void Saved_files_leave_deleted_objects_out_and_renumber_the_links()
    {
        var (library, parent, child, parentFile, childFile) = Sample();
        library.SetDeleted([parent.Entities[0], child.Entities[0]], true); // The other district's LOD, and the bench.
        var writer = new YmapWriter();
        var reader = new YmapReader();

        var newParent = reader.Read(writer.Write(parentFile, parent, MapExtents.Of(parent)).Content);
        var newChild = reader.Read(writer.Write(childFile, child, MapExtents.Of(child)).Content);

        Assert.Equal([BuildingLod], newParent.Entities.Select(e => e.ArchetypeHash));
        Assert.Equal([BuildingA, BuildingB], newChild.Entities.Select(e => e.ArchetypeHash));
        Assert.All(newChild.Entities, e => Assert.Equal(0, e.ParentIndex)); // The buildings' LOD is now n° 0.
        Assert.Equal(new Vector3(10f, 0f, 0f), newChild.Entities[0].Position);
    }

    [Fact]
    public void Saved_files_count_the_children_left()
    {
        var (library, parent, child, parentFile, _) = Sample();
        library.SetDeleted([child.Entities[1]], true);

        var written = new YmapReader().Read(new YmapWriter().Write(parentFile, parent, MapExtents.Of(parent)).Content);

        Assert.Equal(1, written.Entities[1].ChildCount);
    }

    [Fact]
    public void Deleted_objects_are_not_found_around_the_camera()
    {
        var (library, _, child, _, _) = Sample();
        library.SetDeleted([child.Entities[0]], true);

        Assert.DoesNotContain(child.Entities[0], library.EntitiesAround(Vector2.Zero, 100f));
    }
}
