using System.Buffers.Binary;
using System.Security.Cryptography;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Archives;

// How an archive (its tables, and the files flagged as encrypted) is encrypted.
public enum ArchiveEncryption
{
    None,
    Aes,
    Ng,
}

// Decryption of archive data. Both ciphers work on 16-byte blocks; trailing bytes that do not fill a block are stored
// in clear.
public static class ArchiveCipher
{
    public static void Decrypt(ArchiveEncryption encryption, ArchiveKeys keys, Span<byte> data, string name, uint length)
    {
        switch (encryption)
        {
            case ArchiveEncryption.Aes:
                DecryptAes(keys.AesKey, data);
                break;
            case ArchiveEncryption.Ng:
                DecryptNg(keys, data, name, length);
                break;
        }
    }

    public static void DecryptAes(byte[] key, Span<byte> data)
    {
        int length = data.Length & ~15;
        if (length == 0)
            return;
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptEcb(data[..length], PaddingMode.None);
        plain.CopyTo(data);
    }

    // The NG cipher: the key is chosen from the name (file or archive name) and its length, then each block goes
    // through 17 rounds of table lookups.
    public static void DecryptNg(ArchiveKeys keys, Span<byte> data, string name, uint length)
    {
        var key = keys.NgKey((int)((Joaat.Hash(name) + length) % ArchiveKeys.NgKeyCount));
        var tables = keys.NgTables;
        for (int offset = 0; offset + 16 <= data.Length; offset += 16)
            DecryptNgBlock(data.Slice(offset, 16), key, tables);
    }

    private static void DecryptNgBlock(Span<byte> block, ReadOnlySpan<uint> key, ReadOnlySpan<uint> tables)
    {
        // Rounds 0, 1 and 16 mix the bytes of each column; rounds 2 to 15 mix across columns.
        Round(block, key, tables, 0, straight: true);
        Round(block, key, tables, 1, straight: true);
        for (int round = 2; round < 16; round++)
            Round(block, key, tables, round, straight: false);
        Round(block, key, tables, 16, straight: true);
    }

    private static void Round(Span<byte> b, ReadOnlySpan<uint> key, ReadOnlySpan<uint> tables, int round, bool straight)
    {
        var t = tables.Slice(round * ArchiveKeys.NgTablesPerRound * 256, ArchiveKeys.NgTablesPerRound * 256);
        int k = round * 4;
        uint x1, x2, x3, x4;
        if (straight)
        {
            x1 = t[b[0]] ^ t[256 + b[1]] ^ t[512 + b[2]] ^ t[768 + b[3]] ^ key[k];
            x2 = t[1024 + b[4]] ^ t[1280 + b[5]] ^ t[1536 + b[6]] ^ t[1792 + b[7]] ^ key[k + 1];
            x3 = t[2048 + b[8]] ^ t[2304 + b[9]] ^ t[2560 + b[10]] ^ t[2816 + b[11]] ^ key[k + 2];
            x4 = t[3072 + b[12]] ^ t[3328 + b[13]] ^ t[3584 + b[14]] ^ t[3840 + b[15]] ^ key[k + 3];
        }
        else
        {
            x1 = t[b[0]] ^ t[1792 + b[7]] ^ t[2560 + b[10]] ^ t[3328 + b[13]] ^ key[k];
            x2 = t[256 + b[1]] ^ t[1024 + b[4]] ^ t[2816 + b[11]] ^ t[3584 + b[14]] ^ key[k + 1];
            x3 = t[512 + b[2]] ^ t[1280 + b[5]] ^ t[2048 + b[8]] ^ t[3840 + b[15]] ^ key[k + 2];
            x4 = t[768 + b[3]] ^ t[1536 + b[6]] ^ t[2304 + b[9]] ^ t[3072 + b[12]] ^ key[k + 3];
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b, x1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], x2);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], x3);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], x4);
    }
}
