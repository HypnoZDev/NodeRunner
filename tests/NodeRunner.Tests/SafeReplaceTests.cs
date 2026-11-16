using System.IO.Compression;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class SafeReplaceTests
{
    private static readonly byte[] Resource = [0x52, 0x53, 0x43, 0x37, .. new byte[12]];

    [Fact]
    public void Adding_a_pack_again_replaces_it_and_leaves_no_copy()
    {
        string root = Directory.CreateTempSubdirectory("nr-pack").FullName;
        try
        {
            string library = Path.Combine(root, "lib");
            string source = Directory.CreateDirectory(Path.Combine(root, "bancs")).FullName;
            File.WriteAllBytes(Path.Combine(source, "banc.ydr"), Resource);
            string target = PropLibrary.AddPack(source, library);
            File.Delete(Path.Combine(source, "banc.ydr"));
            File.WriteAllBytes(Path.Combine(source, "chaise.ydr"), Resource);

            Assert.Equal(target, PropLibrary.AddPack(source, library));
            Assert.Equal(["chaise.ydr"], Directory.GetFiles(target).Select(Path.GetFileName));
            Assert.Equal([target], Directory.GetDirectories(library));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_zip_is_extracted_within_its_limits()
    {
        string root = Directory.CreateTempSubdirectory("nr-zip").FullName;
        try
        {
            Assert.Equal("abc", File.ReadAllText(Path.Combine(Extract(root, "ok", ("stream/a.txt", 3)), "stream", "a.txt")));
            Assert.Throws<InvalidDataException>(() => Extract(root, "out", ("../evil.txt", 3)));
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.Throws<InvalidDataException>(() => Extract(root, "big", ("a.bin", 1 << 20)));
            Assert.Throws<InvalidDataException>(() => Extract(root, "many", ("a", 1), ("b", 1), ("c", 1), ("d", 1), ("e", 1)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Entries of zeros (they compress very well, as in a "zip bomb"); limits: 64 KB, 4 files.
    private static string Extract(string root, string name, params (string Path, int Size)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, size) in entries)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(path == "stream/a.txt" ? "abc"u8 : new byte[size]);
            }
        buffer.Position = 0;
        string folder = Path.Combine(root, name);
        SafeZip.Extract(buffer, folder, 64 * 1024, 4);
        return folder;
    }
}
