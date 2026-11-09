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
