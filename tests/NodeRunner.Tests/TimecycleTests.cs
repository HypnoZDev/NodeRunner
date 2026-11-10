using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class TimecycleTests
{
    private const uint TimecycleStructure = 0x674F9350;

    // A test map with the given timecycle zones (CTimeCycleModifier, 64 bytes each).
    internal static byte[] BuildYmap(params (uint Name, Vector3 Min, Vector3 Max, float Percentage, float Range, uint Start, uint End)[] zones)
    {
        var document = MetaDocument.Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd)));
        document.Structures.Add(DeletionTests.Structure(TimecycleStructure, 64));
        var items = new byte[zones.Length * 64];
        for (int i = 0; i < zones.Length; i++)
        {
            var c = items.AsSpan(i * 64);
            var (name, min, max, percentage, range, start, end) = zones[i];
            BinaryPrimitives.WriteUInt32LittleEndian(c[8..], name);
            for (int k = 0; k < 3; k++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(c[(16 + k * 4)..], min[k]);
                BinaryPrimitives.WriteSingleLittleEndian(c[(32 + k * 4)..], max[k]);
            }
            BinaryPrimitives.WriteSingleLittleEndian(c[28..], float.NaN);
            BinaryPrimitives.WriteSingleLittleEndian(c[44..], float.NaN);
            BinaryPrimitives.WriteSingleLittleEndian(c[48..], percentage);
            BinaryPrimitives.WriteSingleLittleEndian(c[52..], range);
            BinaryPrimitives.WriteUInt32LittleEndian(c[56..], start);
            BinaryPrimitives.WriteUInt32LittleEndian(c[60..], end);
        }
        document.Blocks.Add(new MetaDocument.Block(TimecycleStructure, items));
        var map = document.Blocks[document.RootBlock].Data;
        BinaryPrimitives.WriteUInt64LittleEndian(map.AsSpan(224), MetaDocument.Pointer(document.Blocks.Count - 1, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(232), (ushort)zones.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(234), (ushort)zones.Length);
        return document.Write().Content;
    }

    [Fact]
    public void Timecycle_zones_are_read_from_a_map_file()
    {
        var file = BuildYmap((0x9CFCA62E, new Vector3(-10, 0, 1), new Vector3(10, 20, 5), 50f, 5f, 0, 23),
            (0x431ED452, new Vector3(100, 100, 0), new Vector3(110, 105, 3), 100f, 1f, 20, 6));
        var map = new YmapReader().Read(file);

        Assert.Equal(2, map.TimecycleModifiers.Count);
        var first = map.TimecycleModifiers[0];
        Assert.Equal(0x9CFCA62Eu, first.NameHash);
        Assert.Equal(new Vector3(-10, 0, 1), first.BoundsMin);
        Assert.Equal(new Vector3(10, 20, 5), first.BoundsMax);
        Assert.Equal(new Vector3(0, 10, 3), first.Center);
        Assert.Equal(50f, first.Percentage);
        Assert.Equal(5f, first.Range);
        Assert.Equal(23u, first.EndHour);
        var second = map.TimecycleModifiers[1];
        Assert.Equal(1, second.SourceIndex);
        Assert.Equal((20u, 6u), (second.StartHour, second.EndHour));
    }

    [Fact]
    public void Timecycle_zones_are_kept_when_the_map_file_is_written_again()
    {
        var file = BuildYmap((0x9CFCA62E, new Vector3(-10, 0, 1), new Vector3(10, 20, 5), 50f, 5f, 0, 23));
        var map = new YmapReader().Read(file);
        map.Entities[0].SetPlacement(new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One);
        var written = new YmapReader().Read(new YmapWriter().Write(file, map, Editor.MapExtentsCalculator.Compute(map, _ => null)).Content);
        Assert.Equal(map.TimecycleModifiers, written.TimecycleModifiers);
    }

    [Fact]
    public void A_changed_timecycle_zone_is_written_back_and_held_by_the_streaming_box()
    {
        var file = BuildYmap((0x9CFCA62E, new Vector3(-10, 0, 1), new Vector3(10, 20, 5), 50f, 5f, 0, 23),
            (0x431ED452, new Vector3(100, 100, 0), new Vector3(110, 105, 3), 100f, 1f, 20, 6));
        var map = new YmapReader().Read(file);
        var changed = map.TimecycleModifiers[1] with
        {
            NameHash = 0x12345678, BoundsMin = new Vector3(500, 600, -5), BoundsMax = new Vector3(520, 640, 10),
            Percentage = 30f, Range = 12f, StartHour = 6, EndHour = 21,
        };
        var edit = new Editor.TimecycleEdit(map, 1, map.TimecycleModifiers[1], changed, "test");
        edit.Redo();
        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);
        var written = new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content);

        Assert.Equal(map.TimecycleModifiers[0], written.TimecycleModifiers[0]);
        Assert.Equal(changed, written.TimecycleModifiers[1]);
        Assert.True(written.StreamingExtentsMax.X >= 520 && written.StreamingExtentsMax.Y >= 640 && written.StreamingExtentsMin.Z <= -5);

        edit.Undo();
        Assert.Equal(new YmapReader().Read(file).TimecycleModifiers,
            new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content).TimecycleModifiers);
    }

    [Fact]
    public void A_world_sized_timecycle_zone_does_not_grow_the_streaming_box()
    {
        var file = BuildYmap((0x9CFCA62E, new Vector3(-1e30f), new Vector3(1e30f), 100f, 0f, 0, 23));
        var map = new YmapReader().Read(file);
        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);
        Assert.True(extents.StreamingMax.X < 1e6f);
    }

    [Fact]
    public void Timecycle_zones_are_deleted_and_added()
    {
        var file = BuildYmap((0x9CFCA62E, new Vector3(-10, 0, 1), new Vector3(10, 20, 5), 50f, 5f, 0, 23),
            (0x431ED452, new Vector3(100, 100, 0), new Vector3(110, 105, 3), 100f, 1f, 20, 6));
        var map = new YmapReader().Read(file);
        var original = map.TimecycleModifiers.ToList();
        var zone = new Data.Maps.TimecycleModifier { NameHash = 0x55, BoundsMin = new Vector3(0, 0, 0), BoundsMax = new Vector3(4, 4, 4), Percentage = 100, Range = 5, EndHour = 23 };
        var deleted = Editor.TimecycleDeleteEdit.Apply(map, 0, "test");
        Editor.TimecycleAddEdit.Apply(map, zone, "test");
        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);

        var written = new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content);
        Assert.Equal([original[1] with { SourceIndex = 0 }, zone with { SourceIndex = 1 }], written.TimecycleModifiers);

        // All deleted, then undone.
        var last = Editor.TimecycleDeleteEdit.Apply(map, 1, "test");
        var added = Editor.TimecycleDeleteEdit.Apply(map, 2, "test");
        Assert.Empty(new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content).TimecycleModifiers);
        added.Undo();
        last.Undo();
        deleted.Undo();
        Assert.Equal(original.Append(zone with { SourceIndex = 2 }),
            new YmapReader().Read(new YmapWriter().Write(file, map, extents).Content).TimecycleModifiers);
    }

    [Fact]
    public void A_project_map_with_a_timecycle_zone_and_a_parking_spot_is_built()
    {
        var template = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd));
        var library = Data.Maps.MapLibrary.Create((new YmapReader().Read(template), "template.ymap"));
        var map = library.CreateCustomMap("proj_props.ymap");
        var zone = new Data.Maps.TimecycleModifier { NameHash = 0x55, BoundsMin = new Vector3(-30, 40, 0), BoundsMax = new Vector3(-20, 50, 6), Percentage = 100, Range = 5, EndHour = 23 };
        var spot = new Data.Maps.CarGenerator { Position = new Vector3(0, 0, 1), Orientation = new Vector2(4, 0), Width = 2.5f, Flags = 0xE60 };
        Editor.TimecycleAddEdit.Apply(map, zone, "test");
        Editor.CarGeneratorAddEdit.Apply(map, spot, "test");
        var extents = Editor.MapExtentsCalculator.Compute(map, _ => null);
        var borrowed = new[]
        {
            YmapWriter.TimecycleDescription(BuildYmap((1, Vector3.Zero, Vector3.One, 1f, 1f, 0, 23)))!,
            YmapWriter.CarGeneratorDescription(CarGeneratorTests.BuildYmap((Vector3.Zero, Vector2.UnitX, 1f, 0)))!,
        };

        Assert.Throws<InvalidDataException>(() => new YmapWriter().Build(template, map, "proj_props", extents, borrowed: borrowed[1..]));
        var content = new YmapWriter().Build(template, map, "proj_props", extents, borrowed: borrowed).Content;
        var written = new YmapReader().Read(content);
        Assert.Equal(zone with { SourceIndex = 0 }, Assert.Single(written.TimecycleModifiers));
        Assert.Equal(spot with { SourceIndex = 0 }, Assert.Single(written.CarGenerators));
        Assert.True(written.StreamingExtentsMin.X <= -30 && written.StreamingExtentsMax.Y >= 50);

        // A new zone gets the game's value in the 4th float of its corners.
        var document = MetaDocument.Read(content);
        var block = document.Blocks.First(b => b.Structure == TimecycleStructure);
        Assert.Equal(0x7F800001u, BinaryPrimitives.ReadUInt32LittleEndian(block.Data.AsSpan(28)));
        Assert.Equal(0x7F800001u, BinaryPrimitives.ReadUInt32LittleEndian(block.Data.AsSpan(44)));
    }

    [Fact]
    public void A_map_file_without_timecycle_zones_reads_an_empty_list() =>
        Assert.Empty(new YmapReader().Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd))).TimecycleModifiers);
}
