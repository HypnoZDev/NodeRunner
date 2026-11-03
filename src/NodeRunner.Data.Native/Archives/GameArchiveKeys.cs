using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Archives;

/// <summary>
/// Loads the archive keys of the user's game. The AES key is found in the user's own executable; the NG keys and
/// tables, which today's executables only build in memory, come from NodeRunner's key file, itself encrypted with
/// that AES key: it is useless without the game.
/// </summary>
public sealed class GameArchiveKeys : IArchiveKeySource
{
    private const string KeyFileResource = "NodeRunner.ArchiveKeys";

    public ArchiveKeys Load(GameInstallation installation, IProgress<string>? progress)
    {
        string exeName = installation.Edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe";
        byte[] exe = File.ReadAllBytes(Path.Combine(installation.FolderPath, exeName));
        byte[] aesKey = AesKeyFinder.Find(exe)
            ?? throw new InvalidDataException($"Clé introuvable dans {exeName} : cette version du jeu n'est pas encore prise en charge.");

        using var stream = typeof(GameArchiveKeys).Assembly.GetManifestResourceStream(KeyFileResource)
            ?? throw new InvalidOperationException("Fichier de clés absent de NodeRunner.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ArchiveKeyFile.Read(buffer.ToArray(), aesKey);
    }
}

/// <summary>
/// Finds the game's AES key in its executable by fingerprint: a fast 64-bit hash tried at every position, then
/// confirmed with SHA-256. Only the fingerprints are stored in NodeRunner, never the key.
/// </summary>
public static class AesKeyFinder
{
    private const ulong QuickFingerprint = 0x33FA04D4B6B0E91C;
    private static readonly byte[] Sha256Fingerprint = Convert.FromHexString(
        "1BC7625FC374717D47FA98C801562180873294A9D9DE9EEDB18876A208AFE077");

    public static byte[]? Find(ReadOnlySpan<byte> exe) => Find(exe, QuickFingerprint, Sha256Fingerprint);

    internal static byte[]? Find(ReadOnlySpan<byte> exe, ulong quick, ReadOnlySpan<byte> sha256)
    {
        Span<byte> hash = stackalloc byte[32];
        for (int i = 0; i + ArchiveKeys.AesKeyLength <= exe.Length; i++)
        {
            var candidate = exe.Slice(i, ArchiveKeys.AesKeyLength);
            if (Fingerprint(candidate) != quick)
                continue;
            SHA256.HashData(candidate, hash);
            if (hash.SequenceEqual(sha256))
                return candidate.ToArray();
        }
        return null;
    }

    /// <summary>Fast 64-bit hash of a 32-byte block.</summary>
    public static ulong Fingerprint(ReadOnlySpan<byte> block)
    {
        ulong h = 0x9E3779B97F4A7C15;
        for (int i = 0; i < 4; i++)
        {
            h ^= BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8)) * 0xC2B2AE3D27D4EB4F;
            h = BitOperations.RotateLeft(h, 31) * 0x9E3779B97F4A7C15;
        }
        return h ^ (h >> 29);
    }
}

/// <summary>
/// NodeRunner's key file: "NRAK", format version, 16-byte IV, SHA-256 of the content, then the content
/// (the 101 NG keys in the game's order followed by the NG tables) compressed and encrypted with AES-256-CBC
/// using the game's AES key.
/// </summary>
public static class ArchiveKeyFile
{
    private const uint Magic = 0x4B41524E; // "NRAK"
    private const uint Version = 1;
    private const int HeaderLength = 8 + 16 + 32;

    public static ArchiveKeys Read(byte[] file, byte[] aesKey)
    {
        if (file.Length <= HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) != Version)
            throw new InvalidDataException("Fichier de clés invalide.");

        byte[] content;
        try
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;
            byte[] compressed = aes.DecryptCbc(file.AsSpan(HeaderLength), file.AsSpan(8, 16));
            using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            content = output.ToArray();
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            throw new InvalidDataException("Le fichier de clés ne correspond pas à ce jeu.", ex);
        }

        int ngKeysLength = ArchiveKeys.NgKeyCount * ArchiveKeys.NgKeyLength;
        if (content.Length != ngKeysLength + ArchiveKeys.NgTablesLength
            || !SHA256.HashData(content).AsSpan().SequenceEqual(file.AsSpan(24, 32)))
            throw new InvalidDataException("Le fichier de clés ne correspond pas à ce jeu.");

        return new ArchiveKeys(aesKey, content.AsSpan(0, ngKeysLength), content.AsSpan(ngKeysLength));
    }

    public static byte[] Write(byte[] aesKey, ReadOnlySpan<byte> ngKeys, ReadOnlySpan<byte> ngTables)
    {
        byte[] content = [.. ngKeys, .. ngTables];
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(content);

        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = aesKey;
        byte[] encrypted = aes.EncryptCbc(compressed.ToArray(), iv);

        var file = new byte[HeaderLength + encrypted.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), Version);
        iv.CopyTo(file, 8);
        SHA256.HashData(content).CopyTo(file, 24);
        encrypted.CopyTo(file, HeaderLength);
        return file;
    }
}
