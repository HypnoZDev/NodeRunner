using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class LodLightTests
{
    // Block types of the game's files for arrays of Vector3, float / uint, byte.
    private const uint Vector3Block = 0xE2CBCFD4;
    private const uint FloatBlock = 0x21;
    private const uint UIntBlock = 0x15;
    private const uint ByteBlock = 0x11;

    private static int AddBlock(MetaDocument document, uint structure, byte[] data)
    {
        document.Blocks.Add(new MetaDocument.Block(structure, data));
        return document.Blocks.Count - 1;
    }

    private static void SetArray(byte[] map, int field, int block, int count)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(map.AsSpan(field), MetaDocument.Pointer(block, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(field + 8), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(field + 10), (ushort)count);
    }

    private static byte[] Floats(params float[] values)
    {
        var data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
        return data;
    }

    private static byte[] UInts(params uint[] values)
    {
        var data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), values[i]);
        return data;
    }

    private static byte[] Vectors(params Vector3[] values) => Floats(values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray());

    // A map file with two distant lights and their details (both in the same file, which the format allows).
    private static byte[] BuildLights()
    {
        var document = MetaDocument.Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd)));
        document.Structures.Add(DeletionTests.Structure(Vector3Block, 12));
        var map = document.Blocks[document.RootBlock].Data;

        // Distant lights (CDistantLODLight at 392): positions, colours, 1 street light, category 1.
        SetArray(map, 392 + 8, AddBlock(document, Vector3Block, Vectors(new Vector3(1, 2, 3), new Vector3(4, 5, 6))), 2);
        SetArray(map, 392 + 24, AddBlock(document, UIntBlock, UInts(0xAAFF780A, 0x55DDECE7)), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(392 + 40), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(392 + 42), 1);

        // Details (CLODLight at 256).
        SetArray(map, 256 + 8, AddBlock(document, Vector3Block, Vectors(new Vector3(0, 0, -1), new Vector3(1, 0, 0))), 2);
        SetArray(map, 256 + 24, AddBlock(document, FloatBlock, Floats(12.5f, 3f)), 2);
        SetArray(map, 256 + 40, AddBlock(document, FloatBlock, Floats(8f, 2f)), 2);
        SetArray(map, 256 + 56, AddBlock(document, UIntBlock, UInts(0x08F0003F, 0x04FFFFFF)), 2);
        SetArray(map, 256 + 72, AddBlock(document, UIntBlock, UInts(0x1234, 0x5678)), 2);
        SetArray(map, 256 + 88, AddBlock(document, ByteBlock, [10, 20]), 2);
        SetArray(map, 256 + 104, AddBlock(document, ByteBlock, [30, 40]), 2);
        SetArray(map, 256 + 120, AddBlock(document, ByteBlock, [50, 60]), 2);

        return document.Write().Content;
    }

    [Fact]
    public void Distant_lights_and_their_details_are_read()
    {
        var read = new YmapReader().Read(BuildLights());

        Assert.Equal(2, read.DistantLights.Count);
        var light = read.DistantLights[0];
        Assert.Equal(new Vector3(1, 2, 3), light.Position);
        Assert.Equal((0xFF, 0x78, 0x0A, 0xAA), (light.Red, light.Green, light.Blue, light.Intensity));
        Assert.Equal(1, read.DistantStreetLights);
        Assert.Equal(1, read.DistantCategory);

        Assert.Equal(2, read.LodLights.Count);
        var details = read.LodLights[0];
        Assert.Equal(new Vector3(0, 0, -1), details.Direction);
        Assert.Equal(12.5f, details.Falloff);
        Assert.Equal(8f, details.FalloffExponent);
        Assert.Equal(LodLightType.Spot, details.Type);
        Assert.True(details.IsOnAt(22) && details.IsOnAt(3) && !details.IsOnAt(12));
        Assert.Equal((10, 30, 50), (details.ConeInnerAngle, details.ConeOuterAngleOrCapExt, details.CoronaIntensity));
        Assert.Equal(LodLightType.Point, read.LodLights[1].Type);
        Assert.Equal(0xFFFFFFu, read.LodLights[1].HourMask);
    }

    [Fact]
    public void Edited_lights_are_written_in_place()
    {
        var content = BuildLights();
        var map = new YmapReader().Read(content);
        var before = LodLightValues.Of(map, map, 0);
        var after = new LodLightValues(new DistantLodLight(new Vector3(7, 8, 9), DistantLodLight.Pack(1, 2, 3, 4)),
            before.Details!.Value.WithType(LodLightType.Point).WithHourMask(LodLight.HourRange(18, 8)) with
            {
                Direction = new Vector3(0, 1, 0), Falloff = 20f, FalloffExponent = 16f, ConeInnerAngle = 11, ConeOuterAngleOrCapExt = 22, CoronaIntensity = 33,
            });
        var edit = new LodLightEdit(map, map, 0, before, after, "test");
        edit.Redo();

        var written = new YmapWriter().Write(content, map, MapExtents.Of(map)).Content;
        var read = new YmapReader().Read(written);

        Assert.Equal(map.DistantLights, read.DistantLights);
        Assert.Equal(map.LodLights, read.LodLights);
        Assert.Equal(after.Light, read.DistantLights[0]);
        Assert.Equal(after.Details, read.LodLights[0]);
        Assert.Equal(new YmapReader().Read(content).LodLights[1], read.LodLights[1]);
        Assert.True(read.LodLights[0].IsOnAt(18) && read.LodLights[0].IsOnAt(7) && !read.LodLights[0].IsOnAt(8));
        Assert.Equal(1, read.DistantStreetLights);

        edit.Undo();
        Assert.Equal(before, LodLightValues.Of(map, map, 0));
    }

    [Fact]
    public void A_moved_light_stays_in_the_boxes_of_both_its_files()
    {
        var distant = new MapData
        {
            NameHash = 1,
            Entities = [],
            StreamingExtentsMin = new Vector3(-450),
            StreamingExtentsMax = new Vector3(450),
            DistantLights = [new DistantLodLight(Vector3.Zero, 0)],
        };
        var lod = new MapData { NameHash = 2, ParentHash = 1, Entities = [], StreamingExtentsMin = new Vector3(-450), StreamingExtentsMax = new Vector3(450),
            LodLights = [new LodLight(Vector3.UnitZ, 1, 1, 0, 0, 0, 0, 0)] };

        // Unchanged: the game's boxes are kept.
        Assert.Equal(new Vector3(450), MapExtentsCalculator.Compute(distant, _ => null).StreamingMax);

        distant.SetDistantLight(0, new DistantLodLight(new Vector3(1000, 0, 0), 0));
        var extents = MapExtentsCalculator.Compute(distant, _ => null);
        Assert.Equal(1000 + MapExtentsCalculator.LightReach, extents.StreamingMax.X);
        Assert.Equal(1000, extents.EntitiesMax.X);
        Assert.Equal(1000 + MapExtentsCalculator.LightReach, MapExtentsCalculator.Compute(lod, _ => null, distant.DistantLights).StreamingMax.X);
    }

    [Fact]
    public void Deleted_and_added_lights_are_written_as_new_arrays()
    {
        var content = BuildLights();
        var map = new YmapReader().Read(content);
        // The street light (the first one) deleted, a light added after the other.
        var delete = LodLightDeleteEdit.Apply(map, map, 0, "test");
        var add = LodLightAddEdit.Apply(map, map, new DistantLodLight(new Vector3(9, 9, 9), 0xAAFF9D66),
            new LodLight(-Vector3.UnitZ, 10f, 64f, 0x08F0003F, 0x99, 14, 85, 7), "test");

        var read = new YmapReader().Read(new YmapWriter().Write(content, map, MapExtents.Of(map)).Content);
        Assert.Equal([map.DistantLights[1], map.DistantLights[2]], read.DistantLights);
        Assert.Equal([map.LodLights[1], map.LodLights[2]], read.LodLights);
        Assert.Equal(0, read.DistantStreetLights);
        Assert.Equal(1, read.DistantCategory);

        // Undone: back as it was.
        add.Undo();
        delete.Undo();
        var again = new YmapReader().Read(new YmapWriter().Write(content, map, MapExtents.Of(map)).Content);
        Assert.Equal(new YmapReader().Read(content).DistantLights, again.DistantLights);
        Assert.Equal(new YmapReader().Read(content).LodLights, again.LodLights);
        Assert.Equal(1, again.DistantStreetLights);

        // All deleted: empty arrays.
        LodLightDeleteEdit.Apply(map, map, 0, "test");
        LodLightDeleteEdit.Apply(map, map, 1, "test");
        var empty = new YmapReader().Read(new YmapWriter().Write(content, map, MapExtents.Of(map)).Content);
        Assert.Empty(empty.DistantLights);
        Assert.Empty(empty.LodLights);
    }

    [Fact]
    public void A_light_file_of_the_project_is_built_from_a_game_light_file()
    {
        var template = BuildLights();
        var distant = new MapData { NameHash = 0xABCD, Entities = [], IsCustom = true };
        var lod = new MapData { NameHash = 0xBCDE, ParentHash = 0xABCD, Entities = [], IsCustom = true };
        LodLightAddEdit.Apply(distant, lod, new DistantLodLight(new Vector3(100, 200, 30), 0xAAFF9D66),
            new LodLight(-Vector3.UnitZ, 10f, 64f, 0x08F0003F, 0x99, 14, 85, 7), "test");

        var extents = MapExtentsCalculator.Compute(distant, _ => null);
        Assert.Equal(new Vector3(100, 200, 30) - new Vector3(MapExtentsCalculator.LightReach), extents.StreamingMin);
        var read = new YmapReader().Read(new YmapWriter().BuildLights(template, distant, "proj_distlodlights", extents).Content);
        Assert.Equal(0xABCDu, read.NameHash);
        Assert.Equal(distant.DistantLights, read.DistantLights);
        Assert.Equal(0, read.DistantStreetLights);
        Assert.Equal(new Vector3(100, 200, 30), read.EntitiesExtentsMin);

        var lodRead = new YmapReader().Read(new YmapWriter().BuildLights(template, lod, "proj_lodlights",
            MapExtentsCalculator.Compute(lod, _ => null, distant.KeptDistantLights)).Content);
        Assert.Equal(0xABCDu, lodRead.ParentHash);
        Assert.Equal(lod.LodLights, lodRead.LodLights);
    }

    [Theory]
    [InlineData(20, 6, 0xF0003Fu)]
    [InlineData(6, 20, 0x0FFFC0u)]
    [InlineData(5, 5, 0xFFFFFFu)]
    public void Hour_ranges_go_across_midnight(int start, int end, uint mask) => Assert.Equal(mask, LodLight.HourRange(start, end));

    [Fact]
    public void A_map_without_lights_reads_empty_lists()
    {
        var read = new YmapReader().Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd)));
        Assert.Empty(read.DistantLights);
        Assert.Empty(read.LodLights);
    }
}
