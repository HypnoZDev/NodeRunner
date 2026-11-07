using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class YtypWriterTests
{
    private const uint PointerBlock = 7;
    private const uint InteriorName = 0x1234;

    // A .ytyp holding one interior with three objects, the last two in its room, and an entity set of two objects
    // (rooms 0 and 7), and a portal of four corners.
    private static byte[] Sample()
    {
        var document = new MetaDocument { Name = "sample", RootBlock = 0 };
        void Structure(uint hash, int size)
        {
            var info = new byte[0x20];
            BinaryPrimitives.WriteUInt32LittleEndian(info, hash);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(0x18), size);
            document.Structures.Add(new MetaDocument.StructureInfo(info, []));
        }
        Structure(YtypReader.InteriorArchetypeStructure, 240);
        Structure(YmapReader.EntityStructure, YmapReader.EntitySize);
        Structure(YtypReader.RoomStructure, YtypReader.RoomSize);
        Structure(YtypReader.EntitySetStructure, YtypReader.EntitySetSize);
        Structure(YtypReader.PortalStructure, YtypReader.PortalSize);

        void Array(byte[] data, int field, int block, int offset, int count)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(field), MetaDocument.Pointer(block, offset));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field + 8), (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field + 10), (ushort)count);
        }

        var types = new byte[64];
        Array(types, YtypReader.TypesArchetypes, 1, 0, 1);
        var archetypePointers = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(archetypePointers, MetaDocument.Pointer(2, 0));
        var interior = new byte[240];
        BinaryPrimitives.WriteUInt32LittleEndian(interior.AsSpan(YtypReader.ArchName), InteriorName);
        Array(interior, YtypReader.InteriorEntities, 3, 0, 3);
        Array(interior, YtypReader.InteriorRooms, 5, 0, 1);
        var entityPointers = new byte[24];
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(entityPointers.AsSpan(i * 8), MetaDocument.Pointer(4, i * YmapReader.EntitySize));
        var room = new byte[YtypReader.RoomSize];
        Array(room, YtypReader.RoomAttachedObjects, 6, 0, 2);
        var roomObjects = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(roomObjects, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(roomObjects.AsSpan(4), 2);
        Array(interior, YtypReader.InteriorEntitySets, 7, 0, 1);
        var set = new byte[YtypReader.EntitySetSize];
        BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(YtypReader.EntitySetName), 0xABCD);
        Array(set, YtypReader.EntitySetLocations, 8, 0, 2);
        Array(set, YtypReader.EntitySetEntities, 9, 0, 2);
        var setRooms = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(setRooms.AsSpan(4), 7);
        var setPointers = new byte[16];
        for (int i = 0; i < 2; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(setPointers.AsSpan(i * 8), MetaDocument.Pointer(4, (3 + i) * YmapReader.EntitySize));

        Array(interior, YtypReader.InteriorPortals, 10, 0, 1);
        var portal = new byte[YtypReader.PortalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(portal.AsSpan(YtypReader.PortalRoomTo), 1);
        Array(portal, YtypReader.PortalCorners, 11, 0, 4);
        var corners = new byte[4 * 16];
        for (int c = 0; c < 4; c++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(corners.AsSpan(c * 16), c);
            BinaryPrimitives.WriteSingleLittleEndian(corners.AsSpan(c * 16 + 12), float.NaN);
        }

        var entities = new byte[5 * YmapReader.EntitySize];
        for (int i = 0; i < 5; i++)
        {
            int o = i * YmapReader.EntitySize;
            BinaryPrimitives.WriteUInt32LittleEndian(entities.AsSpan(o + YmapReader.EntityArchetype), (uint)(100 + i));
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityPosition), i);
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityRotation + 12), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityScaleXy), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(entities.AsSpan(o + YmapReader.EntityScaleZ), 1f);
        }

        document.Blocks.Add(new MetaDocument.Block(YtypReader.MapTypesStructure, types));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock, archetypePointers));
        document.Blocks.Add(new MetaDocument.Block(YtypReader.InteriorArchetypeStructure, interior));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock, entityPointers));
        document.Blocks.Add(new MetaDocument.Block(YmapReader.EntityStructure, entities));
        document.Blocks.Add(new MetaDocument.Block(YtypReader.RoomStructure, room));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock + 1, roomObjects));
        document.Blocks.Add(new MetaDocument.Block(YtypReader.EntitySetStructure, set));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock + 1, setRooms));
        document.Blocks.Add(new MetaDocument.Block(PointerBlock, setPointers));
        document.Blocks.Add(new MetaDocument.Block(YtypReader.PortalStructure, portal));
        document.Blocks.Add(new MetaDocument.Block(0x33, corners));
        return document.Write().Content;
    }

    [Fact]
    public void Moves_an_object_of_an_interior()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        Assert.Equal(InteriorName, interior.NameHash);
        var moved = interior.InteriorEntities[1];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        moved.SetPlacement(new Vector3(3, 4, 5), turn, new Vector3(2, 2, 3));

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal(Vector3.Zero, back.InteriorEntities[0].Position);
        var read = back.InteriorEntities[1];
        Assert.Equal(new Vector3(3, 4, 5), read.Position);
        Assert.True(Quaternion.Dot(turn, read.Orientation) > 0.9999f);
        Assert.Equal(new Vector3(2, 2, 3), read.Scale);
    }

    [Fact]
    public void Refuses_another_file()
    {
        var interior = new YtypReader().Read(Sample()).Archetypes.Single();
        var other = new Archetype { NameHash = 99, Kind = ArchetypeKind.Interior, InteriorEntities = interior.InteriorEntities };
        Assert.Throws<InvalidDataException>(() => new YtypWriter().Write(Sample(), [other]));
    }

    [Fact]
    public void Deletes_an_object_and_renumbers_its_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        Assert.Equal(0, interior.InteriorEntities[2].InteriorRoom);
        var library = new MapLibrary();
        library.SetDeleted([interior.InteriorEntities[1]], true);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal(2, back.InteriorEntities.Count);
        Assert.Equal([100u, 102u], back.InteriorEntities.Select(e => e.ArchetypeHash));
        Assert.Equal(-1, back.InteriorEntities[0].InteriorRoom);
        Assert.Equal(0, back.InteriorEntities[1].InteriorRoom);
    }

    [Fact]
    public void Adds_an_object_to_a_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var library = new MapLibrary();
        library.SetDeleted([interior.InteriorEntities[0]], true);
        interior.AddInteriorEntity(new MapEntity
        {
            ArchetypeHash = 555, Position = new Vector3(1, 2, 3), StoredRotation = Quaternion.Identity, InteriorRoom = 0,
            LodDistance = 50, LodLevel = LodLevel.OrphanHd,
        });

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal([101u, 102u, 555u], back.InteriorEntities.Select(e => e.ArchetypeHash));
        var added = back.InteriorEntities[2];
        Assert.Equal(new Vector3(1, 2, 3), added.Position);
        Assert.Equal(0, added.InteriorRoom);
        Assert.Equal(50f, added.LodDistance);
        Assert.All(back.InteriorEntities.Skip(1), e => Assert.Equal(0, e.InteriorRoom));
    }

    [Fact]
    public void Deletes_an_object_of_an_entity_set_with_its_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var set = interior.InteriorEntitySets.Single();
        Assert.Equal([0, 7], set.Entities.Select(e => e.InteriorRoom));
        new MapLibrary().SetDeleted([set.Entities[0]], true);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        var read = back.InteriorEntitySets.Single();
        Assert.Equal(0xABCDu, read.NameHash);
        Assert.Equal([104u], read.Entities.Select(e => e.ArchetypeHash));
        Assert.Equal(7, read.Entities[0].InteriorRoom);
        Assert.Equal(3, back.InteriorEntities.Count);
    }

    private static MapEntity MovedCopy(MapEntity old) => new()
    {
        ArchetypeHash = old.ArchetypeHash, Position = old.Position, StoredRotation = old.StoredRotation, Scale = old.Scale,
        Flags = old.Flags, LodDistance = old.LodDistance, InteriorRoom = old.InteriorRoom, MovedFrom = old,
    };

    [Fact]
    public void Moves_objects_into_and_out_of_an_entity_set()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var set = interior.InteriorEntitySets.Single();
        var library = new MapLibrary();
        var intoSet = interior.InteriorEntities[2];
        var outOfSet = set.Entities[1];
        library.SetDeleted([intoSet, outOfSet], true, countAsDeleted: false);
        set.AddEntity(MovedCopy(intoSet));
        interior.AddInteriorEntity(MovedCopy(outOfSet));

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal([100u, 101u, 104u], back.InteriorEntities.Select(e => e.ArchetypeHash));
        Assert.Equal(-1, back.InteriorEntities[2].InteriorRoom); // Room 7 does not exist in the interior.
        Assert.Equal(0, back.InteriorEntities[1].InteriorRoom);
        var read = back.InteriorEntitySets.Single();
        Assert.Equal([103u, 102u], read.Entities.Select(e => e.ArchetypeHash));
        Assert.Equal([0, 0], read.Entities.Select(e => e.InteriorRoom));
        Assert.Equal(2f, read.Entities[1].Position.X);
    }

    [Fact]
    public void Renames_and_creates_entity_sets()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        interior.InteriorEntitySets[0].NameHash = 0xBEEF;
        var created = new InteriorEntitySet(0xCAFE, []);
        interior.AddEntitySet(created);
        var moved = interior.InteriorEntities[0];
        new MapLibrary().SetDeleted([moved], true, countAsDeleted: false);
        created.AddEntity(MovedCopy(moved));

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal([0xBEEFu, 0xCAFEu], back.InteriorEntitySets.Select(s => s.NameHash));
        Assert.Equal([103u, 104u], back.InteriorEntitySets[0].Entities.Select(e => e.ArchetypeHash));
        Assert.Equal([100u], back.InteriorEntitySets[1].Entities.Select(e => e.ArchetypeHash));
        Assert.Equal([101u, 102u], back.InteriorEntities.Select(e => e.ArchetypeHash));
    }

    [Fact]
    public void Changes_a_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var room = interior.InteriorRooms[0] with
        {
            Name = "kitchen", BoundsMin = new Vector3(-1, -2, -3), BoundsMax = new Vector3(4, 5, 6), Flags = 0x104,
            TimecycleHash = 0xAAAA, SecondaryTimecycleHash = 0xBBBB, FloorId = 2, ExteriorVisibilityDepth = 1,
        };
        interior.SetRoom(0, room);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal(room, back.InteriorRooms.Single());
        Assert.Equal([-1, 0, 0], back.InteriorEntities.Select(e => e.InteriorRoom));

        // Renamed again: a shorter name, then none.
        var again = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content);
        Assert.Equal("kitchen", again.Archetypes.Single().InteriorRooms[0].Name);
        interior.SetRoom(0, room with { Name = "" });
        Assert.Equal("", new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single().InteriorRooms[0].Name);
    }

    [Fact]
    public void Changes_a_portal()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        Assert.Equal([0f, 1f, 2f, 3f], interior.InteriorPortals.Single().Corners.Select(c => c.X));
        var portal = interior.InteriorPortals[0] with
        {
            RoomFrom = 1, RoomTo = 0, Flags = 0x2040, Opacity = 127, MirrorPriority = 1, AudioOcclusionHash = 0xCCCC,
            Corners = [new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(7, 8, 9), new Vector3(-1, -2, -3)],
        };
        interior.SetPortal(0, portal);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.True(portal.SameAs(back.InteriorPortals.Single()));
        Assert.Equal([-1, 0, 0], back.InteriorEntities.Select(e => e.InteriorRoom));
    }

    [Fact]
    public void Writes_an_object_put_in_another_room()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var portalObjects = interior.InteriorEntities.Select(e => e.InteriorPortal).ToList();
        interior.InteriorEntities[0].SetInteriorPlace(0, interior.InteriorEntities[0].InteriorPortal);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal([0, 0, 0], back.InteriorEntities.Select(e => e.InteriorRoom));
        Assert.Equal(portalObjects, back.InteriorEntities.Select(e => e.InteriorPortal));
        Assert.Equal(interior.InteriorEntitySets.Single().Entities.Select(e => e.InteriorRoom),
            back.InteriorEntitySets.Single().Entities.Select(e => e.InteriorRoom));

        // Back in no room: as in the file.
        interior.InteriorEntities[0].SetInteriorPlace(-1, interior.InteriorEntities[0].InteriorPortal);
        back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal([-1, 0, 0], back.InteriorEntities.Select(e => e.InteriorRoom));
    }

    [Fact]
    public void Adds_and_removes_rooms_and_portals()
    {
        var source = Sample();
        var interior = new YtypReader().Read(source).Archetypes.Single();
        var library = new MapLibrary();
        library.AddArchetype(interior);
        var original = library.GetLayout(InteriorName);

        int room = library.AddRoom(InteriorName, new InteriorRoom(new Vector3(-10), new Vector3(10)) { Name = "hall", Flags = 0x40 });
        Assert.Equal(1, room);
        interior.InteriorEntities[2].SetInteriorPlace(1, -1);
        interior.InteriorEntitySets[0].Entities[0].SetInteriorPlace(1, -1);
        Vector3[] corners = [new(1, 0, 2), new(2, 0, 2), new(2, 0, 0), new(1, 0, 0)];
        int portal = library.AddPortal(InteriorName, new InteriorPortal(1, 0, 0x40) { Corners = corners });
        Assert.Equal(1, portal);

        var back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Equal(["", "hall"], back.InteriorRooms.Select(r => r.Name));
        Assert.Equal(0x40u, back.InteriorRooms[1].Flags);
        Assert.Equal(2, back.InteriorPortals.Count);
        Assert.Equal(corners, back.InteriorPortals[1].Corners);
        Assert.Equal((1, 0), (back.InteriorPortals[1].RoomFrom, back.InteriorPortals[1].RoomTo));
        Assert.Equal([-1, 0, 1], back.InteriorEntities.Select(e => e.InteriorRoom));
        Assert.Equal([1, 7], back.InteriorEntitySets.Single().Entities.Select(e => e.InteriorRoom));

        // The room removed: its portals too, its objects back in the limbo.
        var withRoom = library.GetLayout(InteriorName);
        library.RemoveRoom(InteriorName, 1);
        Assert.Empty(interior.InteriorPortals);
        back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Single(back.InteriorRooms);
        Assert.Empty(back.InteriorPortals);
        Assert.Equal([-1, 0, 0], back.InteriorEntities.Select(e => e.InteriorRoom));
        Assert.Equal([0, 7], back.InteriorEntitySets.Single().Entities.Select(e => e.InteriorRoom));

        // Undone: as before.
        library.SetLayout(InteriorName, withRoom);
        Assert.Equal(1, interior.InteriorEntities[2].InteriorRoom);
        library.SetLayout(InteriorName, original);
        back = new YtypReader().Read(new YtypWriter().Write(source, [interior]).Content).Archetypes.Single();
        Assert.Single(back.InteriorRooms);
        Assert.True(back.InteriorPortals.Single().SameAs(interior.InteriorPortals.Single()));
        Assert.Equal([-1, 0, 0], back.InteriorEntities.Select(e => e.InteriorRoom));
    }
}
