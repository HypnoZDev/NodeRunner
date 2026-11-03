namespace NodeRunner.Data.Native.Resources;

// Reader for "Meta" resources (.ymt, .ymap and .ytyp in their binary form).
internal readonly ref struct MetaResource
{
    // Header (after the 16-byte resource file base).
    private const int RootBlockIndexOffset = 0x1C;
    private const int StructureInfosPointerOffset = 0x20;
    private const int DataBlocksPointerOffset = 0x30;
    private const int StructureInfosCountOffset = 0x48;
    private const int DataBlocksCountOffset = 0x4C;
    private const int HeaderSize = 0x70;

    private const int StructureInfoSize = 0x20;
    private const int DataBlockInfoSize = 0x10;

    private readonly int _structureInfosOffset;
    private readonly int _structureInfosCount;
    private readonly int _dataBlocksOffset;
    private readonly int _dataBlocksCount;

    public ResourceData Data { get; }

    public MetaResource(ReadOnlySpan<byte> data)
    {
        Data = new ResourceData(data);
        if (Data.Length < HeaderSize)
            throw new InvalidDataException("Fichier Meta trop court.");

        _structureInfosCount = Data.UInt16(StructureInfosCountOffset);
        _dataBlocksCount = Data.UInt16(DataBlocksCountOffset);
        _structureInfosOffset = Data.ResolveArray(Data.UInt64(StructureInfosPointerOffset), _structureInfosCount, StructureInfoSize, "les structures");
        _dataBlocksOffset = Data.ResolveArray(Data.UInt64(DataBlocksPointerOffset), _dataBlocksCount, DataBlockInfoSize, "les blocs");
    }

    public int RootBlock => (int)Data.UInt32(RootBlockIndexOffset) - 1;

    public uint BlockStructure(int block) => Data.UInt32(BlockInfo(block));

    public int BlockLength(int block) => (int)Data.UInt32(BlockInfo(block) + 4);

    public int BlockStart(int block)
    {
        int length = BlockLength(block);
        return Data.ResolveArray(Data.UInt64(BlockInfo(block) + 8), Math.Max(length, 1), 1, $"le bloc {block + 1}");
    }

    public int StructureSize(uint structureHash)
    {
        for (int i = 0; i < _structureInfosCount; i++)
        {
            int o = _structureInfosOffset + i * StructureInfoSize;
            if (Data.UInt32(o) == structureHash)
                return (int)Data.UInt32(o + 0x18);
        }
        return -1;
    }

    public void RequireStructure(uint structureHash, int expectedSize, string what)
    {
        int size = StructureSize(structureHash);
        if (size != expectedSize)
            throw new InvalidDataException($"Structure {what} inattendue (taille {size}, attendue {expectedSize}).");
    }

    // Resolves an array field (16 bytes: pointer, count) located at fieldOffset.
    public (int Offset, int Count) ReadArray(int fieldOffset, int itemSize)
    {
        ulong pointer = Data.UInt64(fieldOffset);
        int count = Data.UInt16(fieldOffset + 8);
        if (count == 0 || pointer == 0)
            return (-1, 0);

        int block = (int)(pointer & 0xFFF) - 1;
        int offsetInBlock = (int)((pointer >> 12) & 0xFFFFF);
        if (block < 0 || block >= _dataBlocksCount)
            throw new InvalidDataException($"Pointeur Meta vers un bloc inexistant ({block + 1}).");
        if (offsetInBlock + count * itemSize > BlockLength(block))
            throw new InvalidDataException($"Tableau Meta hors de son bloc ({count} éléments).");

        return (BlockStart(block) + offsetInBlock, count);
    }

    // Resolves an array of pointers (16-byte field) at fieldOffset: each item points to a structure in another block,
    // possibly of different types.
    public List<(int Offset, uint Structure)> ReadPointerArray(int fieldOffset)
    {
        const int pointerSize = 8;
        var (offset, count) = ReadArray(fieldOffset, pointerSize);
        var items = new List<(int, uint)>(count);
        for (int i = 0; i < count; i++)
        {
            ulong pointer = Data.UInt64(offset + i * pointerSize);
            int block = (int)(pointer & 0xFFF) - 1;
            int offsetInBlock = (int)((pointer >> 12) & 0xFFFFF);
            if (block < 0)
                continue; // Null pointer.
            if (offsetInBlock >= BlockLength(block))
                throw new InvalidDataException($"Pointeur Meta hors de son bloc ({block + 1}).");
            items.Add((BlockStart(block) + offsetInBlock, BlockStructure(block)));
        }
        return items;
    }

    public string String(int fieldOffset)
    {
        var (start, count) = ReadArray(fieldOffset, 1);
        if (count == 0)
            return "";
        var chars = new char[count];
        int length = 0;
        for (; length < count; length++)
        {
            byte b = Data.Byte(start + length);
            if (b == 0)
                break;
            chars[length] = (char)b;
        }
        return new string(chars, 0, length);
    }

    public float Float(int offset) => BitConverter.UInt32BitsToSingle(Data.UInt32(offset));

    public System.Numerics.Vector3 Vector3(int offset) => new(Float(offset), Float(offset + 4), Float(offset + 8));

    private int BlockInfo(int block)
    {
        if (block < 0 || block >= _dataBlocksCount)
            throw new InvalidDataException($"Bloc Meta inexistant ({block + 1}).");
        return _dataBlocksOffset + block * DataBlockInfoSize;
    }
}
