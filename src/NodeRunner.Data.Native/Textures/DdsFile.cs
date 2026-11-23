using System.Buffers.Binary;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// Reads .dds images (the files Sollumz and CodeWalker export next to a .ytd.xml) in the formats the game uses.
public static class DdsFile
{
    private const uint Magic = 0x20534444; // "DDS "
    private const int HeaderSize = 128;
    private const int Dx10HeaderSize = 20;

    // DDS_HEADER (after the magic): 8 height, 12 width, 24 mip count, 76 pixel format (DDS_PIXELFORMAT, 32 bytes),
    // 108 caps2.
    private const uint MipCountFlag = 0x20000;
    private const uint FourCcFlag = 0x4;
    private const uint RgbFlag = 0x40;
    private const uint AlphaOnlyFlag = 0x2;
    private const uint LuminanceFlag = 0x20000;
    private const uint CubeMapFlag = 0x200;
    private const uint VolumeFlag = 0x200000;

    public static TextureData Read(ReadOnlySpan<byte> dds, string name)
    {
        if (dds.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(dds) != Magic)
            throw new InvalidDataException($"{name} : ce n'est pas une image .dds.");
        var header = dds[..HeaderSize].ToArray();
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(o));

        uint flags = U32(8);
        int height = (int)U32(12);
        int width = (int)U32(16);
        int levels = (flags & MipCountFlag) != 0 ? Math.Max(1, (int)U32(28)) : 1;
        if ((U32(112) & (CubeMapFlag | VolumeFlag)) != 0)
            throw new InvalidDataException($"{name} : les textures cube ou 3D ne sont pas prises en charge.");
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384)
            throw new InvalidDataException($"{name} : taille d'image invalide ({width} x {height}).");

        uint pfFlags = U32(80);
        uint fourCc = U32(84);
        int start = HeaderSize;
        TextureFormat format;
        if ((pfFlags & FourCcFlag) != 0 && fourCc == FourCc("DX10"))
        {
            if (dds.Length < HeaderSize + Dx10HeaderSize)
                throw new InvalidDataException($"{name} : image .dds tronquée.");
            var dx10 = dds.Slice(HeaderSize, Dx10HeaderSize);
            format = FromDxgi(BinaryPrimitives.ReadUInt32LittleEndian(dx10));
            if (BinaryPrimitives.ReadUInt32LittleEndian(dx10[4..]) != 3 || BinaryPrimitives.ReadUInt32LittleEndian(dx10[12..]) > 1) // 2D texture, one item
                throw new InvalidDataException($"{name} : seules les textures 2D simples sont prises en charge.");
            start += Dx10HeaderSize;
        }
        else if ((pfFlags & FourCcFlag) != 0)
        {
            format = fourCc switch
            {
                _ when fourCc == FourCc("DXT1") => TextureFormat.Bc1,
                _ when fourCc == FourCc("DXT2") || fourCc == FourCc("DXT3") => TextureFormat.Bc2,
                _ when fourCc == FourCc("DXT4") || fourCc == FourCc("DXT5") => TextureFormat.Bc3,
                _ when fourCc == FourCc("ATI1") || fourCc == FourCc("BC4U") => TextureFormat.Bc4,
                _ when fourCc == FourCc("ATI2") || fourCc == FourCc("BC5U") => TextureFormat.Bc5,
                _ => TextureFormat.Unknown,
            };
        }
        else
        {
            int bits = (int)U32(88);
            uint r = U32(92), a = U32(104);
            format = bits switch
            {
                32 when (pfFlags & RgbFlag) != 0 && r == 0x00FF0000 => a != 0 ? TextureFormat.B8G8R8A8 : TextureFormat.B8G8R8X8,
                32 when (pfFlags & RgbFlag) != 0 && r == 0x000000FF => TextureFormat.R8G8B8A8,
                16 when (pfFlags & RgbFlag) != 0 && r == 0x7C00 && a == 0x8000 => TextureFormat.B5G5R5A1,
                8 when (pfFlags & AlphaOnlyFlag) != 0 => TextureFormat.A8,
                8 when (pfFlags & LuminanceFlag) != 0 => TextureFormat.L8,
                _ => TextureFormat.Unknown,
            };
        }
        if (format == TextureFormat.Unknown)
            throw new InvalidDataException($"{name} : format d'image non pris en charge par le jeu (enregistre-la en DXT1, DXT5, BC7 ou A8R8G8B8).");

        // Some tools write more levels than the size allows: keep those down to 1 x 1.
        levels = Math.Min(levels, 1 + (int)Math.Log2(Math.Max(width, height)));
        // The game sizes its pixels as stride x height, a quarter less per level, so its smallest compressed levels are
        // a few bytes short of whole blocks; images exported from game files keep that size.
        long length = 0, gameLength = 0;
        long level = (long)TextureLayout.Pitch(format, width, 1).RowPitch * height / (TextureLayout.IsBlockCompressed(format) ? 4 : 1);
        for (int i = 0; i < levels; i++, level /= 4)
        {
            length += TextureLayout.Pitch(format, width >> i, height >> i).SlicePitch;
            gameLength += level;
        }
        if (start + gameLength > dds.Length)
            throw new InvalidDataException($"{name} : image .dds tronquée ({dds.Length - start} octets de pixels, {length} attendus).");
        length = Math.Min(length, dds.Length - start);

        string lower = name.ToLowerInvariant();
        return new TextureData
        {
            NameHash = Joaat.Hash(lower),
            Name = lower,
            Width = width,
            Height = height,
            MipLevels = levels,
            Format = format,
            Data = dds.Slice(start, (int)length).ToArray(),
        };
    }

    // A .dds image of the texture, every mip level (the classic header, the DX10 one for BC7 only).
    public static byte[] Write(TextureData texture)
    {
        var format = texture.Format;
        bool dx10 = format == TextureFormat.Bc7;
        int length = 0;
        for (int i = 0; i < texture.MipLevels; i++)
            length += TextureLayout.Pitch(format, texture.Width >> i, texture.Height >> i).SlicePitch;
        int start = HeaderSize + (dx10 ? Dx10HeaderSize : 0);
        var dds = new byte[start + length];
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(o), v);

        bool compressed = TextureLayout.IsBlockCompressed(format);
        U32(0, Magic);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | MipCountFlag | (compressed ? 0x80000u : 0x8u));
        U32(12, (uint)texture.Height);
        U32(16, (uint)texture.Width);
        var (row, slice) = TextureLayout.Pitch(format, texture.Width, texture.Height);
        U32(20, (uint)(compressed ? slice : row));
        U32(24, 1);
        U32(28, (uint)texture.MipLevels);
        U32(76, 32);
        (uint flags, uint fourCc, uint bits, uint r, uint g, uint b, uint a) pf = format switch
        {
            TextureFormat.Bc1 => (FourCcFlag, FourCc("DXT1"), 0, 0, 0, 0, 0),
            TextureFormat.Bc2 => (FourCcFlag, FourCc("DXT3"), 0, 0, 0, 0, 0),
            TextureFormat.Bc3 => (FourCcFlag, FourCc("DXT5"), 0, 0, 0, 0, 0),
            TextureFormat.Bc4 => (FourCcFlag, FourCc("BC4U"), 0, 0, 0, 0, 0),
            TextureFormat.Bc5 => (FourCcFlag, FourCc("BC5U"), 0, 0, 0, 0, 0),
            TextureFormat.Bc7 => (FourCcFlag, FourCc("DX10"), 0, 0, 0, 0, 0),
            TextureFormat.B8G8R8A8 => (RgbFlag | 0x1, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
            TextureFormat.B8G8R8X8 => (RgbFlag, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0),
            TextureFormat.R8G8B8A8 => (RgbFlag | 0x1, 0, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000),
            TextureFormat.B5G5R5A1 => (RgbFlag | 0x1, 0, 16, 0x7C00, 0x03E0, 0x001F, 0x8000),
            TextureFormat.A8 => (AlphaOnlyFlag, 0, 8, 0, 0, 0, 0xFF),
            TextureFormat.L8 => (LuminanceFlag, 0, 8, 0xFF, 0, 0, 0),
            _ => throw new InvalidDataException($"Format de texture {format} non pris en charge."),
        };
        U32(80, pf.flags);
        U32(84, pf.fourCc);
        U32(88, pf.bits);
        U32(92, pf.r);
        U32(96, pf.g);
        U32(100, pf.b);
        U32(104, pf.a);
        U32(108, 0x1000 | (texture.MipLevels > 1 ? 0x400008u : 0u));
        if (dx10)
        {
            U32(HeaderSize, 98); // BC7_UNORM
            U32(HeaderSize + 4, 3);
            U32(HeaderSize + 12, 1);
        }
        // Game files can be a few bytes short on the smallest levels: the rest stays zero.
        texture.Data.AsSpan(0, Math.Min(length, texture.Data.Length)).CopyTo(dds.AsSpan(start));
        return dds;
    }

    private static uint FourCc(string code) => (uint)(code[0] | code[1] << 8 | code[2] << 16 | code[3] << 24);

    private static TextureFormat FromDxgi(uint dxgi) => dxgi switch
    {
        70 or 71 or 72 => TextureFormat.Bc1,
        73 or 74 or 75 => TextureFormat.Bc2,
        76 or 77 or 78 => TextureFormat.Bc3,
        79 or 80 => TextureFormat.Bc4,
        82 or 83 => TextureFormat.Bc5,
        97 or 98 or 99 => TextureFormat.Bc7,
        87 or 91 => TextureFormat.B8G8R8A8,
        88 or 93 => TextureFormat.B8G8R8X8,
        28 or 29 => TextureFormat.R8G8B8A8,
        86 => TextureFormat.B5G5R5A1,
        65 => TextureFormat.A8,
        61 => TextureFormat.L8,
        _ => TextureFormat.Unknown,
    };
}
