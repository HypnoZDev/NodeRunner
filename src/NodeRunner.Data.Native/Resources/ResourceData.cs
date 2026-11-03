using System.Buffers.Binary;

namespace NodeRunner.Data.Native.Resources;

/// <summary>
/// Bounds-checked little-endian reads in decompressed resource data (the content of a RSC7 file).
/// Pointers inside a resource are "virtual" addresses: 0x50000000 + offset in the system segment, 0x60000000 +
/// offset in the graphics segment (pixels, vertices...), which follows the system pages in the decompressed data.
/// The graphics segment is only reachable when its start is given.
/// </summary>
internal readonly ref struct ResourceData
{
    private const ulong SystemBase = 0x50000000;
    private const ulong GraphicsBase = 0x60000000;
    private const ulong SegmentMask = 0xF0000000;

    private readonly ReadOnlySpan<byte> _data;
    private readonly int _graphicsStart;

    public ResourceData(ReadOnlySpan<byte> data, int graphicsStart = -1)
    {
        _data = data;
        _graphicsStart = graphicsStart;
    }

    public int Length => _data.Length;

    public byte Byte(int offset) => Slice(offset, 1)[0];
    public ushort UInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
    public short Int16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Slice(offset, 2));
    public uint UInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    public ulong UInt64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));
    public ReadOnlySpan<byte> Bytes(int offset, int length) => Slice(offset, length);

    /// <summary>Null-terminated ASCII text at a pointer; null for a null pointer.</summary>
    public string? Text(ulong pointer, string what)
    {
        if (pointer == 0)
            return null;
        int start = ResolveArray(pointer, 1, 1, what);
        int length = _data[start..].IndexOf((byte)0);
        if (length < 0)
            throw new InvalidDataException($"{what} : texte sans fin.");
        return System.Text.Encoding.ASCII.GetString(_data.Slice(start, length));
    }

    /// <summary>
    /// Converts a resource pointer to an offset in the data, checking that <paramref name="count"/> items
    /// of <paramref name="itemSize"/> bytes fit. Returns -1 for a null pointer with no items.
    /// </summary>
    public int ResolveArray(ulong pointer, long count, int itemSize, string what)
    {
        if (count == 0)
            return -1;
        long offset = (pointer & SegmentMask) switch
        {
            SystemBase => (long)(pointer - SystemBase),
            GraphicsBase when _graphicsStart >= 0 => _graphicsStart + (long)(pointer - GraphicsBase),
            _ => throw new InvalidDataException($"Pointeur invalide pour {what} : 0x{pointer:X}."),
        };
        long end = offset + count * itemSize;
        if (offset < 0 || end > _data.Length)
            throw new InvalidDataException($"{what} dépasse la fin du fichier ({count} éléments à 0x{offset:X}).");
        return (int)offset;
    }

    private ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if (offset < 0 || offset + length > _data.Length)
            throw new InvalidDataException($"Lecture hors du fichier à la position 0x{offset:X}.");
        return _data.Slice(offset, length);
    }
}
