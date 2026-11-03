using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Native.Collisions;

namespace NodeRunner.Tests;

/// <summary>
/// A collision file built in memory: a composite holding a geometry with a search tree (3 triangles, placed 100 m
/// along X) and a lone box. Both transform arrays point to the same data, as in some of the game's files.
/// </summary>
public class YbnReaderTests
{
    private const int Composite = 0x000, ChildPointers = 0x200, Transforms = 0x240, ChildBoxes = 0x2C0, ChildFlags = 0x300;
    private const int Geometry = 0x400, Polygons = 0x600, Vertices = 0x640, Materials = 0x680, PolygonMaterials = 0x6A0;
    private const int Tree = 0x700, TreeNodes = 0x780, TreeTrees = 0x7E0, Box = 0x800;

    [Fact]
    public void Read_PlacesTrianglesAndKeepsPolygonNumbers()
    {
        var mesh = YbnReader.Read(Build())!;

        Assert.Equal(3 + 12, mesh.TriangleCount);
        Assert.Equal([0, 1, 2], mesh.Polygons.Take(3));
        Assert.All(mesh.Polygons.Skip(3), p => Assert.Equal(-1, p));
        Assert.Equal([7, 9, 7], mesh.Materials.Take(3));
        Assert.Equal(new Vector3(101, 0, 0), mesh.Vertices[mesh.Indices[1]]);
        Assert.Equal(4, mesh.Materials[3]);
    }

    [Fact]
    public void RemovePolygons_KeepsTheOthersWithAValidTree()
    {
        var data = YbnReader.RemovePolygons(Build(), new HashSet<int> { 1 });
        var mesh = YbnReader.Read(data)!;

        Assert.Equal(2 + 12, mesh.TriangleCount);
        Assert.Equal([7, 7], mesh.Materials.Take(2));
        Assert.Equal(2u, U32(data, Geometry + 0xD4));
        // Neighbour links: the link to the removed triangle is gone, none points past the end.
        for (int p = 0; p < 2; p++)
        {
            for (int edge = 10; edge <= 14; edge += 2)
            {
                ushort neighbour = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Polygons + p * 16 + edge));
                Assert.True(neighbour == 0xFFFF || neighbour < 2);
            }
        }
        // One leaf holding both triangles, one tree.
        Assert.Equal(1u, U32(data, Tree + 0x08));
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(TreeNodes + 12)));
        Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(TreeNodes + 14)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Tree + 0x78)));
    }

    [Fact]
    public void RemovePolygons_DropsAGeometryLeftEmpty()
    {
        var data = YbnReader.RemovePolygons(Build(), new HashSet<int> { 0, 1, 2 });
        var mesh = YbnReader.Read(data)!;

        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Composite + 0xA0)));
        Assert.Equal(12, mesh.TriangleCount);
        Assert.All(mesh.Polygons, p => Assert.Equal(-1, p));
        // The box keeps its own placement (the shared transform array was packed once).
        Assert.Equal(new Vector3(-1), mesh.BoundsMin);
        Assert.Equal(new Vector3(1), mesh.BoundsMax);
    }

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

    private static byte[] Build()
    {
        var d = new byte[0x2000];
        void P(int at, int target) => BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(at), 0x50000000UL + (ulong)target);
        void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), v);
        void V3(int at, Vector3 v) { F(at, v.X); F(at + 4, v.Y); F(at + 8, v.Z); }
        void S16(int at, int v) => BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(at), (short)v);
        void U16(int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), (ushort)v);

        d[Composite + 0x10] = 10;
        P(Composite + 0x70, ChildPointers);
        P(Composite + 0x78, Transforms);
        P(Composite + 0x80, Transforms);
        P(Composite + 0x88, ChildBoxes);
        P(Composite + 0x90, ChildFlags);
        P(Composite + 0x98, ChildFlags);
        U16(Composite + 0xA0, 2);
        U16(Composite + 0xA2, 2);
        P(ChildPointers, Geometry);
        P(ChildPointers + 8, Box);
        // Transforms: rows of 3 floats + 1 word; geometry moved 100 m along X, box in place.
        for (int i = 0; i < 2; i++)
        {
            int m = Transforms + i * 64;
            F(m, 1);
            F(m + 20, 1);
            F(m + 40, 1);
            F(m + 48, i == 0 ? 100 : 0);
        }
        V3(ChildBoxes, new Vector3(100, 0, 0));
        V3(ChildBoxes + 16, new Vector3(101, 1, 0));
        V3(ChildBoxes + 32, new Vector3(-1));
        V3(ChildBoxes + 48, new Vector3(1));

        // Geometry with tree: 4 vertices on a 1 m square (shorts x 0.01), 3 triangles.
        d[Geometry + 0x10] = 8;
        P(Geometry + 0x88, Polygons);
        V3(Geometry + 0x90, new Vector3(0.01f));
        P(Geometry + 0xB0, Vertices);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Geometry + 0xD0), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Geometry + 0xD4), 3);
        P(Geometry + 0xF0, Materials);
        P(Geometry + 0x118, PolygonMaterials);
        d[Geometry + 0x120] = 2;
        P(Geometry + 0x130, Tree);
        int[][] squares = [[0, 0], [100, 0], [0, 100], [100, 100]];
        for (int v = 0; v < 4; v++)
        {
            S16(Vertices + v * 6, squares[v][0]);
            S16(Vertices + v * 6 + 2, squares[v][1]);
        }
        int[][] triangles = [[0, 1, 2, 1, 0xFFFF, 0xFFFF], [1, 3, 2, 0, 2, 0xFFFF], [1, 2, 3, 1, 0xFFFF, 0xFFFF]];
        for (int t = 0; t < 3; t++)
        {
            for (int k = 0; k < 6; k++)
                U16(Polygons + t * 16 + 4 + k * 2, triangles[t][k]);
        }
        d[Materials] = 7;
        d[Materials + 8] = 9;
        d[PolygonMaterials + 1] = 1;

        // Its tree: one leaf with the 3 triangles, room for 3 nodes and 1 tree.
        P(Tree, TreeNodes);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Tree + 0x08), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(Tree + 0x0C), 3);
        V3(Tree + 0x50, new Vector3(100));
        V3(Tree + 0x60, new Vector3(0.01f));
        P(Tree + 0x70, TreeTrees);
        U16(Tree + 0x78, 1);
        U16(Tree + 0x7A, 1);
        S16(TreeNodes + 6, 100);
        S16(TreeNodes + 8, 100);
        S16(TreeNodes + 14, 3);
        S16(TreeTrees + 14, 1);

        // Lone box.
        d[Box + 0x10] = 3;
        V3(Box + 0x20, new Vector3(1));
        V3(Box + 0x30, new Vector3(-1));
        d[Box + 0x4C] = 4;
        return d;
    }
}
