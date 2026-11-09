using System.IO.Compression;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Dlc;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class PropLibraryTests
{
    private static readonly byte[] Resource = [0x52, 0x53, 0x43, 0x37, .. new byte[12]];

    [Fact]
    public void Library_files_win_over_the_game_and_lose_to_the_project()
    {
        var files = new GameContentTests.FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "x.ymap");
        files.AddArchive(@"x64i.rpf\levels\a\models.rpf", "shop.ydr");
        string root = Directory.CreateTempSubdirectory("nr-library").FullName;
        try
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "projet", "stream")).FullName;
            string library = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
            // A FiveM resource inside a category folder, and a plain folder.
            string resource = Directory.CreateDirectory(Path.Combine(library, "[props]", "logos", "stream")).FullName;
            File.WriteAllText(Path.Combine(library, "[props]", "logos", "fxmanifest.lua"), "");
            File.WriteAllBytes(Path.Combine(resource, "logos.ytyp"), Resource);
            File.WriteAllBytes(Path.Combine(resource, "logo.ydr"), Resource);
            File.WriteAllBytes(Path.Combine(resource, "shop.ydr"), Resource);
            string plain = Directory.CreateDirectory(Path.Combine(library, "bancs", "sous")).FullName;
            File.WriteAllBytes(Path.Combine(plain, "banc.ydr"), Resource);
            File.WriteAllBytes(Path.Combine(plain, "logo.ydr"), Resource);   // also in "logos": the first pack wins
            File.WriteAllBytes(Path.Combine(project, "banc.ydr"), Resource);

            var overlay = (LooseFileOverlay)LooseFileOverlay.Over(files, project, null, library);
            var content = GameContent.Load(overlay, new DlcManifestReader(), new MapSelection(MapVersion.Multiplayer));

            string lib = GameContent.LibraryRoot, own = GameContent.LooseFilesRoot;
            Assert.Equal([$@"{lib}\logo.ydr", $@"{lib}\shop.ydr", $@"{own}\banc.ydr"], content.EnumerateFiles(".ydr").Order());
            Assert.Equal("logos", overlay.LibraryFileOf($@"{lib}\shop.ydr")?.Pack);
            Assert.Equal("bancs", overlay.LibraryFileOf($@"{lib}\logo.ydr")?.Pack);
            Assert.Single(overlay.LibraryDuplicates);
            Assert.Null(overlay.StreamFileOf($@"{lib}\shop.ydr"));
            Assert.Equal(1, overlay.FileCount);
            Assert.True(GameContent.IsLooseFile($@"{lib}\shop.ydr") && GameContent.IsLibraryFile($@"{lib}\shop.ydr"));
            Assert.False(GameContent.IsLibraryFile($@"{own}\banc.ydr"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Library_alone_is_enough_for_an_overlay()
    {
        var files = new GameContentTests.FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "x.ymap");
        string library = Directory.CreateTempSubdirectory("nr-library").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(library, "logo.ytd"), Resource);
            var overlay = Assert.IsType<LooseFileOverlay>(LooseFileOverlay.Over(files, null, null, library));
            Assert.Equal(1, overlay.LibraryFileCount);
            Assert.Equal("", overlay.LibraryFileOf($@"{GameContent.LibraryRoot}\logo.ytd")?.Pack);
            Assert.Same(files, LooseFileOverlay.Over(files, null, null, Path.Combine(library, "missing")));
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void AddPack_copies_a_folder_or_the_folder_inside_a_zip()
    {
        string root = Directory.CreateTempSubdirectory("nr-library").FullName;
        try
        {
            string library = Path.Combine(root, "lib");
            string pack = Directory.CreateDirectory(Path.Combine(root, "inside_props", "stream")).FullName;
            File.WriteAllText(Path.Combine(root, "inside_props", "fxmanifest.lua"), "");
            File.WriteAllBytes(Path.Combine(pack, "logo.ydr"), Resource);

            string added = PropLibrary.AddPack(Path.Combine(root, "inside_props"), library);
            Assert.True(File.Exists(Path.Combine(library, "inside_props", "stream", "logo.ydr")));
            Assert.Equal(Path.Combine(library, "inside_props"), added);
            Assert.True(PropLibrary.HasPack(Path.Combine(root, "inside_props"), library));

            string zip = Path.Combine(root, "logos.zip");
            ZipFile.CreateFromDirectory(Path.Combine(root, "inside_props"), zip, CompressionLevel.Fastest, includeBaseDirectory: true);
            PropLibrary.AddPack(zip, library);
            Assert.True(File.Exists(Path.Combine(library, "logos", "stream", "logo.ydr")));
            Assert.True(File.Exists(Path.Combine(library, "logos", "fxmanifest.lua")));

            string empty = Directory.CreateDirectory(Path.Combine(root, "vide")).FullName;
            File.WriteAllText(Path.Combine(empty, "readme.txt"), "");
            Assert.Throws<IOException>(() => PropLibrary.AddPack(empty, library));
            Assert.False(Directory.Exists(Path.Combine(library, "vide")));
            Assert.Throws<IOException>(() => PropLibrary.AddPack(Path.Combine(library, "logos"), library));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Dependencies_are_the_files_of_the_placed_library_types()
    {
        string lib = GameContent.LibraryRoot;
        string[] libraryFiles =
        [
            $@"{lib}\logos.ytyp", $@"{lib}\logo_1.ydr", $@"{lib}\logo_2.ydr", $@"{lib}\logos.ytd", $@"{lib}\logo_1.ybn",
            $@"{lib}\sign.yft", $@"{lib}\sign_hi.yft", $@"{lib}\shop.ytyp",
        ];
        var logo1 = Type("logo_1", ArchetypeAssetType.Drawable, txd: "logos", physics: "logo_1");
        var logo2 = Type("logo_2", ArchetypeAssetType.Drawable, txd: "logos");
        var sign = Type("sign", ArchetypeAssetType.Fragment, txd: "logos");
        var bench = Type("prop_bench_01a", ArchetypeAssetType.Drawable, txd: "prop_bench");
        var shop = new Archetype
        {
            NameHash = Joaat.Hash("shop_int"), Kind = ArchetypeKind.Interior,
            InteriorEntities = [new MapEntity { ArchetypeHash = sign.NameHash, Position = default }, new MapEntity { ArchetypeHash = bench.NameHash, Position = default }],
        };
        var types = new[] { logo1, logo2, sign, bench, shop }.ToDictionary(a => a.NameHash);
        var files = new Dictionary<uint, string>
        {
            [logo1.NameHash] = $@"{lib}\logos.ytyp", [logo2.NameHash] = $@"{lib}\logos.ytyp", [sign.NameHash] = $@"{lib}\logos.ytyp",
            [shop.NameHash] = $@"{lib}\shop.ytyp", [bench.NameHash] = @"x64i.rpf\props.ytyp",
        };
        string? Model(Archetype a) => a.NameHash == bench.NameHash ? @"x64i.rpf\prop_bench_01a.ydr"
            : libraryFiles.FirstOrDefault(f => Joaat.Hash(Path.GetFileNameWithoutExtension(f)) == a.AssetNameHash && f.EndsWith(a.AssetType == ArchetypeAssetType.Fragment ? ".yft" : ".ydr"));

        var placed = PropLibrary.Dependencies([logo1.NameHash, bench.NameHash], h => types.GetValueOrDefault(h), files.GetValueOrDefault, Model, libraryFiles);
        Assert.Equal([$@"{lib}\logos.ytyp", $@"{lib}\logo_1.ydr", $@"{lib}\logos.ytd", $@"{lib}\logo_1.ybn"], placed);

        var inside = PropLibrary.Dependencies([shop.NameHash], h => types.GetValueOrDefault(h), files.GetValueOrDefault, Model, libraryFiles);
        Assert.Equal([$@"{lib}\shop.ytyp", $@"{lib}\logos.ytyp", $@"{lib}\sign.yft", $@"{lib}\sign_hi.yft", $@"{lib}\logos.ytd"], inside);
    }

    private static Archetype Type(string name, ArchetypeAssetType asset, string txd, string? physics = null) => new()
    {
        NameHash = Joaat.Hash(name),
        AssetType = asset,
        AssetNameHash = Joaat.Hash(name),
        TextureDictionaryHash = Joaat.Hash(txd),
        PhysicsDictionaryHash = physics is null ? 0 : Joaat.Hash(physics),
    };
}
