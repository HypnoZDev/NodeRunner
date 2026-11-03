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

    [Fact]
    public void Removed_entities_are_counted_type_by_type()
    {
        var original = new[] { Entity(1), Entity(2), Entity(2), Entity(3) };
        var current = new[] { Entity(2), Entity(3), Entity(4) }; // One "2" and the "1" are gone, a "4" was added.

        var removed = RemovedEntities.Find(original, current);

        Assert.Equal([original[0], original[2]], removed);
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
