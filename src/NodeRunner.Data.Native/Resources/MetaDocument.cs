using System.Buffers.Binary;
using System.Text;

namespace NodeRunner.Data.Native.Resources;

// Editable form of a "Meta" resource (.ymap, .ytyp, .ymt): its structure and enum descriptions, kept as they are, and
// its data blocks, which can be changed, grown or added.
public sealed class MetaDocument
{
    // Resource file base (first 16 bytes) and Meta header.
    private const int PagesInfoPointerOffset = 0x08;
    private const int RootBlockOffset = 0x1C;
    private const int StructureInfosPointerOffset = 0x20;
    private const int EnumInfosPointerOffset = 0x28;
    private const int DataBlocksPointerOffset = 0x30;
    private const int NamePointerOffset = 0x38;
    private const int UselessDataPointerOffset = 0x40;
    private const int StructureInfosCountOffset = 0x48;
    private const int EnumInfosCountOffset = 0x4A;
    private const int DataBlocksCountOffset = 0x4C;
    private const int HeaderSize = 0x70;

    private const int StructureInfoSize = 0x20;
    private const int StructureEntrySize = 0x10;
    private const int EnumInfoSize = 0x18;
    private const int EnumEntrySize = 0x08;
    private const int DataBlockInfoSize = 0x10;
    private const int PagesInfoHeaderSize = 0x10;
    private const int PagesInfoEntrySize = 0x08;

    private const ulong SystemBase = 0x50000000;

    public const int MaxBlockLength = 0x4000;

    // Page layout, as the game files are commonly rebuilt: one page size, chosen to fit the largest item.
    private const int MinPageSize = 0x2000;
    private const int MaxPageCount = 127;
    private const int Alignment = 16;
    private const int PageMargin = 16;

    // A structure as the file describes it: description (32 bytes) and its fields (16 bytes each).
    public sealed record StructureInfo(byte[] Info, byte[] Entries)
    {
        public uint NameHash => BinaryPrimitives.ReadUInt32LittleEndian(Info);
        public int Size => BinaryPrimitives.ReadInt32LittleEndian(Info.AsSpan(0x18));
    }

    // An enum as the file describes it: description (24 bytes) and its values (8 bytes each).
    public sealed record EnumInfo(byte[] Info, byte[] Entries);

    // A data block: items of one structure, back to back.
    public sealed class Block(uint structure, byte[] data)
    {
        public uint Structure { get; } = structure;
        public byte[] Data { get; set; } = data;
    }

    private byte[] _header = new byte[HeaderSize];

    public List<StructureInfo> Structures { get; } = new();
    public List<EnumInfo> Enums { get; } = new();
    public List<Block> Blocks { get; } = new();
    public string? Name { get; set; }

