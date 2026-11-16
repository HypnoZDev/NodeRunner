using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// Writes a complete texture dictionary (.ytd, resource version 13): dictionary, textures and names in system pages,
// pixels in graphics pages (a texture never crosses the end of a page).
internal static class YtdBuilder
{
    private const int Version = 13;

    // Most common virtual table words of the game's files.
    private const uint DictionaryVft = 0x40573510;
    private const uint TextureVft = 0x40619668;

    private const int DictionaryLength = 0x40;
    private const int TextureLength = 0x90;

    public static byte[] Build(List<XmlTexture> textures)
    {
        var builder = new ResourceBuilder();
        WriteDictionary(builder, textures);
        return builder.Build(Version);
    }

    // The dictionary block (the file's root when it is the first block added) and the block of each texture, by name
    // hash (drawables point their shaders at them).
    internal static (ResourceBuilder.Block Dictionary, Dictionary<uint, ResourceBuilder.Block> Textures) WriteDictionary(
        ResourceBuilder builder, List<XmlTexture> textures)
    {
        // The game looks textures up by name hash: sorted.
        textures = [.. textures.OrderBy(t => t.Data.NameHash)];
        int count = textures.Count;
        var dictionary = builder.Add(DictionaryLength);
        var hashes = builder.Add(4 * count);
        var list = builder.Add(8 * count);

        dictionary.U32(0, DictionaryVft);
        dictionary.U32(0x04, 1);
        dictionary.U32(0x18, 1);
        builder.Pointer(dictionary, 0x20, hashes);
        dictionary.U16(0x28, count);
        dictionary.U16(0x2A, count);
        builder.Pointer(dictionary, 0x30, list);
        dictionary.U16(0x38, count);
        dictionary.U16(0x3A, count);

        var blocks = new Dictionary<uint, ResourceBuilder.Block>();
        for (int i = 0; i < count; i++)
        {
            var x = textures[i];
            var tex = x.Data;
            var t = builder.Add(TextureLength);
            hashes.U32(4 * i, tex.NameHash);
            builder.Pointer(list, 8 * i, t);
            blocks[tex.NameHash] = t;

            t.U32(0, TextureVft);
            t.U32(0x04, 1);
            builder.Pointer(t, 0x28, builder.String(tex.Name));
            t.U16(0x30, 1);
            t.U16(0x32, x.Unknown32);
            t.U32(0x40, x.UsageData);
            t.U32(0x48, x.ExtraFlags);
            t.U16(0x50, tex.Width);
            t.U16(0x52, tex.Height);
            t.U16(0x54, 1);
            t.U16(0x56, Stride(tex.Format, tex.Width));
            t.U32(0x58, GameFormat(tex.Format));
            t.U8(0x5D, tex.MipLevels);
            builder.Pointer(t, 0x70, builder.Add(tex.Data, graphics: true));
        }
        return (dictionary, blocks);
    }

    // Bytes per row of pixels (for compressed formats: a row of 4 x 4 blocks divided by 4), as in the game's files.
    internal static int Stride(TextureFormat format, int width)
    {
        int row = TextureLayout.Pitch(format, width, 1).RowPitch;
        return TextureLayout.IsBlockCompressed(format) ? row / 4 : row;
    }

    internal static uint GameFormat(TextureFormat format) => format switch
    {
        TextureFormat.Bc1 => 0x31545844,
        TextureFormat.Bc2 => 0x33545844,
        TextureFormat.Bc3 => 0x35545844,
        TextureFormat.Bc4 => 0x31495441,
        TextureFormat.Bc5 => 0x32495441,
        TextureFormat.Bc7 => 0x20374342,
        TextureFormat.B8G8R8A8 => 21,
        TextureFormat.B8G8R8X8 => 22,
        TextureFormat.B5G5R5A1 => 25,
        TextureFormat.A8 => 28,
        TextureFormat.R8G8B8A8 => 32,
        TextureFormat.L8 => 50,
        _ => throw new InvalidDataException($"Format de texture {format} non pris en charge."),
    };
}
