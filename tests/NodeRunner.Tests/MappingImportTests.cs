using NodeRunner.Data.FileSystem;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class MappingImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr_import_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
    }

    [Fact]
    public void ChangesInAWatchedFolderGiveTheFilesToAddAgain()
    {
        Touch(@"export\a.ydr.xml");
        Touch(@"export\a\a.dds");
        Touch(@"export\b.ytd.xml");
        Touch(@"export\b.ytd\b.dds");
        Touch(@"export\c.ymap");
        Touch(@"export\notes.txt");
        Touch(@"outside.ymap");
        string folder = Path.Combine(_root, "export");
        string P(string relative) => Path.Combine(_root, relative);

        Assert.Equal([P(@"export\c.ymap")], MappingImport.WatchedSources([P(@"export\c.ymap"), P(@"export\c.ymap"), P("outside.ymap"), P(@"export\gone.ydr")], folder));
        // A texture changed: the XML exports next to it and in the folder above are converted again.
        Assert.Equal([P(@"export\a.ydr.xml"), P(@"export\b.ytd.xml")], MappingImport.WatchedSources([P(@"export\b.ytd\b.dds")], folder));
        Assert.Equal([folder], MappingImport.WatchedSources([folder + @"\"], folder));
    }

    [Fact]
    public void CopiesStreamedFilesIntoANewProjectWithoutTouchingTheSource()
    {
        Touch(@"cayo\fxmanifest.lua");
        Touch(@"cayo\stream\a.ymap");
        Touch(@"cayo\stream\col\b.ybn");
        Touch(@"cayo\stream\other\a.ymap");
        Touch(@"cayo\readme.txt");

        var result = MappingImport.Import(Path.Combine(_root, "cayo"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        Assert.Equal(2, result.Copied);
        Assert.Single(result.Skipped);
        Assert.Equal("cayo", result.Project.Name);
        Assert.True(File.Exists(Path.Combine(result.Project.StreamFolder, "a.ymap")));
        Assert.True(File.Exists(Path.Combine(result.Project.StreamFolder, "b.ybn")));
        Assert.True(File.Exists(Path.Combine(_root, @"cayo\stream\a.ymap")));
        Assert.False(File.Exists(Path.Combine(_root, @"cayo\projet.json")));
    }

    [Fact]
    public void FilesAddedToAProjectReplaceThoseOfTheSameName()
    {
        Touch(@"first\stream\a.ymap");
        var project = MappingImport.Import(Path.Combine(_root, "first"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null).Project;
        Touch(@"blender\a.ymap");
        Touch(@"blender\sub\b.ybn");
        Touch(@"blender\c.ytyp.xml");
        Touch(@"single\d.ydr");

        var result = MappingImport.AddToProject([Path.Combine(_root, "blender"), Path.Combine(_root, @"single\d.ydr")], project,
            new Dictionary<string, Func<XmlExport, byte[]>> { [".ytyp"] = export => [.. "ytyp:"u8, .. System.Text.Encoding.ASCII.GetBytes(export.FileName)] });

        Assert.Equal(4, result.Copied);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(1, result.Converted);
        Assert.Equal(@"blender\a.ymap", File.ReadAllText(Path.Combine(project.StreamFolder, "a.ymap")));
        Assert.Equal("ytyp:c.ytyp.xml", File.ReadAllText(Path.Combine(project.StreamFolder, "c.ytyp")));
        Assert.True(File.Exists(Path.Combine(project.StreamFolder, "b.ybn")));
        Assert.True(File.Exists(Path.Combine(project.StreamFolder, "d.ydr")));
        Assert.Empty(Directory.EnumerateFiles(project.StreamFolder, "*.tmp"));

        Assert.True(MappingImport.CanImport(Path.Combine(_root, @"single\d.ydr")));
        Touch(@"single\notes.txt");
        Assert.False(MappingImport.CanImport(Path.Combine(_root, @"single\notes.txt")));
        Assert.Throws<InvalidDataException>(() => MappingImport.AddToProject([Path.Combine(_root, @"single\notes.txt")], project));
    }

    [Fact]
    public void FilesLockedByAssetEscrowAreCopiedAndListed()
    {
        Touch(@"shop\stream\unlocked\shop.ymap");
        string locked = Path.Combine(_root, @"shop\stream\locked\ydr\shop_bar.ydr");
        Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
        File.WriteAllBytes(locked, [.. "FXAP"u8, 1, 0, 0, 0, 0x52, 0xA3]);

        var result = MappingImport.Import(Path.Combine(_root, "shop"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        Assert.Equal(2, result.Copied);
        Assert.Equal(["shop_bar.ydr"], result.Protected);
        Assert.True(ProtectedFile.IsProtected(Path.Combine(result.Project.StreamFolder, "shop_bar.ydr")));
        Assert.False(ProtectedFile.IsProtected(Path.Combine(result.Project.StreamFolder, "shop.ymap")));
        Assert.True(ProtectedFile.IsError(ProtectedFile.Error("shop_bar.ydr")));
        Assert.False(ProtectedFile.IsError(new InvalidDataException()));
    }

    [Fact]
    public void AStreamFolderTakesItsParentsNameAndNeverOverwritesAProject()
    {
        Touch(@"pack\stream\a.ymap");
        Directory.CreateDirectory(Path.Combine(_root, "projects", "pack"));

        var result = MappingImport.Import(Path.Combine(_root, @"pack\stream"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        // Named after its folder, so that both projects export to different FiveM resources.
        Assert.Equal("pack 2", result.Project.Name);
        Assert.Equal("pack_2", result.Project.ResourceName);
        Assert.Equal("pack 2", Path.GetFileName(result.Project.Folder));
    }

    [Fact]
    public void ABrokenXmlExportIsLeftOutWithoutStoppingTheImport()
    {
        Touch(@"pack\a.ymap");
        Touch(@"pack\b.ybn.xml");
        var converters = new Dictionary<string, Func<XmlExport, byte[]>> { [".ybn"] = _ => throw new IndexOutOfRangeException("bad") };

        var result = MappingImport.Import(Path.Combine(_root, "pack"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null, converters);

        Assert.Equal(1, result.Copied);
        Assert.Contains(result.Ignored, i => i.StartsWith("b.ybn.xml"));
        Assert.False(File.Exists(Path.Combine(result.Project.StreamFolder, "b.ybn.tmp")));
    }

    [Fact]
    public void AResourcesDataFilesAreKeptWithTheirManifestLines()
    {
        Touch(@"mlo\stream\a.ymap");
        Touch(@"mlo\audio\mlo_game.dat151.rel");
        Touch(@"mlo\audio\sfx\bank\a.awc");
        Touch(@"mlo\client.lua");
        Touch(@"mlo\notes.txt");
        File.WriteAllText(Path.Combine(_root, @"mlo\fxmanifest.lua"), """
            fx_version 'cerulean'
            game 'gta5'
            this_is_a_map 'yes'
            -- data_file 'TIMECYCLEMOD_FILE' 'commented.xml'
            files {
                'audio/*.dat151.rel',
                "audio/sfx/**/*.awc",
            }
            client_script 'client.lua'
            data_file 'DLC_ITYP_REQUEST' 'stream/mlo.ytyp'
            data_file 'AUDIO_GAMEDATA' 'audio/mlo_game.dat'
            data_file 'TIMECYCLEMOD_FILE' 'missing.xml'
            """);

        var result = MappingImport.Import(Path.Combine(_root, @"mlo\stream"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        string folder = result.Project.Folder;
        Assert.Equal(1, result.Copied);
        Assert.Equal(2, result.ResourceFiles);
        Assert.Equal(["audio/mlo_game.dat151.rel", "audio/sfx/bank/a.awc"], ResourceExtras.ReadFiles(folder));
        var lines = ResourceExtras.ReadLines(folder);
        Assert.Contains("data_file 'AUDIO_GAMEDATA' 'audio/mlo_game.dat'", lines);
        Assert.DoesNotContain(lines, l => l.Contains("DLC_ITYP_REQUEST") || l.Contains("missing.xml") || l.Contains("commented"));
        Assert.Contains(result.Ignored, i => i.StartsWith("client.lua"));
        Assert.Contains(result.Ignored, i => i.StartsWith("missing.xml"));

        string manifest = FiveMResource.BuildManifest("me", "1.0", ["a.ymap"], extraLines: lines);
        Assert.Contains("    'audio/sfx/bank/a.awc',", manifest);
        Assert.True(ResourceExtras.Matches("audio/**/*.awc", "audio/a.awc"));
        Assert.False(ResourceExtras.Matches("audio/*.awc", "audio/sfx/a.awc"));
    }

    private const string Assembly = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package version="2.0" target="Five">
        <metadata><name>Shop Mod</name><version><major>3</major><minor>1</minor></version><author><displayName>Someone</displayName></author></metadata>
        <content>
        <archive path="x64l.rpf" createIfNotExist="True" type="RPF7">
        <archive path="\levels\gta5\_cityw\koreatown_01\kt1_12.rpf" createIfNotExist="True" type="RPF7">
        <add source="aaa\kt1_12_shop.ydr">\kt1_12_shop.ydr</add>
        <add source="bbb/kt1_12_shop+hi.ytd">\kt1_12_shop+hi.ytd</add>
        <add source="ccc\missing.ybn">\missing.ybn</add>
        </archive>
        </archive>
        <add source="ddd\dlc.rpf">update\x64\dlcpacks\shop\dlc.rpf</add>
        <xml path="update\update.rpf\common\data\dlclist.xml"><add xpath="/SMandatoryPacksData/Paths">x</add></xml>
        </content>
        </package>
        """;

    private string MakeZip(string relative, params (string Path, string Text)[] entries)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    [Fact]
    public void AnOpenIvPackageGivesItsFilesTheirGameNames()
    {
        string oiv = MakeZip(@"dl\mod.oiv", ("assembly.xml", Assembly), ("content/aaa/kt1_12_shop.ydr", "model"),
            ("content/bbb/kt1_12_shop+hi.ytd", "textures"), ("content/ddd/dlc.rpf", "rpf"));

        var result = MappingImport.Import(oiv, Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        Assert.Equal("Shop Mod", result.Project.Name);
        Assert.Equal("Someone", result.Author);
        Assert.Equal(2, result.Copied);
        Assert.Equal("model", File.ReadAllText(Path.Combine(result.Project.StreamFolder, "kt1_12_shop.ydr")));
        Assert.True(File.Exists(Path.Combine(result.Project.StreamFolder, "kt1_12_shop+hi.ytd")));
        Assert.Equal(3, result.Ignored.Count); // missing.ybn, dlc.rpf, the xml edit
    }

    [Fact]
    public void AnUnzippedPackageAndAFolderHoldingAPackageAreImportedToo()
    {
        Touch(@"unzipped\assembly.xml");
        File.WriteAllText(Path.Combine(_root, @"unzipped\assembly.xml"), Assembly);
        Touch(@"unzipped\content\aaa\kt1_12_shop.ydr");
        Touch(@"unzipped\content\PLACEHOLDER");
        var unzipped = MappingImport.Import(Path.Combine(_root, "unzipped"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);
        Assert.Equal("Shop Mod", unzipped.Project.Name);
        Assert.Equal(1, unzipped.Copied);

        Touch(@"download\readme.txt");
        MakeZip(@"download\sub\arena.oiv", ("assembly.xml", Assembly), ("content/aaa/kt1_12_shop.ydr", "model"));
        var download = MappingImport.Import(Path.Combine(_root, "download"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);
        Assert.Equal("Shop Mod 2", Path.GetFileName(download.Project.Folder));
        Assert.Equal(1, download.Copied);
    }

    [Fact]
    public void AZippedResourceIsImportedLikeItsFolder()
    {
        string zip = MakeZip(@"dl\my_map.zip", ("my_map/fxmanifest.lua", ""), ("my_map/stream/a.ymap", "map"), ("my_map/stream/b.ytyp", "types"));

        var result = MappingImport.Import(zip, Path.Combine(_root, "projects"), MapVersion.Multiplayer, null);

        Assert.Equal("my_map", result.Project.Name);
        Assert.Equal(2, result.Copied);
        Assert.Null(result.Author);
    }
}
