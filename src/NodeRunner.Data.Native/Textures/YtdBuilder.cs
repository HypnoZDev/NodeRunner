using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// Writes a complete texture dictionary (.ytd, resource version 13): dictionary, textures and names in one system page,
// pixels in graphics pages (a texture never crosses the end of a page).
internal static class YtdBuilder
{
    private const int Version = 13;
    private const ulong SystemBase = 0x50000000;
    private const ulong GraphicsBase = 0x60000000;
    private const int MinPageSize = 0x2000;
    private const int MaxBasePageSize = 0x200 << 15;

    // Most common virtual table words of the game's files.
    private const uint DictionaryVft = 0x40573510;
    private const uint TextureVft = 0x40619668;

    private const int DictionaryLength = 0x40;
    private const int TextureLength = 0x90;

    public static byte[] Build(List<XmlTexture> textures)
    {
        // The game looks textures up by name hash: sorted.
        textures = [.. textures.OrderBy(t => t.Data.NameHash)];
        int count = textures.Count;

        var (positions, pageSize, pageCount) = LayoutPixels(textures.Select(t => t.Data.Data.Length).ToList());
        int graphicsBase = Math.Min(pageSize, MaxBasePageSize);
        int graphicsUnits = pageCount * (pageSize / graphicsBase);
        int graphicsChunks = (graphicsUnits >> 4) + BitOperations.PopCount((uint)graphicsUnits & 0xF);

        // System segment.
        int length = 0;
        int Alloc(int size)
        {
            int at = (length + 15) & ~15;
            length = at + size;
            return at;
        }
        int dictionary = Alloc(DictionaryLength);
        int pagesInfo = Alloc(16 + 8 * (1 + graphicsChunks));
        int hashes = Alloc(4 * count);
        int list = Alloc(8 * count);
        var textureAt = textures.Select(_ => Alloc(TextureLength)).ToArray();
        var nameAt = textures.Select(t => Alloc(Encoding.ASCII.GetByteCount(t.Data.Name) + 1)).ToArray();

        int systemSize = MinPageSize;
        while (systemSize < length)
            systemSize *= 2;
        if (systemSize > MaxBasePageSize)
            throw new InvalidDataException("Trop de textures dans un seul dictionnaire.");
        var content = new byte[systemSize + pageCount * pageSize];
        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(o), (ushort)v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(o), v);
        void U64(int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(o), v);

        U32(dictionary, DictionaryVft);
        U32(dictionary + 0x04, 1);
        U64(dictionary + 0x08, SystemBase + (ulong)pagesInfo);
        U32(dictionary + 0x18, 1);
        U64(dictionary + 0x20, SystemBase + (ulong)hashes);
        U16(dictionary + 0x28, count);
        U16(dictionary + 0x2A, count);
        U64(dictionary + 0x30, SystemBase + (ulong)list);
        U16(dictionary + 0x38, count);
        U16(dictionary + 0x3A, count);

        content[pagesInfo + 8] = 1;
        content[pagesInfo + 9] = (byte)graphicsChunks;

        for (int i = 0; i < count; i++)
        {
            var x = textures[i];
            var tex = x.Data;
            int t = textureAt[i];
            U32(hashes + 4 * i, tex.NameHash);
            U64(list + 8 * i, SystemBase + (ulong)t);

            U32(t, TextureVft);
            U32(t + 0x04, 1);
            U64(t + 0x28, SystemBase + (ulong)nameAt[i]);
            U16(t + 0x30, 1);
            U16(t + 0x32, x.Unknown32);
            U32(t + 0x40, x.UsageData);
            U32(t + 0x48, x.ExtraFlags);
            U16(t + 0x50, tex.Width);
            U16(t + 0x52, tex.Height);
            U16(t + 0x54, 1);
            U16(t + 0x56, Stride(tex.Format, tex.Width));
            U32(t + 0x58, GameFormat(tex.Format));
            content[t + 0x5D] = (byte)tex.MipLevels;
            U64(t + 0x70, GraphicsBase + (ulong)positions[i]);

            Encoding.ASCII.GetBytes(tex.Name, content.AsSpan(nameAt[i]));
            tex.Data.CopyTo(content.AsSpan(systemSize + positions[i]));
        }

        uint systemFlags = ((uint)(Version >> 4) & 0xF) << 28 | MetaDocument.PageFlags(1, systemSize);
        uint graphicsFlags = ((uint)Version & 0xF) << 28 | MetaDocument.PageFlags(graphicsUnits, graphicsBase);
        return ResourceFile.Write(new ResourceHeader(Version, systemFlags, graphicsFlags), content);
    }

    // Pixels of each texture, biggest first, in the first page with room left (pages of one size, a power of two).
    private static (int[] Positions, int PageSize, int PageCount) LayoutPixels(List<int> sizes)
    {
        int pageSize = MinPageSize;
        while (pageSize < sizes.Max())
            pageSize *= 2;
        var used = new List<int>();
        var positions = new int[sizes.Count];
        foreach (int i in Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]))
        {
            int page = used.FindIndex(u => pageSize - u >= sizes[i]);
            if (page < 0)
            {
                page = used.Count;
                used.Add(0);
            }
            positions[i] = page * pageSize + used[page];
            used[page] = Math.Min(pageSize, (used[page] + sizes[i] + 15) & ~15);
        }
        return (positions, pageSize, used.Count);
    }

    // Bytes per row of pixels (for compressed formats: a row of 4 x 4 blocks divided by 4), as in the game's files.
    private static int Stride(TextureFormat format, int width)
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
