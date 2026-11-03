using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

// A model (.ydr) built in memory: one level of detail, one geometry of one triangle (position, normal, texture
// coordinates) drawn with a cut-out shader whose colour texture is "Wall_Brick".
public class DrawableReaderTests
{
    private const int SystemSize = 0x2000;

    [Fact]
    public void Read_GivesTheMeshWithItsShaderAndTexture()
    {
        var models = DrawableReader.Read(Build(), new ResourceHeader(165, MetaDocument.PageFlags(1, SystemSize), 0), "prop_test", ".ydr");

        var model = Assert.Single(models).Value;
        Assert.Equal(Joaat.Hash("prop_test"), model.NameHash);
        Assert.Equal((new Vector3(-1), new Vector3(1)), (model.BoundsMin, model.BoundsMax));
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)], mesh.Positions);
        Assert.All(mesh.Normals, n => Assert.Equal(Vector3.UnitZ, n));
        Assert.Equal(new Vector2(1, 0), mesh.TexCoords[1]);
        Assert.All(mesh.Colors, c => Assert.Equal(Vector4.One, c));
        Assert.Equal([0, 1, 2], mesh.Indices.Select(i => (int)i));
        Assert.Equal("wall_brick", mesh.DiffuseTextureName);
        Assert.Equal(MeshBlendMode.Cutout, mesh.BlendMode);
        Assert.Null(mesh.Terrain);
    }

    private static byte[] Build()
    {
        var d = new byte[SystemSize + 0x200];
        void P(int at, int target, bool graphics = false) =>
            BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(at), (graphics ? 0x60000000UL : 0x50000000UL) + (ulong)target);
        void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(at), v);
        void U16(int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), (ushort)v);
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v);

        // Drawable: shader group, bounds, high level list (also the start of the levels block).
        P(0x10, 0x200);
        F(0x30, -1); F(0x34, -1); F(0x38, -1);
        F(0x40, 1); F(0x44, 1); F(0x48, 1);
        P(0x50, 0x300);
        P(0xA0, 0x300);

        // Level list (pointer, count, capacity) -> one model -> one geometry using shader 0.
        P(0x300, 0x310);
        U16(0x308, 1);
        U16(0x30A, 1);
        P(0x310, 0x320);
        P(0x328, 0x360);
        U16(0x330, 1);
        P(0x340, 0x368);
        P(0x360, 0x400);

        // Shader group -> shader "default", cut out (bucket 3), one texture parameter.
        P(0x210, 0x240);
        U16(0x218, 1);
        P(0x240, 0x260);
        P(0x260, 0x2A0);
        U32(0x268, Joaat.Hash("default"));
        d[0x270] = 1;
        d[0x271] = 3;
        P(0x2A8, 0x600);
        U32(0x2B0, Joaat.Hash("DiffuseSampler"));
        P(0x600 + 0x28, 0x640);
        Encoding.ASCII.GetBytes("Wall_Brick\0").CopyTo(d, 0x640);

        // Geometry -> vertex buffer (3 vertices of 32 bytes) and index buffer (3 indices).
        P(0x418, 0x500);
        P(0x438, 0x580);
        U16(0x508, 32);
        P(0x510, 0, graphics: true);
        U32(0x518, 3);
        P(0x530, 0x5C0);
        U32(0x588, 3);
        P(0x590, 0x100, graphics: true);
        // Declaration: position (bit 0, float3), normal (bit 3, float3), texture coordinates (bit 6, float2).
        U32(0x5C0, 1 | 8 | 64);
        U16(0x5C4, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x5C8), 0x6UL | 0x6UL << 12 | 0x5UL << 24);

        Vector2[] corners = [new(0, 0), new(1, 0), new(0, 1)];
        for (int v = 0; v < 3; v++)
        {
            int at = SystemSize + v * 32;
            F(at, corners[v].X);
            F(at + 4, corners[v].Y);
            F(at + 20, 1);
            F(at + 24, corners[v].X);
            F(at + 28, corners[v].Y);
            U16(SystemSize + 0x100 + v * 2, v);
        }
        return d;
    }
}
