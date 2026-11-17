using System.Buffers.Binary;
using System.Text;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Classic;

namespace NodeRunner.Tests;

// GTA III / Vice City / San Andreas ".img" archives built on disk (no game file).
public class ClassicArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noderunner-classic-" + Guid.NewGuid().ToString("N"));

    public ClassicArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly (string Name, byte[] Data)[] Files =
    [
        ("road.dff", Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray()),
        ("road.txd", Encoding.ASCII.GetBytes("textures")),
    ];

    [Fact]
    public void Version2_ReadsTableAndFiles()
    {
        string game = MakeGame(ClassicGame.SanAndreas, version2: true);
        using var fs = new ClassicFileSystem();
        fs.Open(game, ClassicGame.SanAndreas);

        Assert.Empty(fs.Errors);
        AssertFiles(fs);
    }

    [Fact]
    public void Version1_ReadsTheDirFile()
    {
        string game = MakeGame(ClassicGame.ViceCity, version2: false);
        using var fs = new ClassicFileSystem();
        fs.Open(game, ClassicGame.ViceCity);

        Assert.Empty(fs.Errors);
        AssertFiles(fs);
    }

    [Fact]
    public void ModFolders_AreLeftOut()
    {
        string game = MakeGame(ClassicGame.SanAndreas, version2: true);
        string mod = Path.Combine(game, "modloader", "some mod", "models");
        Directory.CreateDirectory(mod);
        File.WriteAllBytes(Path.Combine(mod, "extra.img"), BuildVersion2());
        using var fs = new ClassicFileSystem();
        fs.Open(game, ClassicGame.SanAndreas);

        Assert.Equal([Path.Combine("models", "gta3.img"), Path.Combine("models", "gta_int.img")], fs.EnumerateArchives());
    }

    [Fact]
    public void Locator_TellsTheGamesApart()
    {
        string trilogy = Path.Combine(_folder, "trilogy");
        string sa = MakeGame(ClassicGame.SanAndreas, version2: true, Path.Combine(trilogy, "GTASA"));
        string vc = MakeGame(ClassicGame.ViceCity, version2: false, Path.Combine(trilogy, "GTAVC"));

        Assert.Equal(sa, ClassicInstallLocator.Resolve(trilogy, ClassicGame.SanAndreas));
        Assert.Equal(vc, ClassicInstallLocator.Resolve(trilogy, ClassicGame.ViceCity));
        Assert.Null(ClassicInstallLocator.Resolve(trilogy, ClassicGame.Gta3));
        Assert.False(ClassicInstallLocator.IsGameFolder(vc, ClassicGame.SanAndreas));
    }

    private static void AssertFiles(ClassicFileSystem fs)
    {
        string archive = Path.Combine("models", "gta3.img");
        foreach (var (name, data) in Files)
        {
            string path = Path.Combine(archive, name);
            var read = fs.ReadFile(path);
            Assert.NotNull(read);
            // Files are padded to whole blocks.
            Assert.Equal(ClassicImgArchive.BlockSize * ((data.Length + ClassicImgArchive.BlockSize - 1) / ClassicImgArchive.BlockSize), read!.Length);
            Assert.Equal(data, read[..data.Length]);
        }
        Assert.True(fs.FileExists(Path.Combine("data", "gta.dat")) || fs.FileExists(Path.Combine("data", "gta_vc.dat")));
    }

    private string MakeGame(ClassicGame game, bool version2, string? folder = null)
    {
        folder ??= Path.Combine(_folder, game.ToString());
        string models = Path.Combine(folder, "models");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, ClassicInstallLocator.MapList(game)), "IDE DATA\\MAPS\\TEST.IDE\n");
        if (version2)
        {
            File.WriteAllBytes(Path.Combine(models, "gta3.img"), BuildVersion2());
        }
        else
        {
            var (img, dir) = BuildVersion1();
            File.WriteAllBytes(Path.Combine(models, "gta3.img"), img);
            File.WriteAllBytes(Path.Combine(models, "gta3.dir"), dir);
        }
        if (game == ClassicGame.SanAndreas)
            File.WriteAllBytes(Path.Combine(models, "gta_int.img"), BuildVersion2());
        return folder;
    }

    private static int Blocks(int size) => (size + ClassicImgArchive.BlockSize - 1) / ClassicImgArchive.BlockSize;

    private static byte[] BuildVersion2()
    {
        int block = 1;
        var image = new byte[ClassicImgArchive.BlockSize * (1 + Files.Sum(f => Blocks(f.Data.Length)))];
        BinaryPrimitives.WriteUInt32LittleEndian(image, ClassicImgArchive.Version2Magic);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4), Files.Length);
        for (int i = 0; i < Files.Length; i++)
        {
            var entry = image.AsSpan(8 + i * 32, 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)block);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)Blocks(Files[i].Data.Length));
            Encoding.ASCII.GetBytes(Files[i].Name).CopyTo(entry[8..]);
            Files[i].Data.CopyTo(image, block * ClassicImgArchive.BlockSize);
            block += Blocks(Files[i].Data.Length);
        }
        return image;
    }

    private static (byte[] Image, byte[] Directory) BuildVersion1()
    {
        int block = 0;
        var image = new byte[ClassicImgArchive.BlockSize * Files.Sum(f => Blocks(f.Data.Length))];
        var directory = new byte[Files.Length * 32];
        for (int i = 0; i < Files.Length; i++)
        {
            var entry = directory.AsSpan(i * 32, 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)block);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)Blocks(Files[i].Data.Length));
            Encoding.ASCII.GetBytes(Files[i].Name).CopyTo(entry[8..]);
            Files[i].Data.CopyTo(image, block * ClassicImgArchive.BlockSize);
            block += Blocks(Files[i].Data.Length);
        }
        return (image, directory);
    }
}
