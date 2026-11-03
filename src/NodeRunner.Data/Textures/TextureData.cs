namespace NodeRunner.Data.Textures;

/// <summary>Pixel formats of the game textures (names follow DXGI, which the GPU reads directly).</summary>
public enum TextureFormat
{
    Unknown,

    /// <summary>DXT1: 4x4 blocks of 8 bytes, colour + 1-bit alpha.</summary>
    Bc1,

    /// <summary>DXT3: 4x4 blocks of 16 bytes, colour + sharp alpha.</summary>
    Bc2,

    /// <summary>DXT5: 4x4 blocks of 16 bytes, colour + smooth alpha.</summary>
    Bc3,

    /// <summary>ATI1: 4x4 blocks of 8 bytes, one channel.</summary>
    Bc4,

    /// <summary>ATI2: 4x4 blocks of 16 bytes, two channels (normal maps).</summary>
    Bc5,

    /// <summary>4x4 blocks of 16 bytes, high quality colour + alpha.</summary>
    Bc7,

    B8G8R8A8,
    B8G8R8X8,
    R8G8B8A8,
    B5G5R5A1,
    A8,
    L8,
}

/// <summary>
/// A texture of the game (from a .ytd dictionary or embedded in a model file), in its GPU format:
/// <see cref="Data"/> holds every mip level, largest first, one after the other.
/// </summary>
public sealed class TextureData
{
    /// <summary>Hash of the lower case name.</summary>
    public required uint NameHash { get; init; }

    public required string Name { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MipLevels { get; init; }
    public required TextureFormat Format { get; init; }

    public required byte[] Data { get; init; }
}

/// <summary>Reads texture dictionaries (.ytd) from the game files.</summary>
public interface ITextureReader
{
    /// <summary>
    /// Reads every texture of a .ytd file, keyed by name hash.
    /// Returns an empty dictionary if the file does not exist.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is damaged or in an unsupported format.</exception>
    IReadOnlyDictionary<uint, TextureData> ReadTextures(string path);
}

/// <summary>Memory layout of the mip levels of a <see cref="TextureData"/>.</summary>
public static class TextureLayout
{
    public static bool IsBlockCompressed(TextureFormat format) =>
        format is TextureFormat.Bc1 or TextureFormat.Bc2 or TextureFormat.Bc3
            or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7;

    /// <summary>
    /// Bytes per row (of pixels, or of 4x4 blocks for compressed formats) and bytes of a whole mip level
    /// of the given size. Zero for <see cref="TextureFormat.Unknown"/>.
    /// </summary>
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

    /// <summary>
    /// The mip levels actually present in <see cref="TextureData.Data"/>: offset, size and pitches of each,
    /// largest first. Levels cut off by a too short data block are left out.
    /// </summary>
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

/// <summary>One mip level inside <see cref="TextureData.Data"/>.</summary>
public readonly record struct MipLevel(int Width, int Height, int Offset, int RowPitch, int SlicePitch);
