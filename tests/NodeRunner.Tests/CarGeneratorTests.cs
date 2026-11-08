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
    public void A_map_file_without_parking_spots_reads_an_empty_list() =>
        Assert.Empty(new YmapReader().Read(DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, Data.Maps.LodLevel.OrphanHd))).CarGenerators);
}
