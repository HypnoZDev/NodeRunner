using System.Numerics;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Dlc;

namespace NodeRunner.Tests;

public class GameContentTests
{
    private const string PackRoot = @"update\x64\dlcpacks\pk\dlc.rpf";

    [Fact]
    public void Reads_dlc_list_setup_and_content()
    {
        var reader = new DlcManifestReader();

        var packs = reader.ReadPackList(Utf8("""
            <SMandatoryPacksData><Paths>
              <Item>platform:/dlcPacks/mpBeach/</Item>
              <item>dlcpacks:/mpheist/</item>
            </Paths></SMandatoryPacksData>
            """));
        var setup = reader.ReadSetup(Utf8(Setup("dlcPk", order: 7)));
        var content = reader.ReadContent(Utf8(Content()));

        Assert.Equal(["mpbeach", "mpheist"], packs);
        Assert.Equal("dlcPk", setup.DeviceName);
        Assert.Equal(7, setup.Order);
        Assert.Equal(["CS_MAP"], setup.ChangeSetGroups["GROUP_MAP"]);
        var file = Assert.Single(content.DataFiles);
        Assert.True(file.Disabled);
        var changeSet = content.ChangeSets["CS_MAP"];
        Assert.Equal(["platform:/levels/a/meta.rpf"], changeSet.FilesToInvalidate);
        Assert.Equal(["dlcPk:/%PLATFORM%/levels/a/meta.rpf"], changeSet.FilesToEnable);
    }

