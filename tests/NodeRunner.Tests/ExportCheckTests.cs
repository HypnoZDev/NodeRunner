using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class ExportCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noderunner_conflicts_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relativePath, string content = "")
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Scan_root_is_the_resources_folder_holding_the_export_folder()
    {
        string maps = Path.Combine(_root, "server", "resources", "[maps]");
        Directory.CreateDirectory(maps);

        Assert.Equal(Path.Combine(_root, "server", "resources"), ConflictScanner.ScanRoot(maps));
        Assert.Equal(Path.GetFullPath(_root), ConflictScanner.ScanRoot(_root));
    }

    [Fact]
    public void Finds_the_same_map_file_in_other_resources_but_not_in_our_own()
    {
        Write(@"[maps]\garage\fxmanifest.lua");
        string garage = Write(@"[maps]\garage\stream\sub\HW1_26_strm_0.ymap");
        Write(@"[maps]\garage\stream\other.ymap");
        Write(@"[maps]\hopital\fxmanifest.lua");
        Write(@"[maps]\hopital\stream\hw1_26_strm_0.ymap");
        string loose = Write(@"[old]\stuff\hw1_26_strm_0.ymap");

        var conflicts = ConflictScanner.ScanResources(_root, [Path.Combine(_root, "[maps]", "hopital")], ["hw1_26_strm_0.ymap"]);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.Owner == "garage" && c.Path == garage && !c.IsProject);
        Assert.Contains(conflicts, c => c.Owner == "[old]" && c.Path == loose); // No manifest: the folder under the root.
    }

    [Fact]
    public void Finds_earlier_exports_of_the_same_resource_elsewhere()
    {
        Write(@"geneva\fxmanifest.lua", FiveMResource.GeneratedMarker + " 0.0.1\n");
        Write(@"[other]\geneva\fxmanifest.lua", "fx_version 'cerulean'\n"); // Same name, somebody else's.
        Write(@"[maps]\geneva\fxmanifest.lua", FiveMResource.GeneratedMarker + " 0.0.1\n"); // The one being exported.

        var copy = Assert.Single(ConflictScanner.FindOtherCopies(_root, Path.Combine(_root, "[maps]", "geneva"), "geneva"));

        Assert.Equal(Path.Combine(_root, "geneva"), copy);
    }

    [Fact]
    public void Folders_without_brackets_under_resources_are_not_categories()
    {
        Assert.Empty(ConflictScanner.NonCategoryFolders(_root, _root));
        Assert.Empty(ConflictScanner.NonCategoryFolders(_root, Path.Combine(_root, "[maps]", "[city]")));
        Assert.Equal(["mapping"], ConflictScanner.NonCategoryFolders(_root, Path.Combine(_root, "mapping")));
        Assert.Equal(["mapping"], ConflictScanner.NonCategoryFolders(_root, Path.Combine(_root, "[maps]", "mapping")));
    }

    [Fact]
    public void Finds_the_same_map_file_in_other_projects()
    {
        string stream = Path.Combine(_root, "Commissariat", "stream");
        Write(@"Commissariat\stream\hw1_26_strm_0.ymap");

        var conflict = Assert.Single(ConflictScanner.ScanProjects([("Commissariat", stream), ("Absent", Path.Combine(_root, "x"))], ["hw1_26_strm_0.ymap"]));

        Assert.True(conflict.IsProject);
        Assert.Equal("Commissariat", conflict.Owner);
    }

    private static MapEntity Entity(uint type, LodLevel level = LodLevel.Hd, int parent = -1) =>
        new() { ArchetypeHash = type, Position = Vector3.Zero, LodLevel = level, ParentIndex = parent };

    private static MapEntity At(uint type, float x, int source = -1, bool deleted = false)
    {
        var entity = new MapEntity { ArchetypeHash = type, Position = new Vector3(x, 0, 0), SourceIndex = source };
        if (deleted)
            entity.IsDeleted = true;
        return entity;
    }

    private static MapEntity[] Game() => [At(2, 0, 0), At(2, 10, 1), At(2, 20, 2)];

    [Fact]
    public void The_deleted_copy_is_found_among_identical_buildings()
    {
        // Loaded from the game's file: the index tells which one is gone, even after the others moved.
        var removed = RemovedEntities.Compare(Game(), [At(2, 0, 0), At(2, 25, 2)], fromGameFile: true);
        Assert.Equal(10f, Assert.Single(removed.Removed).Position.X);

        // From a saved version: the unchanged ones pair by place, the middle one is gone.
        removed = RemovedEntities.Compare(Game(), [At(2, 0, 5), At(2, 20, 6)]);
        Assert.Equal(10f, Assert.Single(removed.Removed).Position.X);
        Assert.Empty(removed.Added);

        // A new copy of the same model elsewhere is an addition, not the deleted one moved.
        removed = RemovedEntities.Compare(Game(), [At(2, 0, 5), At(2, 20, 6), At(2, 40)]);
        Assert.Equal(10f, Assert.Single(removed.Removed).Position.X);
        Assert.Equal(40f, Assert.Single(removed.Added).Position.X);

        // Kept but flagged deleted.
        removed = RemovedEntities.Compare(Game(), [At(2, 0, 5), At(2, 10, 6, deleted: true), At(2, 20, 7)]);
        Assert.Equal(10f, Assert.Single(removed.Removed).Position.X);
    }

    [Fact]
    public void Copies_that_cannot_be_told_apart_are_never_treated_as_deleted()
    {
        // Two of three moved to about the same distance from both old places: which one is which is unknown.
        var result = RemovedEntities.Compare(Game(), [At(2, 0, 5), At(2, 15, 6)]);
        Assert.Empty(result.Removed);
        Assert.Equal(2, result.Uncertain.Count);
    }

    [Fact]
    public void Scenery_is_a_detailed_building_of_the_lod_tree()
    {
        var building = new Archetype { NameHash = 1 };
        Assert.True(RemovedEntities.IsScenery(Entity(1, LodLevel.Hd, parent: 4), building));
        Assert.True(RemovedEntities.IsScenery(Entity(1, LodLevel.Hd, parent: 4), null));
        Assert.False(RemovedEntities.IsScenery(Entity(1, LodLevel.OrphanHd), building));
        Assert.False(RemovedEntities.IsScenery(Entity(1, LodLevel.Lod, parent: 2), building));
    }

    [Fact]
    public void Props_carry_their_own_collision()
    {
        var hd = Entity(1, LodLevel.Hd, parent: 4);
        Assert.False(RemovedEntities.IsScenery(hd, new Archetype { NameHash = 1, Flags = 0x20000000 }));
        Assert.False(RemovedEntities.IsScenery(hd, new Archetype { NameHash = 1, AssetType = ArchetypeAssetType.Fragment }));
        Assert.False(RemovedEntities.IsScenery(hd, new Archetype { NameHash = 1, PhysicsDictionaryHash = 7 }));
    }
}
