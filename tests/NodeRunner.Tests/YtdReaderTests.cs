using System.Buffers.Binary;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;

namespace NodeRunner.Tests;

/// <summary>A texture dictionary built in memory: one system page, then the pixels in the graphics segment.</summary>
public class YtdReaderTests
{
    private const int SystemSize = 0x2000;

    [Fact]
    public void Read_GivesEachTextureWithItsMipLevels()
    {
        var (data, header) = Build();

        var textures = YtdReader.Read(data, header);

        Assert.Equal(2, textures.Count);
        var road = textures[Joaat.Hash("road_dirt")];
        Assert.Equal("road_dirt", road.Name);
        Assert.Equal((8, 8, 2, TextureFormat.Bc1), (road.Width, road.Height, road.MipLevels, road.Format));
        // DXT1 8x8: 2 rows of 2 blocks of 8 bytes; the game counts stride x height, then a quarter per level.
        Assert.Equal(16 * 8 + 16 * 8 / 4, road.Data.Length);
        Assert.Equal(Enumerable.Range(0, road.Data.Length).Select(i => (byte)i), road.Data);

        var sign = textures[Joaat.Hash("sign")];
        Assert.Equal((4, 4, 1, TextureFormat.B8G8R8A8), (sign.Width, sign.Height, sign.MipLevels, sign.Format));
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 64), sign.Data);
    }

    [Fact]
    public void Read_RejectsPointersOutsideTheFile()
    {
        var (data, header) = Build();
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x30), 0x50000000 + 0x10000);

        Assert.Throws<InvalidDataException>(() => YtdReader.Read(data, header));
    }

    /// <summary>
    /// Dictionary at 0, its list of 3 texture pointers at 0x40, textures at 0x100, 0x200 and 0x300 (the last one in an
    /// unknown format, skipped), names from 0x400; pixels of the first at graphics 0, of the second at graphics 0x100.
    /// </summary>
    private static (byte[] Data, ResourceHeader Header) Build()
    {
        var data = new byte[SystemSize + 0x200];
        var s = data.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x30..], 0x50000040);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x38..], 3);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x40..], 0x50000100);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x48..], 0x50000200);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x50..], 0x50000300);

        Texture(s, 0x100, 0x400, "Road_Dirt", 8, 8, 16, 0x31545844, 2, 0);
        Texture(s, 0x200, 0x420, "sign", 4, 4, 16, 21, 1, 0x100);
        Texture(s, 0x300, 0x440, "odd", 4, 4, 16, 999, 1, 0x100);

        for (int i = 0; i < 160; i++)
            data[SystemSize + i] = (byte)i;
        data.AsSpan(SystemSize + 0x100, 64).Fill(0xAB);

        return (data, new ResourceHeader(13, MetaDocument.PageFlags(1, SystemSize), 0xD0000000));
    }

    private static void Texture(Span<byte> s, int at, int nameAt, string name, int width, int height, int stride,
        uint format, byte levels, int pixels)
    {
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(s[nameAt..]);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(at + 0x28)..], 0x50000000 + (ulong)nameAt);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 0x50)..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 0x52)..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 0x56)..], (ushort)stride);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 0x58)..], format);
        s[at + 0x5D] = levels;
        BinaryPrimitives.WriteUInt64LittleEndian(s[(at + 0x70)..], 0x60000000 + (ulong)pixels);
    }
}
