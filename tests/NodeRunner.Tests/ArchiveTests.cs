using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

// RPF archives built in memory (no game file): layout, compression, nested archives, AES encryption.
public class ArchiveTests : IDisposable
{
    private static readonly byte[] AesKey = RandomNumberGenerator.GetBytes(32);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noderunner-rpf-" + Guid.NewGuid().ToString("N"));

    public ArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly byte[] PlainText = Encoding.ASCII.GetBytes("stored as is");
    private static readonly byte[] XmlText = Encoding.ASCII.GetBytes("<CMapTypes>" + new string('x', 3000) + "</CMapTypes>");
    private static readonly byte[] ResourceContent = Enumerable.Range(0, 0x2000).Select(i => (byte)i).ToArray();
    private static readonly uint ResourceSystemFlags = MetaDocument.PageFlags(1, 0x2000);
    private const uint ResourceGraphicsFlags = 2u << 28; // version 2, no graphics page

    [Fact]
    public void Open_ReadsFilesDirectoriesAndNestedArchives()
    {
        File.WriteAllBytes(Path.Combine(_folder, "test.rpf"), BuildArchive(encrypted: false));
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));
        File.WriteAllBytes(Path.Combine(_folder, "mods", "modded.rpf"), BuildArchive(encrypted: false));

        using var files = Open();

        Assert.Empty(files.Errors);
        Assert.Equal(["test.rpf", @"test.rpf\inner.rpf"], files.EnumerateArchives().Order());
        Assert.Equal(PlainText, files.ReadFile(@"test.rpf\a.txt"));
        Assert.Equal(XmlText, files.ReadFile(@"TEST.RPF\B.XML"));
        Assert.Equal(PlainText, files.ReadFile(@"test.rpf\sub\d.txt"));
        Assert.Equal(PlainText, files.ReadFile(@"test.rpf\inner.rpf\e.txt"));
        Assert.Equal(ResourceContent, files.ReadFile(@"test.rpf\c.ymap"));
        Assert.Null(files.ReadFile(@"test.rpf\missing.txt"));
        Assert.False(files.FileExists(@"test.rpf\sub"));
        Assert.Equal([@"test.rpf\a.txt", @"test.rpf\inner.rpf\e.txt", @"test.rpf\sub\d.txt"], files.EnumerateFiles("txt").Order());
    }

    [Fact]
    public void GetResourceHeader_GivesVersionAndPageFlags()
    {
        File.WriteAllBytes(Path.Combine(_folder, "test.rpf"), BuildArchive(encrypted: false));
        using var files = Open();

        Assert.Equal(new ResourceHeader(2, ResourceSystemFlags, ResourceGraphicsFlags), files.GetResourceHeader(@"test.rpf\c.ymap"));
        Assert.Null(files.GetResourceHeader(@"test.rpf\a.txt"));
    }

    [Fact]
    public void Open_DecryptsAesArchives()
    {
        File.WriteAllBytes(Path.Combine(_folder, "test.rpf"), BuildArchive(encrypted: true));
        using var files = Open();

        Assert.Empty(files.Errors);
        Assert.Equal(XmlText, files.ReadFile(@"test.rpf\b.xml"));
        Assert.Equal(PlainText, files.ReadFile(@"test.rpf\inner.rpf\e.txt"));
    }

    [Fact]
    public void Open_SkipsDamagedArchives()
    {
        File.WriteAllBytes(Path.Combine(_folder, "test.rpf"), BuildArchive(encrypted: false));
        File.WriteAllBytes(Path.Combine(_folder, "broken.rpf"), RandomNumberGenerator.GetBytes(4096));
        using var files = Open();

        Assert.Single(files.Errors);
        Assert.StartsWith("broken.rpf", files.Errors[0]);
        Assert.Equal(PlainText, files.ReadFile(@"test.rpf\a.txt"));
    }

    [Theory]
    [InlineData(1, 0x2000)]
    [InlineData(5, 0x4000)]
    [InlineData(37, 0x2000)]
    [InlineData(127, 0x10000)]
    public void ResourcePages_SizeMatchesTheFlagsWeWrite(int pageCount, int pageSize)
    {
        Assert.Equal(pageCount * pageSize, ResourcePages.Size(MetaDocument.PageFlags(pageCount, pageSize)));
    }

    [Fact]
    public void AesKeyFinder_FindsTheKeyByItsFingerprints()
    {
        var exe = RandomNumberGenerator.GetBytes(100_000);
        AesKey.CopyTo(exe, 54_321);

        var found = AesKeyFinder.Find(exe, AesKeyFinder.Fingerprint(AesKey), SHA256.HashData(AesKey));
        Assert.Equal(AesKey, found);

        var other = RandomNumberGenerator.GetBytes(32);
        Assert.Null(AesKeyFinder.Find(exe, AesKeyFinder.Fingerprint(other), SHA256.HashData(other)));
    }

    [Fact]
    public void ArchiveKeyFile_RoundTripsAndRejectsAnotherGame()
    {
        var ngKeys = RandomNumberGenerator.GetBytes(ArchiveKeys.NgKeyCount * ArchiveKeys.NgKeyLength);
        var ngTables = RandomNumberGenerator.GetBytes(ArchiveKeys.NgTablesLength);
        var file = ArchiveKeyFile.Write(AesKey, ngKeys, ngTables);

        var keys = ArchiveKeyFile.Read(file, AesKey);
        Assert.Equal(AesKey, keys.AesKey);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(ngKeys.AsSpan(ArchiveKeys.NgKeyLength)), keys.NgKey(1)[0]);

        Assert.Throws<InvalidDataException>(() => ArchiveKeyFile.Read(file, RandomNumberGenerator.GetBytes(32)));
    }

    private RpfFileSystem Open()
    {
        var files = new RpfFileSystem(new TestKeys());
        files.Open(new GameInstallation(_folder, GameEdition.Legacy, "Test"));
        return files;
    }

    private sealed class TestKeys : IArchiveKeySource
    {
        public ArchiveKeys Load(GameInstallation installation, IProgress<string>? progress) =>
            new(AesKey, new byte[ArchiveKeys.NgKeyCount * ArchiveKeys.NgKeyLength], new byte[ArchiveKeys.NgTablesLength]);
    }

    // Root: a.txt (stored), b.xml (compressed), c.ymap (resource), sub\d.txt, inner.rpf (an archive with e.txt).
    private static byte[] BuildArchive(bool encrypted)
    {
        var inner = new ArchiveWriter();
        inner.Directory("", 1, 1);
        inner.Binary("e.txt", PlainText, compress: false);

        var outer = new ArchiveWriter();
        outer.Directory("", 1, 5);
        outer.Binary("a.txt", PlainText, compress: false);
        outer.Binary("b.xml", XmlText, compress: true);
        outer.Resource("c.ymap", ResourceFile.Write(new ResourceHeader(2, ResourceSystemFlags, ResourceGraphicsFlags), ResourceContent),
            ResourceSystemFlags, ResourceGraphicsFlags);
        outer.Directory("sub", 6, 1);
        outer.Binary("inner.rpf", inner.Build(encrypted), compress: false);
        outer.Binary("d.txt", PlainText, compress: false);
        return outer.Build(encrypted);
    }

    // Writes an RPF7 archive: entries are listed in the order of the calls.
    private sealed class ArchiveWriter
    {
        private delegate void EntryWriter(Span<byte> entry, int nameOffset, long block);

        private readonly List<(string Name, EntryWriter Write, byte[]? Data)> _entries = [];

        public void Directory(string name, int first, int count) => _entries.Add((name, (e, n, _) =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)n);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 0x7FFFFF00);
            BinaryPrimitives.WriteInt32LittleEndian(e[8..], first);
            BinaryPrimitives.WriteInt32LittleEndian(e[12..], count);
        }, null));

        public void Binary(string name, byte[] content, bool compress)
        {
            byte[] stored = compress ? Deflate(content) : content;
            _entries.Add((name, (e, n, block) =>
            {
                ulong size = compress ? (ulong)stored.Length : 0;
                BinaryPrimitives.WriteUInt64LittleEndian(e, (ulong)n | size << 16 | (ulong)block << 40);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)content.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(e[12..], 0);
            }, stored));
        }

        public void Resource(string name, byte[] rsc7, uint systemFlags, uint graphicsFlags) => _entries.Add((name, (e, n, block) =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(e, (ulong)n | (ulong)rsc7.Length << 16 | ((ulong)block | 0x800000) << 40);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], systemFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], graphicsFlags);
        }, rsc7));

        public byte[] Build(bool encrypted)
        {
            var names = new MemoryStream();
            var nameOffsets = new List<int>();
            foreach (var (name, _, _) in _entries)
            {
                nameOffsets.Add((int)names.Length);
                names.Write(Encoding.ASCII.GetBytes(name + "\0"));
            }
            while (names.Length % 16 != 0)
                names.WriteByte(0);

            var entries = new byte[_entries.Count * 16];
            var output = new MemoryStream();
            long position = Align(16 + entries.Length + names.Length);
            var placed = new List<(long Position, byte[] Data)>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var (_, write, data) = _entries[i];
                long block = data is null ? 0 : position / 512;
                write(entries.AsSpan(i * 16, 16), nameOffsets[i], block);
                if (data is not null)
                {
                    placed.Add((position, data));
                    position = Align(position + data.Length);
                }
            }

            byte[] nameBytes = names.ToArray();
            if (encrypted)
            {
                using var aes = Aes.Create();
                aes.Key = AesKey;
                entries = aes.EncryptEcb(entries, PaddingMode.None);
                nameBytes = aes.EncryptEcb(nameBytes, PaddingMode.None);
            }

            var file = new byte[position];
            BinaryPrimitives.WriteUInt32LittleEndian(file, 0x52504637);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), _entries.Count);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), encrypted ? 0x0FFFFFF9u : 0x4E45504Fu);
            entries.CopyTo(file, 16);
            nameBytes.CopyTo(file, 16 + entries.Length);
            foreach (var (at, data) in placed)
                data.CopyTo(file, at);
            return file;
        }

        private static long Align(long value) => (value + 511) / 512 * 512;

        private static byte[] Deflate(byte[] content)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(content);
            return output.ToArray();
        }
    }
}
