using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// A texture as stored in a resource (.ytd, or embedded in a model): where its pixels are in the data, for tools that
// replace them in place. Packed is false when its rows are padded (its pixels can't be written as one block).
public sealed record StoredTexture(string Name, int Width, int Height, int MipLevels, TextureFormat Format, int DataAt, int Length,
    bool Packed)
{
    public uint NameHash { get; } = Joaat.Hash(Name);

    // The texture as the data holds it now.
    public TextureData Read(byte[] content) => new()
    {
        NameHash = NameHash,
        Name = Name,
        Width = Width,
        Height = Height,
        MipLevels = MipLevels,
        Format = Format,
        Data = content.AsSpan(DataAt, Length).ToArray(),
    };

    // Writes new pixels (every mip level, in the texture's format) over the old ones; the file keeps its layout.
    // Returns what was there, to put it back.
    public byte[] Write(byte[] content, ReadOnlySpan<byte> pixels)
    {
        var before = content.AsSpan(DataAt, Length).ToArray();
        // The game sizes its smallest compressed levels a few bytes short of whole blocks.
        pixels[..Math.Min(pixels.Length, Length)].CopyTo(content.AsSpan(DataAt));
        return before;
    }

    public void Restore(byte[] content, byte[] before) => before.CopyTo(content.AsSpan(DataAt));
}
