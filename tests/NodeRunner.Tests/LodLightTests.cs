using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

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

    [Fact]
    public void Distant_lights_and_their_details_are_read()
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

        var read = new YmapReader().Read(document.Write().Content);

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
    public void A_map_without_lights_reads_empty_lists()
    {
        var read = new YmapReader().Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd)));
        Assert.Empty(read.DistantLights);
        Assert.Empty(read.LodLights);
    }
}
