namespace NodeRunner.Data.Native.Archives;

// Keys needed to read the game's RPF archives: one AES-256 key, and the "NG" cipher's 101 keys with its 17 rounds of
// 16 lookup tables.
public sealed class ArchiveKeys
{
    public const int AesKeyLength = 32;
    public const int NgKeyCount = 101;
    public const int NgRounds = 17;
    public const int NgTablesPerRound = 16;

    public const int NgKeyLength = NgRounds * 16;

    public const int NgTablesLength = NgRounds * NgTablesPerRound * 256 * 4;

    private readonly uint[] _ngKeys;   // 101 keys of 68 uints
    private readonly uint[] _ngTables; // [round][table][byte], flattened

    public ArchiveKeys(byte[] aesKey, ReadOnlySpan<byte> ngKeys, ReadOnlySpan<byte> ngTables)
    {
        if (aesKey.Length != AesKeyLength)
            throw new ArgumentException("Clé AES invalide.", nameof(aesKey));
        if (ngKeys.Length != NgKeyCount * NgKeyLength)
            throw new ArgumentException("Clés NG invalides.", nameof(ngKeys));
        if (ngTables.Length != NgTablesLength)
            throw new ArgumentException("Tables NG invalides.", nameof(ngTables));

        AesKey = aesKey;
        _ngKeys = ToUInts(ngKeys);
        _ngTables = ToUInts(ngTables);
    }

    public byte[] AesKey { get; }

    internal ReadOnlySpan<uint> NgKey(int index) => _ngKeys.AsSpan(index * (NgKeyLength / 4), NgKeyLength / 4);

    internal ReadOnlySpan<uint> NgTables => _ngTables;

    private static uint[] ToUInts(ReadOnlySpan<byte> bytes)
    {
        var values = new uint[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
        return values;
    }
}
