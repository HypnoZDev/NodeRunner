using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Gta4;

// Reads GTA IV textures: ".wtd" dictionaries and the dictionaries embedded in models (same layout). Texture names are
// stored as "pack:/name.dds"; the dictionary keys and the model references use the bare name.
public static class Gta4Textures
{
    // Dictionary: hashes at 0x10, textures at 0x18 (pointer arrays).
    private const int DictionaryHashes = 0x10, DictionaryItems = 0x18;
    // Texture: name at 0x14, width / height (ushort) at 0x1C / 0x1E, D3D9 format at 0x20, mip levels (byte) at 0x27,
    // pixel data (graphics pointer) at 0x48.
    private const int TextureName = 0x14, TextureWidth = 0x1C, TextureHeight = 0x1E, TextureFormatAt = 0x20, TextureLevels = 0x27, TextureData = 0x48;

    public static Dictionary<uint, TextureData> ReadDictionary(Gta4Drawables.Resource res, int dictionary)
    {
        var textures = new Dictionary<uint, TextureData>();
        if (dictionary < 0)
            return textures;
        int hashes = res.Pointer(dictionary + DictionaryHashes);
        int i = 0;
        foreach (int texture in res.Pointers(dictionary + DictionaryItems))
        {
            uint hash = res.UInt(hashes + i++ * 4);
            if (ReadTexture(res, texture) is { } data)
                textures[hash != 0 ? hash : data.NameHash] = data;
        }
        return textures;
    }

    public static string BareName(string name)
    {
        int slash = name.LastIndexOfAny([':', '/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        return name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static TextureData? ReadTexture(Gta4Drawables.Resource res, int texture)
    {
        if (texture < 0)
            return null;
        var format = Format(res.UInt(texture + TextureFormatAt));
        int width = res.UShort(texture + TextureWidth);
        int height = res.UShort(texture + TextureHeight);
        int levels = Math.Max(1, (int)res.Data[Math.Min(texture + TextureLevels, res.Data.Length - 1)]);
        int data = res.Pointer(texture + TextureData);
        if (format == TextureFormat.Unknown || width == 0 || height == 0 || data < 0)
            return null;

        // Keep the levels that fit in the file.
        int size = 0, kept = 0;
        for (; kept < levels; kept++)
        {
            int slice = TextureLayout.Pitch(format, width >> kept, height >> kept).SlicePitch;
            if ((long)data + size + slice > res.Data.Length)
                break;
            size += slice;
        }
        if (kept == 0)
            return null;

        string name = BareName(res.String(res.Pointer(texture + TextureName)) ?? "");
        return new TextureData
        {
            NameHash = Joaat.Hash(name),
            Name = name,
            Width = width,
            Height = height,
            MipLevels = kept,
            Format = format,
            Data = res.Data.AsSpan(data, size).ToArray(),
        };
    }

    // D3D9 formats: four-character codes for the compressed ones, numbers otherwise.
    private static TextureFormat Format(uint d3d) => d3d switch
    {
        0x31545844 => TextureFormat.Bc1, // DXT1
        0x33545844 => TextureFormat.Bc2, // DXT3
        0x35545844 => TextureFormat.Bc3, // DXT5
        21 => TextureFormat.B8G8R8A8,
        22 => TextureFormat.B8G8R8X8,
        25 => TextureFormat.B5G5R5A1,
        28 => TextureFormat.A8,
        50 => TextureFormat.L8,
        _ => TextureFormat.Unknown,
    };
}

// Texture dictionaries (".wtd") of a GTA IV installation.
public sealed class Gta4TextureReader(Gta4FileSystem files) : ITextureReader
{
    public IReadOnlyDictionary<uint, TextureData> ReadTextures(string path)
    {
        if (files.ReadResource(path) is not { } resource)
            return new Dictionary<uint, TextureData>();
        try
        {
            return Gta4Textures.ReadDictionary(new Gta4Drawables.Resource(resource.Data, resource.SystemSize), 0);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"{path} : textures illisibles ({ex.Message})", ex);
        }
    }
}
