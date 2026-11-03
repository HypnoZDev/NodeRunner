using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Native.Paths;
using NodeRunner.Data.Paths;

namespace NodeRunner.Tests;

public class YndReaderTests
{
    // Synthetic file: header at 0, 2 nodes at 0x70, 1 link at 0xC0.
    internal static byte[] BuildSampleYnd()
    {
        var data = new byte[0xC8];
        var span = data.AsSpan();

        BinaryPrimitives.WriteUInt64LittleEndian(span[0x10..], 0x50000070); // nodes pointer
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], 2);          // node count
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], 1);          // vehicle nodes
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x20..], 1);          // pedestrian nodes
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x28..], 0x500000C0); // links pointer
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x30..], 1);          // link count

        // Node 0: area 529, node 0, at (100.25, -200.5, 30.5), highway, 1 link starting at 0.
        var n0 = span[0x70..];
        BinaryPrimitives.WriteUInt16LittleEndian(n0[16..], 529);
        BinaryPrimitives.WriteUInt16LittleEndian(n0[18..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(n0[20..], 0xDEADBEEF);
        BinaryPrimitives.WriteUInt16LittleEndian(n0[26..], 0);
        BinaryPrimitives.WriteInt16LittleEndian(n0[28..], 401);  // 100.25 * 4
        BinaryPrimitives.WriteInt16LittleEndian(n0[30..], -802); // -200.5 * 4
        BinaryPrimitives.WriteInt16LittleEndian(n0[34..], 976);  // 30.5 * 32
        n0[36] = 0x40;                 // Flags2: highway
        n0[37] = (1 << 3) | (3 << 1);  // 1 link, speed category 3

        // Node 1: pedestrian node, no link.
        var n1 = span[0x98..];
        BinaryPrimitives.WriteUInt16LittleEndian(n1[16..], 529);
        BinaryPrimitives.WriteUInt16LittleEndian(n1[18..], 1);

        // Link 0: to area 530 node 7, 2 lanes forward, 1 backward.
        var l0 = span[0xC0..];
        BinaryPrimitives.WriteUInt16LittleEndian(l0, 530);
        BinaryPrimitives.WriteUInt16LittleEndian(l0[2..], 7);
        l0[6] = (2 << 5) | (1 << 2);
        l0[7] = 12;

        return data;
    }

    [Fact]
    public void Reads_nodes_and_links()
    {
        var area = new YndReader().Read(BuildSampleYnd(), 529);

        Assert.Equal(2, area.Nodes.Count);
        Assert.Equal(1, area.VehicleNodeCount);

        var node = area.Nodes[0];
        Assert.Equal(new PathNodeId(529, 0), node.Id);
        Assert.Equal(new Vector3(100.25f, -200.5f, 30.5f), node.Position);
        Assert.Equal(0xDEADBEEFu, node.StreetNameHash);
        Assert.True(node.IsHighway);
        Assert.False(node.IsPedestrian);
        Assert.Equal(3, node.SpeedCategory);
        Assert.True(area.Nodes[1].IsPedestrian);

        var link = Assert.Single(area.LinksOf(node));
        Assert.Equal(new PathNodeId(530, 7), link.Target);
        Assert.Equal(2, link.LanesForward);
        Assert.Equal(1, link.LanesBackward);
        Assert.Equal(12, link.Length);
    }

    [Fact]
    public void Rejects_pointer_outside_file()
    {
        var data = BuildSampleYnd();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x18), 1000); // more nodes than the file holds

        Assert.Throws<InvalidDataException>(() => new YndReader().Read(data, 529));
    }

    [Theory]
    [InlineData(-8192f, -8192f, 0)]
    [InlineData(0f, 0f, 16 * 32 + 16)]
    [InlineData(8191f, 8191f, 1023)]
    [InlineData(9000f, 0f, -1)]
    public void Computes_area_from_position(float x, float y, int expected)
    {
        Assert.Equal(expected, PathNodeArea.AreaIdAt(new Vector2(x, y)));
    }
}
