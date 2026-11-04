using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Archives;

// Loads the archive keys of the user's game. The key file opens with the AES key found in the executable, or with the
// fingerprint of one of the base archives (the executable differs between stores, the archives do not).
public sealed class GameArchiveKeys : IArchiveKeySource
{
    private const string KeyFileResource = "NodeRunner.ArchiveKeys";
    public const string ExecutableSource = "exe";

    public static readonly string[] ArchiveSources =
        ["x64a.rpf", "x64b.rpf", "x64c.rpf", "x64d.rpf", "x64e.rpf", "x64f.rpf", "x64g.rpf", "x64h.rpf"];

    // What the last Load tried, for error reports: "GTA5.exe : clé absente", "x64a.rpf : lu"...
    public IReadOnlyList<string> Attempts { get; private set; } = [];

    // Source that opened the key file ("exe" or an archive name), null if none did.
    public string? UsedSource { get; private set; }

    public ArchiveKeys Load(GameInstallation installation, IProgress<string>? progress)
    {
        using var stream = typeof(GameArchiveKeys).Assembly.GetManifestResourceStream(KeyFileResource)
            ?? throw new InvalidOperationException("Fichier de clés absent de NodeRunner.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] file = buffer.ToArray();

        string exeName = installation.Edition == GameEdition.Enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe";
        var tried = new List<string>();
        Attempts = tried;
        string? last = null;
        var keys = ArchiveKeyFile.Read(file, source =>
        {
            last = source;
            if (source == ExecutableSource)
            {
                string exePath = Path.Combine(installation.FolderPath, exeName);
                byte[]? key = File.Exists(exePath) ? AesKeyFinder.Find(File.ReadAllBytes(exePath)) : null;
                tried.Add(key is null ? $"{exeName} : clé absente" : $"{exeName} : clé trouvée");
                return key;
            }
            byte[]? fingerprint = ArchiveFingerprint.Compute(Path.Combine(installation.FolderPath, source));
            tried.Add(fingerprint is null ? $"{source} : absent ou illisible" : $"{source} : lu");
            return fingerprint;
        });

        UsedSource = keys is null ? null : last;
        return keys ?? throw new InvalidDataException(
            $"Impossible d'ouvrir les archives du jeu : aucune source de clé ne correspond à cette installation "
            + $"({string.Join(" ; ", tried)}).");
    }
}

// SHA-256 of an RPF7 archive's table of contents (header, entries and names, as stored on disk).
public static class ArchiveFingerprint
{
    private const uint Magic = 0x52504637;

    public static byte[]? Compute(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path);
            Span<byte> header = stackalloc byte[16];
            if (RandomAccess.Read(handle, header, 0) != 16 || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
                return null;
            long entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long namesLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            long length = 16 + entryCount * 16 + namesLength;
            if (length > 64 << 20 || length > RandomAccess.GetLength(handle))
                return null;
            var toc = new byte[length];
            if (RandomAccess.Read(handle, toc, 0) != length)
                return null;
            return SHA256.HashData(toc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

// Finds the game's AES key in its executable by fingerprint: a fast 64-bit hash tried at every position, then
// confirmed with SHA-256.
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

// Layout: "NRAK", version, SHA-256 of the content, slot count, slots (source name, IV, content key wrapped with the
// source's key), IV, content (AES key + NG keys + NG tables, deflated, encrypted with the content key).
public static class ArchiveKeyFile
{
    private const uint Magic = 0x4B41524E; // "NRAK"
    private const uint Version = 2;

    public static ArchiveKeys? Read(byte[] file, Func<string, byte[]?> keyFor)
    {
        var reader = new BinaryReader(new MemoryStream(file));
        if (file.Length < 44 || reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
            throw new InvalidDataException("Fichier de clés invalide.");
        byte[] contentHash = reader.ReadBytes(32);
        int slotCount = reader.ReadByte();
        var slots = new List<(string Source, byte[] Iv, byte[] Wrapped)>();
        for (int i = 0; i < slotCount; i++)
        {
            string source = Encoding.ASCII.GetString(reader.ReadBytes(reader.ReadByte()));
            slots.Add((source, reader.ReadBytes(16), reader.ReadBytes(48)));
        }
        byte[] contentIv = reader.ReadBytes(16);
        byte[] encrypted = reader.ReadBytes(file.Length - (int)reader.BaseStream.Position);

        foreach (var (source, iv, wrapped) in slots)
        {
            byte[]? key = keyFor(source);
            if (key is null)
                continue;
            byte[]? content = TryOpen(key, iv, wrapped, contentIv, encrypted);
            if (content is null || !SHA256.HashData(content).AsSpan().SequenceEqual(contentHash))
                continue;

            int ngKeysLength = ArchiveKeys.NgKeyCount * ArchiveKeys.NgKeyLength;
            if (content.Length != ArchiveKeys.AesKeyLength + ngKeysLength + ArchiveKeys.NgTablesLength)
                throw new InvalidDataException("Fichier de clés invalide.");
            return new ArchiveKeys(content[..ArchiveKeys.AesKeyLength],
                content.AsSpan(ArchiveKeys.AesKeyLength, ngKeysLength),
                content.AsSpan(ArchiveKeys.AesKeyLength + ngKeysLength));
        }
        return null;
    }

    private static byte[]? TryOpen(byte[] key, byte[] iv, byte[] wrapped, byte[] contentIv, byte[] encrypted)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] contentKey = aes.DecryptCbc(wrapped, iv);
            if (contentKey.Length != 32)
                return null;
            aes.Key = contentKey;
            byte[] compressed = aes.DecryptCbc(encrypted, contentIv);
            using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return null;
        }
    }

    public static byte[] Write(byte[] aesKey, ReadOnlySpan<byte> ngKeys, ReadOnlySpan<byte> ngTables,
        IReadOnlyList<(string Source, byte[] Key)> sources)
    {
        byte[] content = [.. aesKey, .. ngKeys, .. ngTables];
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(content);

        byte[] contentKey = RandomNumberGenerator.GetBytes(32);
        using var aes = Aes.Create();
        using var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(SHA256.HashData(content));
        writer.Write((byte)sources.Count);
        foreach (var (source, key) in sources)
        {
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            aes.Key = key;
            writer.Write((byte)source.Length);
            writer.Write(Encoding.ASCII.GetBytes(source));
            writer.Write(iv);
            writer.Write(aes.EncryptCbc(contentKey, iv));
        }
        byte[] contentIv = RandomNumberGenerator.GetBytes(16);
        aes.Key = contentKey;
        writer.Write(contentIv);
        writer.Write(aes.EncryptCbc(compressed.ToArray(), contentIv));
        writer.Flush();
        return output.ToArray();
    }
}
