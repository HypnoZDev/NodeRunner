using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class CarGeneratorTests
{
    private const uint CarGenStructure = 0x6EE83BDF;

    // A test map with the given parking spots (CCarGen, 80 bytes each): position, orientation, width, model.
    internal static byte[] BuildYmap(params (Vector3 Position, Vector2 Orientation, float Width, uint Model)[] spots)
    {
        var document = MetaDocument.Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd)));
        document.Structures.Add(DeletionTests.Structure(CarGenStructure, 80));
        var items = new byte[spots.Length * 80];
        for (int i = 0; i < spots.Length; i++)
        {
            var c = items.AsSpan(i * 80);
            var (p, o, w, model) = spots[i];
            BinaryPrimitives.WriteSingleLittleEndian(c[16..], p.X);
            BinaryPrimitives.WriteSingleLittleEndian(c[20..], p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(c[24..], p.Z);
            BinaryPrimitives.WriteSingleLittleEndian(c[32..], o.X);
            BinaryPrimitives.WriteSingleLittleEndian(c[36..], o.Y);
            BinaryPrimitives.WriteSingleLittleEndian(c[40..], w);
            BinaryPrimitives.WriteUInt32LittleEndian(c[44..], model);
            BinaryPrimitives.WriteUInt32LittleEndian(c[48..], 0xE60);
            for (int k = 0; k < 4; k++)
                BinaryPrimitives.WriteInt32LittleEndian(c[(52 + k * 4)..], k == 0 ? 12 : -1);
            c[72] = 0xFF;
        }
        document.Blocks.Add(new MetaDocument.Block(CarGenStructure, items));
        var map = document.Blocks[document.RootBlock].Data;
        BinaryPrimitives.WriteUInt64LittleEndian(map.AsSpan(240), MetaDocument.Pointer(document.Blocks.Count - 1, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(248), (ushort)spots.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(250), (ushort)spots.Length);
        return document.Write().Content;
    }

    [Fact]
    public void Parking_spots_are_read_from_a_map_file()
    {
        var file = BuildYmap((new Vector3(10, 20, 30), new Vector2(0, 5), 2.5f, 0xABCD), (new Vector3(-4, 1, 2), new Vector2(3, 0), 3f, 0));
        var map = new YmapReader().Read(file);

        Assert.Single(map.Entities);
        Assert.Equal(2, map.CarGenerators.Count);
        var first = map.CarGenerators[0];
        Assert.Equal(new Vector3(10, 20, 30), first.Position);
        Assert.Equal(5f, first.Length, 3);
        Assert.Equal(2.5f, first.Width);
        Assert.Equal(0xABCDu, first.CarModel);
        Assert.Equal(0xE60u, first.Flags);
        Assert.Equal(12, first.Color1);
        Assert.Equal(-1, first.Color4);
        Assert.Equal(-1, first.Livery);
        Assert.Equal(MathF.PI / 2, first.Angle, 3);
        Assert.Equal(1, map.CarGenerators[1].SourceIndex);

        // Footprint: 5 m along Y, 2.5 m along X.
        var corners = first.Corners();
        Assert.Equal(2.5f, corners.Max(c => c.X) - corners.Min(c => c.X), 3);
        Assert.Equal(5f, corners.Max(c => c.Y) - corners.Min(c => c.Y), 3);
    }

    [Fact]
    public void A_changed_parking_spot_is_written_back_to_its_map_file()
    {
        var file = BuildYmap((new Vector3(10, 20, 30), new Vector2(0, 5), 2.5f, 0xABCD), (new Vector3(-4, 1, 2), new Vector2(3, 0), 3f, 0));
        var map = new YmapReader().Read(file);
        var changed = map.CarGenerators[1] with
        {
            Position = new Vector3(-6, 3, 2.5f), Orientation = new Vector2(0, -4.5f), Width = 2.8f, CarModel = 0x1234, Flags = 0x60,
            Color1 = 3, Color2 = 4, Color3 = 5, Color4 = 6, PopGroup = 0x77, Livery = 4,
        };
        var edit = new Editor.CarGeneratorEdit(map, 1, map.CarGenerators[1], changed, "test");
        edit.Redo();

        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);
        var written = new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content);

        Assert.Equal(map.CarGenerators[0], written.CarGenerators[0]);
        Assert.Equal(changed, written.CarGenerators[1]);
        Assert.True(written.StreamingExtentsMin.X <= -6f && written.StreamingExtentsMax.Y >= 20f);

        // Unchanged spots give the same values back; undoing puts the file's spot back.
        edit.Undo();
        var undone = new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content);
        Assert.Equal(new YmapReader().Read(file).CarGenerators, undone.CarGenerators);
    }

    [Fact]
    public void Objects_added_to_a_map_file_with_parking_spots_keep_the_spots()
    {
        var file = BuildYmap((new Vector3(10, 20, 30), new Vector2(0, 5), 2.5f, 0xABCD));
        var map = new YmapReader().Read(file);
        var library = Data.Maps.MapLibrary.Create((map, "imported.ymap"));
        map.Entities[0].SetPlacement(new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One);
        for (int i = 0; i < 300; i++) // More than one block of objects.
            library.AddEntity(map, Editor.Adding.NewEntity(0x5000u + (uint)i, null, new Vector3(i, 0, 0), Quaternion.Identity));

        var written = new YmapReader().Read(new YmapWriter().Write(file, map, Editor.MapExtentsCalculator.Compute(map, _ => null)).Content);

        Assert.Equal(map.CarGenerators, written.CarGenerators);
        Assert.Equal(301, written.Entities.Count);
        Assert.Equal(new Vector3(1, 2, 3), written.Entities[0].Position);
        Assert.Equal(0x5000u + 299, written.Entities[300].ArchetypeHash);
        Assert.Equal(new Vector3(299, 0, 0), written.Entities[300].Position);
        Assert.True(written.StreamingExtentsMax.X >= 299f);
    }

    [Fact]
    public void A_deleted_parking_spot_is_left_out_and_the_others_keep_their_bytes()
    {
        var file = BuildYmap((new Vector3(10, 20, 30), new Vector2(0, 5), 2.5f, 0xABCD), (new Vector3(-4, 1, 2), new Vector2(3, 0), 3f, 0),
            (new Vector3(7, 7, 7), new Vector2(1, 0), 2f, 0x77));
        var map = new YmapReader().Read(file);
        var original = map.CarGenerators.ToList();
        var edit = Editor.CarGeneratorDeleteEdit.Apply(map, 1, "test");
        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);

        var written = new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content);
        Assert.Equal(2, written.CarGenerators.Count);
        Assert.Equal(original[0], written.CarGenerators[0]);
        Assert.Equal(original[2] with { SourceIndex = 1 }, written.CarGenerators[1]);

        // All deleted: no spot left in the file.
        var first = Editor.CarGeneratorDeleteEdit.Apply(map, 0, "test");
        var last = Editor.CarGeneratorDeleteEdit.Apply(map, 2, "test");
        Assert.Empty(new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content).CarGenerators);

        // Undone: the file written as it was.
        last.Undo();
        first.Undo();
        edit.Undo();
        Assert.Equal(original, new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content).CarGenerators);
    }

    [Fact]
    public void Parking_spots_are_added_to_a_map_file_with_or_without_spots()
    {
        var spot = new Data.Maps.CarGenerator { Position = new Vector3(100, 50, 3), Orientation = new Vector2(0, 5), Width = 3f, Flags = 0xE60, CarModel = 0x99 };

        // A file with spots: the new one goes after them.
        var file = BuildYmap((new Vector3(10, 20, 30), new Vector2(0, 5), 2.5f, 0xABCD));
        var map = new YmapReader().Read(file);
        Editor.CarGeneratorAddEdit.Apply(map, spot, "test");
        var written = new YmapReader().Read(new YmapWriter().Write(file, map, Editor.MapExtentsCalculator.Compute(map, _ => null)).Content);
        Assert.Equal(2, written.CarGenerators.Count);
        Assert.Equal(map.CarGenerators[0], written.CarGenerators[0]);
        Assert.Equal(spot with { SourceIndex = 1 }, written.CarGenerators[1]);
        // Loaded from far enough around the new spot.
        Assert.True(written.StreamingExtentsMax.X >= 100 + Editor.MapExtentsCalculator.AddedSpotReach);

        // A file without spots: needs the description of CCarGen.
        var plain = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd));
        var bare = new YmapReader().Read(plain);
        Editor.CarGeneratorAddEdit.Apply(bare, spot, "test");
        var extents = Editor.MapExtentsCalculator.Compute(bare, _ => null);
        Assert.Throws<InvalidDataException>(() => new YmapWriter().Write(plain, bare, extents));
        var description = YmapWriter.CarGeneratorDescription(file);
        Assert.NotNull(description);
        var added = new YmapReader().Read(new YmapWriter().Write(plain, bare, extents, description).Content);
        Assert.Single(added.Entities);
        Assert.Equal(spot with { SourceIndex = 0 }, Assert.Single(added.CarGenerators));
    }

    [Fact]
    public void A_project_map_with_parking_spots_only_is_built()
    {
        var template = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd));
        var library = Data.Maps.MapLibrary.Create((new YmapReader().Read(template), "template.ymap"));
        var map = library.CreateCustomMap("proj_props.ymap");
        var spot = new Data.Maps.CarGenerator { Position = new Vector3(-30, 40, 5), Orientation = new Vector2(4, 0), Width = 2.5f, Flags = 0xE60 };
        Editor.CarGeneratorAddEdit.Apply(map, spot, "test");
        Editor.CarGeneratorAddEdit.Apply(map, spot with { Position = new Vector3(0, 0, 1) }, "test").Undo();
        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);

        Assert.Throws<InvalidDataException>(() => new YmapWriter().Build(template, map, "proj_props", extents));
        var description = YmapWriter.CarGeneratorDescription(BuildYmap((Vector3.Zero, Vector2.UnitX, 1f, 0)));
        var written = new YmapReader().Read(new YmapWriter().Build(template, map, "proj_props", extents, carGenerator: description).Content);

        Assert.Empty(written.Entities);
        Assert.Equal(spot with { SourceIndex = 0 }, Assert.Single(written.CarGenerators));
        Assert.True(written.StreamingExtentsMin.X <= -30 - Editor.MapExtentsCalculator.AddedSpotReach + 0.01f);
        Assert.True(written.EntitiesExtentsMin.X <= written.EntitiesExtentsMax.X);
    }

    [Fact]
    public void A_map_file_without_parking_spots_reads_an_empty_list() =>
        Assert.Empty(new YmapReader().Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd))).CarGenerators);
}
