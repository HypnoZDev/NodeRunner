namespace NodeRunner.Data.Textures;

// Pixel formats of the game textures (names follow DXGI, which the GPU reads directly).
public enum TextureFormat
{
    Unknown,

    Bc1,

    Bc2,

    Bc3,

    Bc4,

    Bc5,

    Bc7,

    B8G8R8A8,
    B8G8R8X8,
    R8G8B8A8,
    B5G5R5A1,
    A8,
    L8,
}

// A texture of the game (from a .ytd dictionary or embedded in a model file), in its GPU format: Data holds every mip
// level, largest first, one after the other.
public sealed class TextureData
{
    public required uint NameHash { get; init; }

    public required string Name { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MipLevels { get; init; }
    public required TextureFormat Format { get; init; }

    public required byte[] Data { get; init; }
}

// Reads texture dictionaries (.ytd) from the game files.
public interface ITextureReader
{
    // Reads every texture of a .ytd file, keyed by name hash.
    IReadOnlyDictionary<uint, TextureData> ReadTextures(string path);
}

// Memory layout of the mip levels of a TextureData.
public static class TextureLayout
{
    public static bool IsBlockCompressed(TextureFormat format) =>
        format is TextureFormat.Bc1 or TextureFormat.Bc2 or TextureFormat.Bc3
            or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7;

    // Bytes per row (of pixels, or of 4x4 blocks for compressed formats) and bytes of a whole mip level of the given
    // size.
    public static (int RowPitch, int SlicePitch) Pitch(TextureFormat format, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (IsBlockCompressed(format))
        {
            int blockBytes = format is TextureFormat.Bc1 or TextureFormat.Bc4 ? 8 : 16;
            int row = (width + 3) / 4 * blockBytes;
            return (row, row * ((height + 3) / 4));
        }

        int pixelBytes = format switch
        {
            TextureFormat.B8G8R8A8 or TextureFormat.B8G8R8X8 or TextureFormat.R8G8B8A8 => 4,
            TextureFormat.B5G5R5A1 => 2,
            TextureFormat.A8 or TextureFormat.L8 => 1,
            _ => 0,
        };
        return (width * pixelBytes, width * pixelBytes * height);
    }

    // The mip levels actually present in Data: offset, size and pitches of each, largest first.
    public static List<MipLevel> GetMipLevels(TextureData texture)
    {
        var levels = new List<MipLevel>();
        int offset = 0;
        for (int i = 0; i < texture.MipLevels; i++)
        {
            int width = Math.Max(1, texture.Width >> i);
            int height = Math.Max(1, texture.Height >> i);
            var (row, slice) = Pitch(texture.Format, width, height);
            if (slice == 0 || offset + slice > texture.Data.Length)
                break;
            levels.Add(new MipLevel(width, height, offset, row, slice));
            offset += slice;
        }
        return levels;
    }
}

public readonly record struct MipLevel(int Width, int Height, int Offset, int RowPitch, int SlicePitch);
