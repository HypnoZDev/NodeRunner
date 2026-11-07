using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class OcclusionTests
{
    private const uint MapDataStructure = 3545841574; // CMapData.
    private const uint BoxStructure = 0x3A282E1D; // CBoxOccluder.

    // A 10 x 10 x 8 m building standing at (100, 200, 30), not turned.
    private static readonly CutVolume Building = new(Matrix4x4.CreateTranslation(-100, -200, -30), new Vector3(-5, -5, 0), new Vector3(5, 5, 8));

    [Fact]
    public void Triangle_inside_or_crossing_the_box_touches_it()
    {
        Assert.True(OcclusionRemoval.Touches(Building, [new(99, 199, 31), new(101, 199, 31), new(100, 201, 35)]));
        // Large wall across the building, no corner inside.
        Assert.True(OcclusionRemoval.Touches(Building, [new(80, 200, 20), new(120, 200, 20), new(100, 200, 60)]));
    }

    [Fact]
    public void Triangle_beside_the_box_does_not_touch_it()
    {
        Assert.False(OcclusionRemoval.Touches(Building, [new(106, 190, 31), new(106, 210, 31), new(106, 200, 37)]));
        // Its box overlaps the building's, but the triangle passes by a corner.
        Assert.False(OcclusionRemoval.Touches(Building, [new(104, 207, 32), new(107, 204, 32), new(107, 207, 32)]));
    }

    [Fact]
    public void Neighbour_merely_touching_the_wall_stays()
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = new Vector3(100, 200, 30) };
        var archetype = new Archetype { NameHash = 1, BoundsMin = new Vector3(-5, -5, 0), BoundsMax = new Vector3(5, 5, 8) };
        var volume = OcclusionRemoval.VolumeOf(entity, archetype)!.Value;
        // The neighbour's wall is on the building's side (x = 105).
        Assert.False(OcclusionRemoval.Touches(volume, [new(105, 190, 31), new(105, 210, 31), new(105, 200, 37)]));
        Assert.True(OcclusionRemoval.Touches(volume, [new(104, 190, 31), new(104, 210, 31), new(104, 200, 37)]));
    }

    [Fact]
    public void Flat_objects_keep_their_occluders()
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = new Vector3(100, 200, 30) };
        var slab = new Archetype { NameHash = 1, BoundsMin = new Vector3(-50, -50, 0), BoundsMax = new Vector3(50, 50, 1) };
        Assert.Null(OcclusionRemoval.VolumeOf(entity, slab));
    }

    [Fact]
    public void Plan_takes_the_boxes_and_triangles_in_the_building()
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = new Vector3(100, 200, 30) };
        var archetype = new Archetype { NameHash = 1, BoundsMin = new Vector3(-5, -5, 0), BoundsMax = new Vector3(5, 5, 8) };
        var occluders = new MapOccluders(
            [new OccluderBox(0, new Vector3(100, 200, 34), new Vector3(4, 4, 4), 0.3f), new OccluderBox(1, new Vector3(150, 200, 34), new Vector3(4, 4, 4), 0)],
            [new OccluderModel(0, [new(98, 198, 31), new(102, 198, 31), new(100, 202, 37), new(150, 200, 31), new(152, 200, 31), new(150, 202, 37)], [0, 1, 2, 3, 4, 5])]);

        var cuts = OcclusionRemoval.Plan([("x64/levels/gta5/sample_occl_00.ymap", occluders)], [(entity, archetype)]);

        var cut = Assert.Single(cuts);
        Assert.Equal("sample_occl_00.ymap", cut.FileName);
        Assert.Equal([0], cut.Boxes);
        Assert.Equal([0], cut.Triangles[0]);
        Assert.Equal((12 + 1) * 3, cut.Preview.Count);
    }

    [Fact]
    public void Plan_takes_a_box_holding_the_whole_building()
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = new Vector3(100, 200, 30) };
        var archetype = new Archetype { NameHash = 1, BoundsMin = new Vector3(-5, -5, 0), BoundsMax = new Vector3(5, 5, 8) };
        var occluders = new MapOccluders([new OccluderBox(0, new Vector3(100, 200, 34), new Vector3(40, 40, 40), 0)], []);
        Assert.Single(OcclusionRemoval.Plan([("big_occl.ymap", occluders)], [(entity, archetype)]));
    }

    [Fact]
    public void Removes_boxes_and_triangles_and_keeps_the_rest()
    {
        var file = new OccluderFile();
        var source = SampleMap();
        var before = file.ReadOccluders(source);
        Assert.Equal(3, before.Boxes.Count);
        Assert.Equal(new Vector3(10, -20, 30.5f), before.Boxes[1].Center);
        Assert.Equal(new Vector3(4, 2, 6), before.Boxes[1].Size);
        Assert.Equal(MathF.PI / 2, before.Boxes[1].Angle, 3);
        Assert.Equal(2, before.Models.Count);
        Assert.Equal(2, before.Models[0].TriangleCount);

        var written = file.RemoveOccluders(source, new HashSet<int> { 0 },
            new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { 0 }, [1] = new HashSet<int> { 0 } });
        var after = file.ReadOccluders(written.Content);

        Assert.Equal(2, after.Boxes.Count);
        Assert.Equal(before.Boxes[1] with { Index = 0 }, after.Boxes[0]);
        Assert.Equal(before.Boxes[2] with { Index = 1 }, after.Boxes[1]);
        // The first mesh keeps its second triangle (3 vertices left), the second mesh had one triangle: gone.
        var model = Assert.Single(after.Models);
        Assert.Equal(1, model.TriangleCount);
        Assert.Equal(3, model.Vertices.Count);
        var kept = before.Models[0];
        Assert.Equal([kept.Vertices[kept.Indices[3]], kept.Vertices[kept.Indices[4]], kept.Vertices[kept.Indices[5]]],
            [model.Vertices[model.Indices[0]], model.Vertices[model.Indices[1]], model.Vertices[model.Indices[2]]]);
    }

    [Fact]
    public void Writes_moved_added_and_deleted_boxes_and_grows_the_extents()
    {
        var file = new OccluderFile();
        var source = SampleMap();
        var before = file.ReadOccluders(source);
        var moved = (before.Boxes[2] with { Center = new Vector3(500, 600, 70), Angle = 0.5f }).Quantized();
        var added = new OccluderBox(-1, new Vector3(-300, 20, 5), new Vector3(10, 12, 14), -1f).Quantized();

        var written = file.WriteOccluders(source, [before.Boxes[1], moved, added],
            new Dictionary<int, IReadOnlySet<int>> { [1] = new HashSet<int> { 0 } });
        var after = file.ReadOccluders(written.Content);

        Assert.Equal(3, after.Boxes.Count);
        Assert.Equal(before.Boxes[1] with { Index = 0 }, after.Boxes[0]);
        Assert.Equal(moved.Center, after.Boxes[1].Center);
        Assert.Equal(moved.Angle, after.Boxes[1].Angle, 4);
        Assert.Equal(added.Size, after.Boxes[2].Size);
        Assert.Equal(added.Angle, after.Boxes[2].Angle, 4);
        Assert.Single(after.Models);

        var mapData = MapDataOf(written.Content);
        Assert.True(BitConverter.ToSingle(mapData, 32) <= -305f); // Streaming min X holds the added box.
        Assert.True(BitConverter.ToSingle(mapData, 48 + 4) >= 600f); // Streaming max Y holds the moved one.
    }

    [Fact]
    public void Builds_a_new_occluder_map_file()
    {
        var file = new OccluderFile();
        OccluderBox[] boxes = [new OccluderBox(0, new Vector3(100, 200, 30), new Vector3(10, 20, 8), 0.25f).Quantized()];

        var built = file.BuildOccluders(SampleMap(), "my_resource_occl", boxes);
        var read = file.ReadOccluders(built.Content);

        var box = Assert.Single(read.Boxes);
        Assert.Equal(boxes[0].Center, box.Center);
        Assert.Equal(boxes[0].Size, box.Size);
        Assert.Empty(read.Models);
        var mapData = MapDataOf(built.Content);
        Assert.Equal(NodeRunner.Data.Hashing.Joaat.Hash("my_resource_occl"), BitConverter.ToUInt32(mapData, 8));
        Assert.Equal(32u, BitConverter.ToUInt32(mapData, 20));
    }

    private static byte[] MapDataOf(byte[] content)
    {
        var document = MetaDocument.Read(content);
        return document.Blocks[document.RootBlock].Data;
    }

    private static byte[] SampleMap()
    {
        var document = new MetaDocument { Name = "sample_occl", RootBlock = 0 };
        document.Structures.Add(new MetaDocument.StructureInfo(new byte[0x20], new byte[0x30]));
        var boxInfo = new byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(boxInfo, BoxStructure);
        document.Structures.Add(new MetaDocument.StructureInfo(boxInfo, new byte[0x30]));
        var mapData = new byte[512];
        var boxes = new byte[3 * 16];
        short[][] values = [[0, 0, 0, 32767, 8, 8, 8, 0], [40, -80, 122, 32767, 16, 8, 24, 0], [400, 400, 40, 0, 4, 4, 4, 32767]];
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 8; k++)
                BinaryPrimitives.WriteInt16LittleEndian(boxes.AsSpan(i * 16 + k * 2), values[i][k]);

        Vector3[] quad = [new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4)];
        byte[] quadIndices = [0, 1, 2, 0, 2, 3];
        Vector3[] single = [new(10, 10, 0), new(12, 10, 0), new(10, 10, 3)];
        byte[] singleIndices = [0, 1, 2];
        var data = new List<byte>();
        int quadOffset = data.Count;
        AddMesh(data, quad, quadIndices);
        int singleOffset = data.Count;
        AddMesh(data, single, singleIndices);

        var models = new byte[2 * 64];
        WriteModel(models.AsSpan(0, 64), quad, quadIndices, 3, quadOffset);
        WriteModel(models.AsSpan(64, 64), single, singleIndices, 3, singleOffset);

        document.Blocks.Add(new MetaDocument.Block(MapDataStructure, mapData));
        document.Blocks.Add(new MetaDocument.Block(BoxStructure, boxes));
        document.Blocks.Add(new MetaDocument.Block(1002, models));
        document.Blocks.Add(new MetaDocument.Block(0x11, data.ToArray()));
        WriteArray(mapData, 128, 1, 3);
        WriteArray(mapData, 144, 2, 2);
        return document.Write().Content;
    }

    private static void AddMesh(List<byte> data, Vector3[] vertices, byte[] indices)
    {
        foreach (var v in vertices)
        {
            var bytes = new byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), v.Z);
            data.AddRange(bytes);
        }
        data.AddRange(indices);
    }

    private static void WriteModel(Span<byte> item, Vector3[] vertices, byte[] indices, int dataBlock, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(item[32..], (uint)(vertices.Length * 12 + indices.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(item[40..], MetaDocument.Pointer(dataBlock, offset));
        BinaryPrimitives.WriteUInt16LittleEndian(item[48..], (ushort)(vertices.Length * 12));
        BinaryPrimitives.WriteUInt16LittleEndian(item[50..], (ushort)(indices.Length / 3 | 0x8000));
    }

    private static void WriteArray(byte[] mapData, int field, int block, int count)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(mapData.AsSpan(field), MetaDocument.Pointer(block, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(mapData.AsSpan(field + 8), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(mapData.AsSpan(field + 10), (ushort)count);
    }
}