    public int RootBlock
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(RootBlockOffset)) - 1;
        set => BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(RootBlockOffset), value + 1);
    }

    public static MetaDocument Read(ReadOnlySpan<byte> data)
    {
        var d = new ResourceData(data);
        if (d.Length < HeaderSize)
            throw new InvalidDataException("Fichier Meta trop court.");

        var document = new MetaDocument { _header = data[..HeaderSize].ToArray() };

        int structureCount = d.UInt16(StructureInfosCountOffset);
        int structures = d.ResolveArray(d.UInt64(StructureInfosPointerOffset), structureCount, StructureInfoSize, "les structures");
        for (int i = 0; i < structureCount; i++)
        {
            int o = structures + i * StructureInfoSize;
            int entryCount = d.UInt16(o + 0x1E);
            int entries = d.ResolveArray(d.UInt64(o + 0x10), entryCount, StructureEntrySize, "les champs d'une structure");
            document.Structures.Add(new StructureInfo(
                data.Slice(o, StructureInfoSize).ToArray(),
                entryCount == 0 ? [] : data.Slice(entries, entryCount * StructureEntrySize).ToArray()));
        }

        int enumCount = d.UInt16(EnumInfosCountOffset);
        int enums = d.ResolveArray(d.UInt64(EnumInfosPointerOffset), enumCount, EnumInfoSize, "les énumérations");
        for (int i = 0; i < enumCount; i++)
        {
            int o = enums + i * EnumInfoSize;
            int entryCount = (int)d.UInt32(o + 0x10);
            int entries = d.ResolveArray(d.UInt64(o + 0x08), entryCount, EnumEntrySize, "les valeurs d'une énumération");
            document.Enums.Add(new EnumInfo(
                data.Slice(o, EnumInfoSize).ToArray(),
                entryCount == 0 ? [] : data.Slice(entries, entryCount * EnumEntrySize).ToArray()));
        }

        int blockCount = d.UInt16(DataBlocksCountOffset);
        int blocks = d.ResolveArray(d.UInt64(DataBlocksPointerOffset), blockCount, DataBlockInfoSize, "les blocs");
        for (int i = 0; i < blockCount; i++)
        {
            int o = blocks + i * DataBlockInfoSize;
            int length = (int)d.UInt32(o + 4);
            int start = d.ResolveArray(d.UInt64(o + 8), length, 1, $"le bloc {i + 1}");
            document.Blocks.Add(new Block(d.UInt32(o), length == 0 ? [] : data.Slice(start, length).ToArray()));
        }

        ulong namePointer = d.UInt64(NamePointerOffset);
        if (namePointer != 0)
        {
            int start = d.ResolveArray(namePointer, 1, 1, "le nom");
            int end = data[start..].IndexOf((byte)0);
            document.Name = Encoding.ASCII.GetString(data.Slice(start, end < 0 ? data.Length - start : end));
        }
        return document;
    }

    public StructureInfo? FindStructure(uint nameHash) => Structures.Find(s => s.NameHash == nameHash);

    public static ulong Pointer(int block, int offset) => (ulong)(uint)(block + 1) | ((ulong)(uint)offset << 12);

    public static (int Block, int Offset) DecodePointer(ulong pointer) => ((int)(pointer & 0xFFF) - 1, (int)((pointer >> 12) & 0xFFFFF));

    // Lays the resource out again and returns its decompressed content, with the system page flags to put in the
    // resource header.
    public (byte[] Content, uint SystemPageFlags) Write()
    {
        // Pieces of the file, in order: header, descriptions, data blocks, name; the page list comes last because
        // its size depends on the number of pages, which depends on the layout (a couple of passes settle it).
        var pieces = new List<int> { HeaderSize, Structures.Count * StructureInfoSize };
        pieces.AddRange(Structures.Select(s => s.Entries.Length));
        pieces.Add(Enums.Count * EnumInfoSize);
        pieces.AddRange(Enums.Select(e => e.Entries.Length));
        pieces.Add(Blocks.Count * DataBlockInfoSize);
        pieces.AddRange(Blocks.Select(b => b.Data.Length));
        pieces.Add(Name is null ? 0 : Name.Length + 1);

        int pageCount = 1;
        int[] positions;
        int pageSize;
        while (true)
        {
            pieces.Add(PagesInfoHeaderSize + PagesInfoEntrySize * pageCount);
            (positions, pageSize, int count) = Layout(pieces);
            pieces.RemoveAt(pieces.Count - 1);
            if (count == pageCount)
                break;
            pageCount = count;
        }

        var output = new byte[pageCount * pageSize];
        int next = 0;
        int Place(ReadOnlySpan<byte> bytes)
        {
            int position = positions[next++];
            bytes.CopyTo(output.AsSpan(position));
            return position;
        }
        static ulong Address(int position, int length) => length == 0 ? 0 : SystemBase + (ulong)position;

        int header = Place(_header);
        var span = output.AsSpan();

        // Structure descriptions, then their fields (the descriptions point to the fields).
        int structureInfos = Place(Concat(Structures.Select(s => s.Info)));
        for (int i = 0; i < Structures.Count; i++)
        {
            var entries = Structures[i].Entries;
            int at = Place(entries);
            int info = structureInfos + i * StructureInfoSize;
            BinaryPrimitives.WriteUInt64LittleEndian(span[(info + 0x10)..], Address(at, entries.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(span[(info + 0x1E)..], (ushort)(entries.Length / StructureEntrySize));
        }

        int enumInfos = Place(Concat(Enums.Select(e => e.Info)));
        for (int i = 0; i < Enums.Count; i++)
        {
            var entries = Enums[i].Entries;
            int at = Place(entries);
            int info = enumInfos + i * EnumInfoSize;
            BinaryPrimitives.WriteUInt64LittleEndian(span[(info + 0x08)..], Address(at, entries.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(span[(info + 0x10)..], (uint)(entries.Length / EnumEntrySize));
        }

        int blockInfos = Place(new byte[Blocks.Count * DataBlockInfoSize]);
        for (int i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            int at = Place(block.Data);
            int info = blockInfos + i * DataBlockInfoSize;
            BinaryPrimitives.WriteUInt32LittleEndian(span[info..], block.Structure);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(info + 4)..], (uint)block.Data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(info + 8)..], Address(at, block.Data.Length));
        }

        int name = Place(Name is null ? [] : Encoding.ASCII.GetBytes(Name + "\0"));

        // Page list: the counts only (the game fills in the rest when loading).
        var pagesInfo = new byte[PagesInfoHeaderSize + PagesInfoEntrySize * pageCount];
        pagesInfo[8] = (byte)pageCount;
        int pages = Place(pagesInfo);

        var h = span[header..];
        BinaryPrimitives.WriteUInt64LittleEndian(h[PagesInfoPointerOffset..], Address(pages, 1));
        BinaryPrimitives.WriteUInt64LittleEndian(h[StructureInfosPointerOffset..], Address(structureInfos, Structures.Count));
        BinaryPrimitives.WriteUInt64LittleEndian(h[EnumInfosPointerOffset..], Address(enumInfos, Enums.Count));
        BinaryPrimitives.WriteUInt64LittleEndian(h[DataBlocksPointerOffset..], Address(blockInfos, Blocks.Count));
        BinaryPrimitives.WriteUInt64LittleEndian(h[NamePointerOffset..], Address(name, Name is null ? 0 : 1));
        BinaryPrimitives.WriteUInt64LittleEndian(h[UselessDataPointerOffset..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h[StructureInfosCountOffset..], (ushort)Structures.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(h[EnumInfosCountOffset..], (ushort)Enums.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(h[DataBlocksCountOffset..], (ushort)Blocks.Count);

        return (output, PageFlags(pageCount, pageSize));
    }

    // Positions of the pieces: back to back (aligned to 16 bytes) in pages of one size, an item never crossing the
    // end of a page.
    private static (int[] Positions, int PageSize, int PageCount) Layout(List<int> pieces)
    {
        int pageSize = MinPageSize;
        while (pageSize < pieces.Max() + PageMargin)
            pageSize *= 2;

        while (true)
        {
            var positions = new int[pieces.Count];
            int pageCount = 0, position = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                int length = pieces[i];
                if (pageCount * pageSize - position < length + PageMargin)
                {
                    pageCount++;
                    position = (pageCount - 1) * pageSize;
                }
                positions[i] = position;
                position += length;
                position = (position + Alignment - 1) / Alignment * Alignment;
            }
            if (pageCount <= MaxPageCount)
                return (positions, pageSize, pageCount);
            pageSize *= 2;
        }
    }

    // System page flags of the resource header for pageCount pages of pageSize bytes: the base size (0x200 shifted),
    // then the count written as pages of 16, 8, 4, 2 and 1 times that size.
    public static uint PageFlags(int pageCount, int pageSize)
    {
        uint shift = 0;
        while ((0x200 << (int)shift) < pageSize)
            shift++;
        uint count = (uint)pageCount;
        return ((count & 1) << 27) | (((count >> 1) & 1) << 26) | (((count >> 2) & 1) << 25) | (((count >> 3) & 1) << 24)
            | (((count >> 4) & 0x7F) << 17) | shift;
    }

    private static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(p => p).ToArray();
}
