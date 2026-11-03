using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// Reads texture dictionaries (.ytd).
public sealed class YtdReader : ITextureReader
{
    private readonly IGameFileSystem _files;

    public YtdReader(IGameFileSystem files)
    {
        _files = files;
    }

    public IReadOnlyDictionary<uint, TextureData> ReadTextures(string path)
    {
        if (_files.ReadFile(path) is not { } data)
            return new Dictionary<uint, TextureData>();
        var header = _files.GetResourceHeader(path) ?? throw new InvalidDataException($"{path} n'est pas une ressource.");
        return Read(data, header);
    }

    public static Dictionary<uint, TextureData> Read(ReadOnlySpan<byte> data, ResourceHeader header)
    {
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        return ReadDictionary(res, 0);
    }

    // Texture dictionary (pgDictionary): 0x30 pointer to the list of texture pointers, 0x38 their count.
    private const int TexturesPointer = 0x30;
    private const int TexturesCount = 0x38;

    // Texture (grcTexturePC), 0x90 bytes.
    private const int NamePointer = 0x28;
    private const int WidthOffset = 0x50;
    private const int HeightOffset = 0x52;
    private const int StrideOffset = 0x56;
    private const int FormatOffset = 0x58;
    private const int LevelsOffset = 0x5D;
    private const int DataPointer = 0x70;
    private const int TextureLength = 0x90;

    internal static Dictionary<uint, TextureData> ReadDictionary(ResourceData res, int offset)
    {
        var textures = new Dictionary<uint, TextureData>();
        int count = res.UInt16(offset + TexturesCount);
        int list = res.ResolveArray(res.UInt64(offset + TexturesPointer), count, 8, "liste des textures");
        for (int i = 0; i < count; i++)
        {
            ulong pointer = res.UInt64(list + i * 8);
            if (pointer == 0)
                continue;
            if (ReadTexture(res, res.ResolveArray(pointer, 1, TextureLength, "texture")) is { } texture)
                textures[texture.NameHash] = texture;
        }
        return textures;
    }

    private static TextureData? ReadTexture(ResourceData res, int t)
    {
        string? name = res.Text(res.UInt64(t + NamePointer), "nom de texture");
        int width = res.UInt16(t + WidthOffset);
        int height = res.UInt16(t + HeightOffset);
        int stride = res.UInt16(t + StrideOffset);
        var format = Format(res.UInt32(t + FormatOffset));
        int levels = Math.Max(1, (int)res.Byte(t + LevelsOffset));
        ulong pixels = res.UInt64(t + DataPointer);
        if (name is null || pixels == 0 || format == TextureFormat.Unknown || width == 0 || height == 0)
            return null;

        int length = PixelsLength(stride, height, levels);
        int start = res.ResolveArray(pixels, length, 1, "pixels de " + name);
        name = name.ToLowerInvariant();
        return new TextureData
        {
            NameHash = Joaat.Hash(name),
            Name = name,
            Width = width,
            Height = height,
            MipLevels = levels,
            Format = format,
            Data = res.Bytes(start, length).ToArray(),
        };
    }

    // Bytes of all the mip levels: the first is stride x height (for block formats, the stride is a row of blocks and
    // the height counts pixels, so this is 4 times the level: the game rounds the same way), each next level a
    // quarter of the previous.
    private static int PixelsLength(int stride, int height, int levels)
    {
        long level = (long)stride * height;
        long total = 0;
        for (int i = 0; i < levels; i++)
        {
            total += level;
            level /= 4;
        }
        return (int)total;
    }

    private static TextureFormat Format(uint code) => code switch
    {
        0x31545844 => TextureFormat.Bc1, // DXT1
        0x33545844 => TextureFormat.Bc2, // DXT3
        0x35545844 => TextureFormat.Bc3, // DXT5
        0x31495441 => TextureFormat.Bc4, // ATI1
        0x32495441 => TextureFormat.Bc5, // ATI2
        0x20374342 => TextureFormat.Bc7, // "BC7 "
        21 => TextureFormat.B8G8R8A8,    // A8R8G8B8
        22 => TextureFormat.B8G8R8X8,    // X8R8G8B8
        25 => TextureFormat.B5G5R5A1,    // A1R5G5B5
        28 => TextureFormat.A8,
        32 => TextureFormat.R8G8B8A8,    // A8B8G8R8
        50 => TextureFormat.L8,
        _ => TextureFormat.Unknown,
    };
}
