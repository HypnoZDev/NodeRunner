using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Native.Collisions;

namespace NodeRunner.Tests;

public class CollisionTests
{
    [Fact]
    public void Box_from_four_alternate_corners_has_all_eight_corners()
    {
        // Every other corner of the box (0,0,0)..(2,4,6), as the game stores it, in any order.
        var builder = new CollisionMeshBuilder();
        builder.AddBoxFromCorners(new(2, 4, 6), new(2, 0, 0), new(0, 4, 0), new(0, 0, 6), material: 7);
        var mesh = builder.Build()!;

        Assert.Equal(12, mesh.TriangleCount);
        Assert.All(mesh.Materials, m => Assert.Equal(7, m));
        Assert.Equal(Vector3.Zero, mesh.BoundsMin);
        Assert.Equal(new Vector3(2, 4, 6), mesh.BoundsMax);
        var corners = mesh.Vertices.Select(v => (v.X, v.Y, v.Z)).Distinct().ToList();
        Assert.Equal(8, corners.Count);
    }

    [Fact]
    public void Transformed_box_is_moved()
    {
        var builder = new CollisionMeshBuilder();
        builder.AddBox(new(-1, -1, -1), new(1, 1, 1), Matrix4x4.CreateTranslation(10, 0, 5), material: 0);
        var mesh = builder.Build()!;
        Assert.Equal(new Vector3(9, -1, 4), mesh.BoundsMin);
        Assert.Equal(new Vector3(11, 1, 6), mesh.BoundsMax);
    }

    [Fact]
    public void Capsule_reaches_its_radius_beyond_both_ends()
    {
        var builder = new CollisionMeshBuilder();
        builder.AddCapsule(new(0, 0, 0), new(0, 0, 10), 1f, material: 0);
        var mesh = builder.Build()!;
        Assert.Equal(-1f, mesh.BoundsMin.Z, 3);
        Assert.Equal(11f, mesh.BoundsMax.Z, 3);
        Assert.Equal(1f, mesh.BoundsMax.X, 3);
    }

    [Fact]
    public void Empty_builder_gives_no_mesh()
    {
        Assert.Null(new CollisionMeshBuilder().Build());
    }

    [Fact]
    public void Cache_file_bounds_store_is_read_and_other_modules_are_skipped()
    {
        var data = new List<byte>(new byte[100]); // header (version text)
        void Text(string line) => data.AddRange(Encoding.ASCII.GetBytes(line + "\n"));
        void Module(string name, byte[] content)
        {
            Text(name);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, content.Length);
            data.AddRange(length);
            data.AddRange(content);
        }

        Text("<fileDates>");
        Text("</fileDates>");
        // Binary data that contains a line break: must be skipped, not read as text.
        Module("fwMapDataStore", Enumerable.Repeat((byte)'\n', 64).ToArray());
        var record = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x12345678);
        float[] values = [1, 2, 3, 4, 5, 6];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(4 + i * 4), values[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), 32512);
        Module("BoundsStore", [.. record, .. record]);
        Text("</module>");
        data.Add(0);

        var boxes = new MapCacheReader().ReadCollisionBoxes(data.ToArray());

        Assert.Equal(2, boxes.Count);
        Assert.Equal(new CollisionBox(0x12345678, new(1, 2, 3), new(4, 5, 6)), boxes[0]);
    }

    [Fact]
    public void Truncated_cache_file_is_rejected()
    {
        var data = new byte[100].Concat(Encoding.ASCII.GetBytes("BoundsStore\n")).Concat(new byte[] { 0xFF, 0, 0, 0 }).ToArray();
        Assert.Throws<InvalidDataException>(() => new MapCacheReader().ReadCollisionBoxes(data));
    }

    [Theory]
    [InlineData("dt1_06_0", CollisionKind.Standard)]
    [InlineData("hi@dt1_06_0", CollisionKind.Vehicle)]
    [InlineData("ma@dt1_06_0", CollisionKind.Other)]
    public void Kind_comes_from_the_file_name_prefix(string name, CollisionKind expected)
    {
        Assert.Equal(expected, CollisionCatalog.KindOf(name));
    }
}
