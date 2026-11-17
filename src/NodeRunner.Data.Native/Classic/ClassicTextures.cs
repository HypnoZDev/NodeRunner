using System.Buffers.Binary;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Classic;

// Reads the texture dictionaries of the first trilogy (".txd"): a "TextureDictionary" section holding one
// "TextureNative" per texture, PC layout (Direct3D 8 for GTA III / Vice City, 9 for San Andreas): platform, filtering,
// name and mask (32 bytes each), raster format, alpha or Direct3D format, size, depth, mip count, type, compression,
// an optional palette, then each mip level with its size. Formats the GPU cannot read directly (565, 4444, 555,
// palettes) are turned into 32-bit colours.
public static class ClassicTextures
{
    private const int FormatMask = 0x0F00, Format1555 = 0x0100, Format565 = 0x0200, Format4444 = 0x0300, FormatLum8 = 0x0400,
        Format8888 = 0x0500, Format888 = 0x0600, Format555 = 0x0A00, Palette8 = 0x2000, Palette4 = 0x4000;
    private const uint Dxt1 = 0x31545844, Dxt3 = 0x33545844, Dxt5 = 0x35545844;

    public static Dictionary<uint, TextureData> ReadDictionary(byte[] data)
    {
        if (RwChunk.At(data, 0, data.Length) is not { Type: RwChunk.TextureDictionary } dictionary)
            throw new InvalidDataException("pas un dictionnaire de textures RenderWare");
        var textures = new Dictionary<uint, TextureData>();
        foreach (var native in dictionary.Children(data).Where(c => c.Type == RwChunk.TextureNative))
        {
            if (native.Child(data, RwChunk.Struct) is { } s && ReadTexture(data.AsSpan(s.Offset, s.Size)) is { } texture)
                textures[texture.NameHash] = texture;
        }
        return textures;
    }

    private static TextureData? ReadTexture(ReadOnlySpan<byte> d)
    {
        if (d.Length < 88)
            return null;
        uint platform = BinaryPrimitives.ReadUInt32LittleEndian(d);
        if (platform is not (8 or 9))
            return null; // Console textures (PS2, Xbox).
        string name = Text(d.Slice(8, 32));
        int raster = BinaryPrimitives.ReadInt32LittleEndian(d[72..]);
        uint alphaOrFormat = BinaryPrimitives.ReadUInt32LittleEndian(d[76..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(d[80..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(d[82..]);
        int levels = d[85];
        int compression = d[87];
        if (name.Length == 0 || width == 0 || height == 0 || levels == 0)
            return null;

        int at = 88;
        byte[]? palette = null;
        if ((raster & (Palette8 | Palette4)) != 0)
        {
            int entries = (raster & Palette8) != 0 ? 256 : 16;
            if (at + entries * 4 > d.Length)
                return null;
            palette = d.Slice(at, entries * 4).ToArray();
            at += entries * 4;
        }

        TextureFormat format;
        if (platform == 9 && alphaOrFormat is Dxt1 or Dxt3 or Dxt5)
            format = alphaOrFormat switch { Dxt1 => TextureFormat.Bc1, Dxt3 => TextureFormat.Bc2, _ => TextureFormat.Bc3 };
        else if (platform == 8 && compression is 1 or 3 or 5)
            format = compression switch { 1 => TextureFormat.Bc1, 3 => TextureFormat.Bc2, _ => TextureFormat.Bc3 };
        else
            format = TextureFormat.B8G8R8A8; // Converted below if needed.

        using var output = new MemoryStream();
        int kept = 0;
        for (int level = 0; level < levels; level++)
        {
            if (at + 4 > d.Length)
                break;
            int size = BinaryPrimitives.ReadInt32LittleEndian(d[at..]);
            at += 4;
            if (size < 0 || at + size > d.Length)
                break;
            var bytes = d.Slice(at, size);
            at += size;
            int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
            var (_, slice) = TextureLayout.Pitch(format, w, h);
            byte[]? pixels = TextureLayout.IsBlockCompressed(format)
                ? bytes.Length >= slice ? bytes[..slice].ToArray() : null
                : ToBgra(bytes, raster, palette, w, h);
            if (pixels is null)
                break;
            output.Write(pixels);
            kept++;
        }
        if (kept == 0)
            return null;
        return new TextureData
        {
            NameHash = Joaat.Hash(name),
            Name = name,
            Width = width,
            Height = height,
            MipLevels = kept,
            Format = format,
            Data = output.ToArray(),
        };
    }

    private static byte[]? ToBgra(ReadOnlySpan<byte> src, int raster, byte[]? palette, int w, int h)
    {
        int pixels = w * h;
        var dst = new byte[pixels * 4];
        if (palette is not null)
        {
            bool four = (raster & Palette4) != 0 && src.Length < pixels;
            if (src.Length < (four ? (pixels + 1) / 2 : pixels))
                return null;
            for (int i = 0; i < pixels; i++)
            {
                int index = four ? (src[i / 2] >> (i % 2 == 0 ? 0 : 4)) & 0xF : src[i];
                int p = Math.Min(index, palette.Length / 4 - 1) * 4;
                // Palette entries are RGBA.
                dst[i * 4] = palette[p + 2];
                dst[i * 4 + 1] = palette[p + 1];
                dst[i * 4 + 2] = palette[p];
                dst[i * 4 + 3] = palette[p + 3];
            }
            return dst;
        }
        int format = raster & FormatMask;
        int bytesPerPixel = format is Format8888 or Format888 ? 4 : format == FormatLum8 ? 1 : 2;
        if (src.Length < pixels * bytesPerPixel)
            return null;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            switch (format)
            {
                case Format8888:
                    src.Slice(i * 4, 4).CopyTo(dst.AsSpan(o));
                    break;
                case Format888:
                    src.Slice(i * 4, 3).CopyTo(dst.AsSpan(o));
                    dst[o + 3] = 255;
                    break;
                case FormatLum8:
                    dst[o] = dst[o + 1] = dst[o + 2] = src[i];
                    dst[o + 3] = 255;
                    break;
                default:
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]);
                    (int r, int g, int b, int a) = format switch
                    {
                        Format565 => (Bits(v >> 11, 5), Bits(v >> 5, 6), Bits(v, 5), 255),
                        Format4444 => (Bits(v >> 8, 4), Bits(v >> 4, 4), Bits(v, 4), Bits(v >> 12, 4)),
                        Format555 => (Bits(v >> 10, 5), Bits(v >> 5, 5), Bits(v, 5), 255),
                        _ => (Bits(v >> 10, 5), Bits(v >> 5, 5), Bits(v, 5), (v & 0x8000) != 0 ? 255 : 0),
                    };
                    dst[o] = (byte)b;
                    dst[o + 1] = (byte)g;
                    dst[o + 2] = (byte)r;
                    dst[o + 3] = (byte)a;
                    break;
            }
        }
        return dst;
    }

    private static int Bits(int value, int bits)
    {
        int max = (1 << bits) - 1;
        return (value & max) * 255 / max;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
    }
}

// Texture dictionaries (".txd") of a GTA III / Vice City / San Andreas installation.
public sealed class ClassicTextureReader(ClassicFileSystem files) : ITextureReader
{
    public IReadOnlyDictionary<uint, TextureData> ReadTextures(string path)
    {
        if (files.ReadFile(path) is not { } data)
            return new Dictionary<uint, TextureData>();
        try
        {
            return ClassicTextures.ReadDictionary(data);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"{path} : textures illisibles ({ex.Message})", ex);
        }
    }
}