    [Fact]
    public void Update_replaces_base_archive_of_the_same_path()
    {
        var files = BaseGame();

        var content = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));

        Assert.Equal([@"update\update.rpf\x64\levels\a\meta.rpf\x.ymap"], content.EnumerateFiles(".ymap"));
    }

    [Fact]
    public void Project_files_win_over_every_pack_and_keep_maps_out()
    {
        var files = BaseGame();
        AddPack(files);
        files.AddArchive(@"update\update.rpf\x64\levels\a\models.rpf", "shop.ydr");
        files.AddArchive($@"{PackRoot}\x64\levels\a\models.rpf", "shop.ydr");
        string folder = Directory.CreateTempSubdirectory("nr-overlay").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "shop.ydr"), [0x52, 0x53, 0x43, 0x37, .. new byte[12]]);
            File.WriteAllBytes(Path.Combine(folder, "custom.ytyp"), []);
            File.WriteAllBytes(Path.Combine(folder, "custom.ymap"), []);

            var source = NodeRunner.Data.Native.Archives.LooseFileOverlay.Over(files, folder);
            var content = GameContent.Load(source, new DlcManifestReader(), new MapSelection(MapVersion.Multiplayer));

            string root = GameContent.LooseFilesRoot;
            Assert.Equal([$@"{root}\shop.ydr"], content.EnumerateFiles(".ydr"));
            Assert.Equal([$@"{root}\custom.ytyp"], content.EnumerateFiles(".ytyp"));
            Assert.DoesNotContain(content.EnumerateFiles(".ymap"), p => p.StartsWith(root));
            Assert.NotNull(source.GetResourceHeader($@"{root}\shop.ydr"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Map_change_of_a_pack_only_applies_to_the_multiplayer_map()
    {
        var files = BaseGame();
        AddPack(files);

        var story = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));
        var online = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.Multiplayer));

        Assert.Equal([@"update\update.rpf\x64\levels\a\meta.rpf\x.ymap"], story.EnumerateFiles(".ymap"));
        Assert.Equal([$@"{PackRoot}\x64\levels\a\meta.rpf\y.ymap"], online.EnumerateFiles(".ymap"));
    }

    [Fact]
    public void Map_can_be_shown_as_it_was_after_a_given_pack()
    {
        var files = BaseGame();
        AddPack(files);                                    // pk: y.ymap
        AddPack(files, name: "pk2", order: 2, ymap: "z.ymap");

        LoadedMap Load(string? lastPack) => new(GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.Multiplayer, lastPack)));

        Assert.Equal(["z.ymap"], Load(null).Ymaps);        // All packs.
        Assert.Equal(["y.ymap"], Load("pk").Ymaps);        // pk2 left out.
        Assert.Equal(["z.ymap"], Load("unknown").Ymaps);   // Unknown pack: all packs.
        Assert.Equal(["pk"], Load("pk").Value.Packs);
        Assert.Equal(["pk", "pk2"], Load("pk").Value.AllPacks.Select(p => p.Name));
    }

    private sealed record LoadedMap(GameContent Value)
    {
        public IEnumerable<string?> Ymaps => Value.EnumerateFiles(".ymap").Select(Path.GetFileName);
    }

    [Fact]
    public void Fixed_archive_in_dlc_patch_replaces_the_pack_one()
    {
        var files = BaseGame();
        AddPack(files);
        files.AddArchive(@"update\update.rpf\dlc_patch\pk\x64\levels\a\meta.rpf", "y.ymap");

        var online = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.Multiplayer));

        Assert.Equal([@"update\update.rpf\dlc_patch\pk\x64\levels\a\meta.rpf\y.ymap"], online.EnumerateFiles(".ymap"));
    }

    [Fact]
    public void Archive_enabled_without_invalidation_is_added_over_the_base_one()
    {
        var files = BaseGame();
        AddPack(files, group: "GROUP_UPDATE_STREAMING", invalidate: false);
        files.AddFile($@"{PackRoot}\x64\levels\a\meta.rpf\x.ymap");

        var story = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));

        // The pack's x.ymap wins over the base one; its y.ymap is added.
        Assert.Equal(
            [$@"{PackRoot}\x64\levels\a\meta.rpf\x.ymap", $@"{PackRoot}\x64\levels\a\meta.rpf\y.ymap"],
            story.EnumerateFiles(".ymap").Order());
    }

    [Fact]
    public void Definitions_fall_back_on_inactive_archives()
    {
        var files = BaseGame();
        files.AddArchive(@"x64i.rpf\levels\a\types.rpf", "base.ytyp");
        AddPack(files);
        files.AddFile($@"{PackRoot}\x64\levels\a\meta.rpf\pack.ytyp");

        var story = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));

        Assert.Equal(["base.ytyp"], story.EnumerateFiles(".ytyp").Select(Path.GetFileName));
        Assert.Equal(["base.ytyp", "pack.ytyp"], story.EnumerateFiles(".ytyp", withInactiveFallback: true).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Links_lod_parents_in_the_same_or_the_parent_ymap()
    {
        var files = new FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "lod.ymap", "hd.ymap");
        var lod = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero, LodLevel = LodLevel.Slod1, ChildCount = 1 };
        var district = new MapEntity { ArchetypeHash = 2, Position = Vector3.Zero, LodLevel = LodLevel.Lod, ParentIndex = 0, ChildCount = 1 };
        var building = new MapEntity { ArchetypeHash = 3, Position = Vector3.Zero, LodLevel = LodLevel.Hd, ParentIndex = 0, Flags = 8 };
        var maps = new Dictionary<string, MapData>
        {
            ["lod"] = new() { NameHash = 10, Entities = [lod, district] },              // district -> lod, same file
            ["hd"] = new() { NameHash = 11, ParentHash = 10, Entities = [building] },    // building -> entity 0 of lod.ymap
        };
        files.SetContent(@"x64i.rpf\levels\a\meta.rpf\lod.ymap", "lod");
        files.SetContent(@"x64i.rpf\levels\a\meta.rpf\hd.ymap", "hd");

        var content = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));
        MapLibrary.Load(content, new NoTypes(), new NamedMaps(maps));

        Assert.Same(lod, district.Parent);
        Assert.Same(lod, building.Parent);
    }

    [Fact]
    public void Interior_objects_are_placed_in_the_world_by_the_instance()
    {
        var (library, _, instance) = LoadTunnel();

        var chair = Assert.Single(instance.InteriorEntities);
        Assert.Same(instance, chair.Interior);
        // (2, 0, 0) in the interior, turned a quarter around Z, from the instance at (100, 0, 0).
        Assert.Equal(100f, chair.Position.X, 3);
        Assert.Equal(2f, chair.Position.Y, 3);
        var facing = Vector3.Transform(Vector3.UnitX, chair.Orientation);
        Assert.Equal(1f, facing.Y, 3);
        Assert.Equal(60u, chair.ArchetypeHash);
        Assert.NotNull(library.GetArchetype(instance.ArchetypeHash));
    }

    [Fact]
    public void Shell_without_switch_distance_hands_over_to_its_interior()
    {
        var (library, shell, instance) = LoadTunnel();

        // Within the draw distance of an object inside (100 m): the interior replaces the simplified shell.
        Assert.Equal(instance.InteriorEntities, LodSelector.Select(library, new Vector3(100f, 0f, 0f), 1f));
        // Further away, but within the shell's own draw distance (500 m): the shell.
        Assert.Equal([shell], LodSelector.Select(library, new Vector3(100f, 400f, 0f), 1f));
    }

    [Fact]
    public void Scripted_maps_are_loaded_with_their_parent_unless_hidden()
    {
        var files = new FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "city.ymap", "hole.ymap", "hole_lod.ymap", "office.ymap");
        foreach (var name in new[] { "city", "hole", "hole_lod", "office" })
            files.SetContent($@"x64i.rpf\levels\a\meta.rpf\{name}.ymap", name);
        const uint scripted = 1;
        var maps = new Dictionary<string, MapData>
        {
            ["city"] = new() { NameHash = Joaat.Hash("city"), Entities = [] },
            ["hole"] = new() { NameHash = Joaat.Hash("hole"), ParentHash = Joaat.Hash("hole_lod"), Flags = scripted, Entities = [] },
            ["hole_lod"] = new() { NameHash = Joaat.Hash("hole_lod"), Flags = scripted, Entities = [] },
            ["office"] = new() { NameHash = Joaat.Hash("office"), Flags = scripted, Entities = [] },
        };
        var content = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));

        var all = MapLibrary.Load(content, new NoTypes(), new NamedMaps(maps));
        var library = MapLibrary.Load(content, new NoTypes(), new NamedMaps(maps), hiddenScriptedMaps: new HashSet<string> { "office" });

        Assert.Equal(["city", "hole", "hole_lod", "office"], all.Maps.Select(m => Path.GetFileNameWithoutExtension(m.FilePath)).Order());
        Assert.Equal(["city", "hole", "hole_lod"], library.Maps.Select(m => Path.GetFileNameWithoutExtension(m.FilePath)).Order());
        // The simplified version comes with its map: not offered on its own.
        Assert.Equal(["hole", "office"], library.ScriptedMapNames);
    }

    [Fact]
    public void Names_come_from_file_names_and_simplified_model_patterns()
    {
        var files = new FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "id2_build14.ydr", "cs6_props_combo06_slod_children.ydd");
        var content = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));
        var wanted = new HashSet<uint> { Joaat.Hash("id2_build14_lod"), Joaat.Hash("cs6_props_combo06_09_lod") };

        var names = HashNames.FromFileNames(content, wanted);

        Assert.Equal("id2_build14", names.TryGet(Joaat.Hash("id2_build14")));
        Assert.Equal("id2_build14_lod", names.TryGet(Joaat.Hash("id2_build14_lod")));
        Assert.Equal("cs6_props_combo06_09_lod", names.TryGet(Joaat.Hash("cs6_props_combo06_09_lod")));
        Assert.Null(names.TryGet(Joaat.Hash("cs6_props_combo06_08_lod"))); // Not an object type: not kept.
        Assert.Equal("0x0000002A", names.Describe(42));
    }

    [Fact]
    public void Objects_inside_an_interior_follow_it_when_it_is_moved()
    {
        var (_, _, instance) = LoadTunnel();
        var chair = Assert.Single(instance.InteriorEntities);
        int revision = chair.Revision;

        instance.SetPlacement(new Vector3(200f, 0f, 0f), Quaternion.Identity, Vector3.One);

        // Unturned interior: the chair is 2 m along X from the interior's origin.
        Assert.Equal(202f, chair.Position.X, 3);
        Assert.Equal(0f, chair.Position.Y, 3);
        Assert.NotEqual(revision, chair.Revision);
        Assert.False(chair.CanEdit);
        Assert.Throws<InvalidOperationException>(() => chair.SetPlacement(Vector3.Zero, Quaternion.Identity, Vector3.One));
    }

    private static (MapLibrary Library, MapEntity Shell, MapEntity Instance) LoadTunnel()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var chair = new MapEntity { ArchetypeHash = 60, Position = new Vector3(2f, 0f, 0f), LodDistance = 100f };
        var interior = new Archetype { NameHash = 50, Kind = ArchetypeKind.Interior, InteriorEntities = [chair] };
        var shell = new MapEntity
        {
            ArchetypeHash = 40, Position = new Vector3(100f, 0f, 0f), LodLevel = LodLevel.Lod, LodDistance = 500f, ChildCount = 1,
        };
        var instance = new MapEntity
        {
            ArchetypeHash = 50, Position = new Vector3(100f, 0f, 0f), StoredRotation = quarter, IsInterior = true,
            LodDistance = 200f, ParentIndex = 0,
        };

        var files = new FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "tunnel.ymap", "tunnel.ytyp");
        files.SetContent(@"x64i.rpf\levels\a\meta.rpf\tunnel.ymap", "tunnel");
        files.SetContent(@"x64i.rpf\levels\a\meta.rpf\tunnel.ytyp", "tunnel");
        var maps = new Dictionary<string, MapData> { ["tunnel"] = new() { NameHash = 10, Entities = [shell, instance] } };

        var content = GameContent.Load(files, new DlcManifestReader(), new MapSelection(MapVersion.SinglePlayer));
        var library = MapLibrary.Load(content, new FixedTypes(interior), new NamedMaps(maps));
        return (library, shell, instance);
    }

    private static FakeGameFiles BaseGame()
    {
        var files = new FakeGameFiles();
        files.AddArchive(@"x64i.rpf\levels\a\meta.rpf", "x.ymap");
        files.AddArchive(@"update\update.rpf\x64\levels\a\meta.rpf", "x.ymap");
        return files;
    }

    private static void AddPack(
        FakeGameFiles files, string name = "pk", int order = 1, string ymap = "y.ymap", string group = "GROUP_MAP", bool invalidate = true)
    {
        files.Packs.Add(name);
        files.SetContent(@"update\update.rpf\common\data\dlclist.xml",
            $"<SMandatoryPacksData><Paths>{string.Concat(files.Packs.Select(p => $"<Item>dlcpacks:/{p}/</Item>"))}</Paths></SMandatoryPacksData>");
        string root = $@"update\x64\dlcpacks\{name}\dlc.rpf";
        string device = "dlc" + name;
        files.AddArchive(root, "setup2.xml", "content.xml");
        files.SetContent($@"{root}\setup2.xml", Setup(device, order, group));
        files.SetContent($@"{root}\content.xml", Content(device, invalidate));
        files.AddArchive($@"{root}\x64\levels\a\meta.rpf", ymap);
    }

    private static string Setup(string device, int order, string group = "GROUP_MAP") => $"""
        <SSetupData>
          <deviceName>{device}</deviceName>
          <datFile>content.xml</datFile>
          <contentChangeSetGroups>
            <Item><NameHash>{group}</NameHash><ContentChangeSets><Item>CS_MAP</Item></ContentChangeSets></Item>
            <Item><NameHash>GROUP_ON_DEMAND</NameHash><ContentChangeSets><Item>CS_NEVER</Item></ContentChangeSets></Item>
          </contentChangeSetGroups>
          <order value="{order}" />
        </SSetupData>
        """;

    private static string Content(string device = "dlcPk", bool invalidate = true) => $"""
        <CDataFileMgr__ContentsOfDataFileXml>
          <dataFiles>
            <Item>
              <filename>{device}:/%PLATFORM%/levels/a/meta.rpf</filename>
              <fileType>RPF_FILE</fileType>
              <disabled value="true" />
            </Item>
          </dataFiles>
          <contentChangeSets>
            <Item>
              <changeSetName>CS_MAP</changeSetName>
              <mapChangeSetData>
                <Item>
                  <filesToInvalidate>{(invalidate ? "<Item>platform:/levels/a/meta.rpf</Item>" : "")}</filesToInvalidate>
                  <filesToEnable><Item>{device}:/%PLATFORM%/levels/a/meta.rpf</Item></filesToEnable>
                </Item>
              </mapChangeSetData>
            </Item>
          </contentChangeSets>
        </CDataFileMgr__ContentsOfDataFileXml>
        """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // In-memory game files: archive paths and file contents.
    private sealed class FakeGameFiles : IGameFileSystem
    {
        private readonly HashSet<string> _archives = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Packs { get; } = new();

        public GameInstallation? Installation => null;

        public void AddArchive(string archive, params string[] fileNames)
        {
            _archives.Add(archive);
            foreach (var name in fileNames)
                AddFile($@"{archive}\{name}");
        }

        public void AddFile(string path) => _files.TryAdd(path, []);
        public void SetContent(string path, string text) => _files[path] = Utf8(text);

        public void Open(GameInstallation installation, IProgress<string>? progress = null) { }
        public bool FileExists(string path) => _files.ContainsKey(path);
        public byte[]? ReadFile(string path) => _files.GetValueOrDefault(path);
        public ResourceHeader? GetResourceHeader(string path) => null;
        public IEnumerable<string> EnumerateFiles(string extension) =>
            _files.Keys.Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
        public IEnumerable<string> EnumerateArchives() => _archives;
        public void Dispose() { }
    }

    private sealed class NoTypes : IMapTypesReader
    {
        public MapTypes Read(ReadOnlySpan<byte> data) => new() { NameHash = 0, Archetypes = [] };
    }

    private sealed class FixedTypes(params Archetype[] archetypes) : IMapTypesReader
    {
        public MapTypes Read(ReadOnlySpan<byte> data) => new() { NameHash = 0, Archetypes = archetypes };
    }

    // Returns the map whose key is the file content.
    private sealed class NamedMaps(Dictionary<string, MapData> maps) : IMapDataReader
    {
        public MapData Read(ReadOnlySpan<byte> data) => maps[Encoding.UTF8.GetString(data)];
    }
}
