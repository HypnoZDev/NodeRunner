using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Native.Classic;
using NodeRunner.Data.Water;

namespace NodeRunner.Tests;

// Water of the first trilogy: the GTA III / Vice City grid and the San Andreas text file, written by hand.
public class ClassicWaterTests
{
    [Fact]
    public void Grid_JoinsCellsOfARowAtTheSameHeight()
    {
        const int fineStart = 4 + 48 * 4 + 48 * 16 + 64 * 64;
        var data = new byte[fineStart + 128 * 128];
        BinaryPrimitives.WriteInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), 63f);
        data.AsSpan(fineStart).Fill(0x80);
        // Row y = 0: cells x = 0 and 1 at height 0, cell 5 at height 63.
        data[fineStart + 0 * 128] = 0;
        data[fineStart + 1 * 128] = 0;
        data[fineStart + 5 * 128] = 1;
        var map = new WaterMap();

        ClassicWater.ReadGrid(map, data, new Vector2(-2048, -2048));

        Assert.Equal(2, map.Quads.Count);
        Assert.Equal(new WaterQuad(-2048, -1984, -2048, -2016, 0, WaterQuadShape.Rectangle), map.Quads[0]);
        Assert.Equal(63f, map.Quads[1].Z);
        Assert.Equal(new Vector4(-2048, -2048, 2048, 2048), map.OceanBounds);
        Assert.Equal(0f, map.OceanHeight);
    }

    [Fact]
    public void SanAndreas_ReadsQuadsAndTriangles()
    {
        static string Corner(float x, float y) => $"{x} {y} 5.0 0 0 0.1 0.2   ";
        var map = new WaterMap();

        ClassicWater.ReadSanAndreas(map, "processed\n"
            + Corner(0, 0) + Corner(10, 0) + Corner(0, 10) + Corner(10, 10) + "1\n"
            + Corner(20, 0) + Corner(30, 0) + Corner(20, 10) + "1\n");

        Assert.Equal(2, map.Quads.Count);
        Assert.Contains(new WaterQuad(0, 10, 0, 10, 5, WaterQuadShape.Rectangle), map.Quads);
        Assert.Contains(new WaterQuad(20, 30, 0, 10, 5, WaterQuadShape.WithoutNorthEast), map.Quads);
    }
}
