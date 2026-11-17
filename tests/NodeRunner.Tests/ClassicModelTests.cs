using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Classic;
using NodeRunner.Data.Textures;

namespace NodeRunner.Tests;

// RenderWare models (".dff") and texture dictionaries (".txd") built by hand, San Andreas layout.
public class ClassicModelTests
{
    private const uint SanAndreasStamp = 0x1803FFFF;

    [Fact]
    public void Version_IsDecodedFromTheLibraryStamp()
    {
        Assert.Equal(0x36003u, RwChunk.DecodeVersion(SanAndreasStamp));
        Assert.Equal(0x33002u, RwChunk.DecodeVersion(0x0C02FFFF));
        Assert.Equal(0x31000u, RwChunk.DecodeVersion(0x310));
    }

    [Fact]
    public void Clump_GivesMeshesPlacedByTheirFrame()
    {
        var model = ClassicModels.ReadClump(BuildClump(), Joaat.Hash("box"));

        Assert.NotNull(model);
        var mesh = Assert.Single(model!.Meshes);
        Assert.Equal("wall", mesh.DiffuseTextureName);
        Assert.True(mesh.BakedLighting);
        Assert.Equal(MeshBlendMode.Cutout, mesh.BlendMode);
        Assert.Equal(3, mesh.Indices.Length);
        // The frame moves the model up by 10.
        Assert.All(mesh.Positions, p => Assert.Equal(10f, p.Z));
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1f), mesh.Colors[0], new Vector4Comparer());
        // Winding turned so the face points up, like its normals.
        var n = Vector3.Cross(mesh.Positions[mesh.Indices[1]] - mesh.Positions[mesh.Indices[0]],
            mesh.Positions[mesh.Indices[2]] - mesh.Positions[mesh.Indices[0]]);
        Assert.True(n.Z > 0);
        Assert.True(mesh.Normals[0].Z > 0.99f);
    }

    [Fact]
    public void TextureDictionary_ConvertsAndKeepsFormats()
    {
        var textures = ClassicTextures.ReadDictionary(BuildDictionary());

        var red = textures[Joaat.Hash("red")];
        Assert.Equal((TextureFormat.B8G8R8A8, 2, 2, 1), (red.Format, red.Width, red.Height, red.MipLevels));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, red.Data[..4]);
        var compressed = textures[Joaat.Hash("dxt")];
        Assert.Equal((TextureFormat.Bc1, 8), (compressed.Format, compressed.Data.Length));
    }

    private sealed class Vector4Comparer : IEqualityComparer<Vector4>
    {
        public bool Equals(Vector4 a, Vector4 b) => Vector4.Distance(a, b) < 0.01f;

        public int GetHashCode(Vector4 v) => 0;
    }

    private static byte[] Chunk(uint type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var data = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, type);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), SanAndreasStamp);
        body.CopyTo(data, 12);
        return data;
    }

    private static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Floats(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] BuildClump()
    {
        var frame = Floats(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 10).Concat(Ints(-1, 0)).ToArray();
        var frames = Chunk(RwChunk.FrameList, Chunk(RwChunk.Struct, Ints(1), frame),
            Chunk(RwChunk.Extension, Chunk(RwChunk.FrameName, Encoding.ASCII.GetBytes("box"))));

        // Prelit, textured, positions, normals: 1 triangle, 3 vertices, 1 morph target.
        var geometryStruct = Ints(0x08 | 0x04 | 0x02 | 0x10, 1, 3, 1)
            .Concat(Enumerable.Repeat(new byte[] { 128, 128, 128, 255 }, 3).SelectMany(c => c))
            .Concat(Floats(0, 0, 1, 0, 0, 1))
            // Stored (v1, v0, material, v2).
            .Concat(new byte[] { 0, 0, 1, 0, 0, 0, 2, 0 })
            .Concat(Floats(0, 0, 0, 1)).Concat(Ints(1, 1))
            .Concat(Floats(0, 0, 0, 0, 1, 0, 1, 0, 0))
            .Concat(Floats(0, 0, 1, 0, 0, 1, 0, 0, 1))
            .ToArray();
        var texture = Chunk(RwChunk.Texture, Chunk(RwChunk.Struct, Ints(0x1106)), Chunk(RwChunk.String, Encoding.ASCII.GetBytes("wall\0")),
            Chunk(RwChunk.String, [0]), Chunk(RwChunk.Extension));
        var material = Chunk(RwChunk.Material, Chunk(RwChunk.Struct, Ints(0), [255, 255, 255, 255], Ints(0, 1), Floats(1, 1, 1)), texture,
            Chunk(RwChunk.Extension));
        var materials = Chunk(RwChunk.MaterialList, Chunk(RwChunk.Struct, Ints(1, -1)), material);
        var geometry = Chunk(RwChunk.Geometry, Chunk(RwChunk.Struct, geometryStruct), materials, Chunk(RwChunk.Extension));
        var geometries = Chunk(RwChunk.GeometryList, Chunk(RwChunk.Struct, Ints(1)), geometry);
        var atomic = Chunk(RwChunk.Atomic, Chunk(RwChunk.Struct, Ints(0, 0, 5, 0)), Chunk(RwChunk.Extension));
        return Chunk(RwChunk.Clump, Chunk(RwChunk.Struct, Ints(1, 0, 0)), frames, geometries, atomic, Chunk(RwChunk.Extension));
    }

    private static byte[] NativeTexture(string name, int raster, uint format, int width, int height, int compression, byte[] level)
    {
        var header = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 9);
        Encoding.ASCII.GetBytes(name).CopyTo(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(72), raster);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), format);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(80), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(82), (ushort)height);
        header[84] = 16;
        header[85] = 1;
        header[86] = 4;
        header[87] = (byte)compression;
        return Chunk(RwChunk.TextureNative, Chunk(RwChunk.Struct, header, Ints(level.Length), level), Chunk(RwChunk.Extension));
    }

    private static byte[] BuildDictionary()
    {
        // 565: pure red is 0xF800.
        var red = NativeTexture("red", 0x0200, 23, 2, 2, 0, Enumerable.Repeat(new byte[] { 0x00, 0xF8 }, 4).SelectMany(b => b).ToArray());
        var dxt = NativeTexture("dxt", 0x0100, 0x31545844, 4, 4, 8, new byte[8]);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 2);
        return Chunk(RwChunk.TextureDictionary, Chunk(RwChunk.Struct, header), red, dxt, Chunk(RwChunk.Extension));
    }
}
