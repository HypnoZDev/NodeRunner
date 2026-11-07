using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A map file of the export also shipped by another FiveM resource of the server, or modified by another project.
public sealed record MapConflict(string FileName, string Owner, bool IsProject, string Path);

// Finds the map and collision files (.ymap, .ybn) that other resources or projects also ship.
public static class ConflictScanner
{
    private static readonly string[] ManifestNames = [FiveMResource.ManifestFileName, "__resource.lua"];

    // Folder to scan for other resources: the server's "resources" folder holding exportFolder (resources are often
    // sorted in sub-folders such as [maps]), otherwise the export folder itself.
    public static string ScanRoot(string exportFolder)
    {
        for (var dir = new DirectoryInfo(exportFolder); dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.Name, "resources", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
        }
        return Path.GetFullPath(exportFolder);
    }

    // The folders between root and exportFolder that FiveM does not look into: under "resources", only folders named
    // in brackets ([maps]) are categories; any other folder is taken as a resource itself, so a resource exported
    // inside it is never started.
    public static List<string> NonCategoryFolders(string root, string exportFolder)
    {
        var folders = new List<string>();
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        for (var dir = new DirectoryInfo(Path.GetFullPath(exportFolder)); dir is not null; dir = dir.Parent)
        {
            if (string.Equals(dir.FullName.TrimEnd('\\', '/'), fullRoot, StringComparison.OrdinalIgnoreCase))
                return folders;
            if (!dir.Name.StartsWith('['))
                folders.Insert(0, dir.Name);
        }
        return []; // Not under the root: nothing known about the server's layout.
    }

    // Other resources named resourceName made by NodeRunner under root: earlier exports of the same project to
    // another folder.
    public static List<string> FindOtherCopies(string root, string ownResourceFolder, string resourceName)
    {
        var copies = new List<string>();
        if (!Directory.Exists(root))
            return copies;
        string own = Path.GetFullPath(ownResourceFolder).TrimEnd('\\', '/');
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (string dir in Directory.EnumerateDirectories(root, resourceName, options))
        {
            string manifest = Path.Combine(dir, FiveMResource.ManifestFileName);
            if (string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), own, StringComparison.OrdinalIgnoreCase) || !File.Exists(manifest))
                continue;
            try
            {
                if (FiveMResource.IsGeneratedManifest(File.ReadAllText(manifest)))
                    copies.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable manifest: treated as somebody else's resource.
            }
        }
        return copies;
    }

    // Every file under root named like one of fileNames, except those of ownResourceFolders (the previous exports of
    // this project).
    public static List<MapConflict> ScanResources(string root, IReadOnlyCollection<string> ownResourceFolders, IReadOnlyCollection<string> fileNames)
    {
        var wanted = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<MapConflict>();
        if (wanted.Count == 0 || !Directory.Exists(root))
            return conflicts;
        var own = ownResourceFolders.Select(f => Path.GetFullPath(f).TrimEnd('\\', '/') + Path.DirectorySeparatorChar).ToList();

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (string file in Directory.EnumerateFiles(root, "*.y*", options))
        {
            string name = Path.GetFileName(file);
            if (!wanted.Contains(name) || own.Any(o => file.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
                continue;
            conflicts.Add(new MapConflict(name, ResourceName(root, file), IsProject: false, file));
        }
        return conflicts;
    }

    public static List<MapConflict> ScanProjects(IEnumerable<(string Name, string StreamFolder)> projects, IReadOnlyCollection<string> fileNames)
    {
        var wanted = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<MapConflict>();
        foreach (var (projectName, stream) in projects)
        {
            if (!Directory.Exists(stream))
                continue;
            foreach (string file in Directory.EnumerateFiles(stream))
            {
                if (wanted.Contains(Path.GetFileName(file)))
                    conflicts.Add(new MapConflict(Path.GetFileName(file), projectName, IsProject: true, file));
            }
        }
        return conflicts;
    }

    private static string ResourceName(string root, string file)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        string? top = null;
        for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length > fullRoot.Length; dir = Path.GetDirectoryName(dir))
        {
            if (ManifestNames.Any(m => File.Exists(Path.Combine(dir, m))))
                return Path.GetFileName(dir);
            top = Path.GetFileName(dir);
        }
        return top ?? Path.GetFileName(fullRoot);
    }
}

// Objects of a game map file that the exported version no longer has (deleted now or in an earlier session).
public static class RemovedEntities
{
    // The entities of original missing from current, compared type by type (a moved object keeps its type, so only
    // the number of objects of each type counts).
    public static List<MapEntity> Find(IReadOnlyList<MapEntity> original, IEnumerable<MapEntity> current)
    {
        var remaining = new Dictionary<uint, int>();
        foreach (var entity in current)
            remaining[entity.ArchetypeHash] = remaining.GetValueOrDefault(entity.ArchetypeHash) + 1;

        var removed = new List<MapEntity>();
        foreach (var entity in original)
        {
            int left = remaining.GetValueOrDefault(entity.ArchetypeHash);
            if (left > 0)
                remaining[entity.ArchetypeHash] = left - 1;
            else
                removed.Add(entity);
        }
        return removed;
    }

    // Part of the scenery: a detailed object of the level-of-detail tree whose collision is in the area's .ybn files,
    // so it stays in the game when the object is deleted. Props carry their own collision and are left out: archetype
    // flag 0x20000000 (set on every prop of the props archives), breakable model (fragment) or own physics
    // dictionary.
    public static bool IsScenery(MapEntity entity, Archetype? archetype) =>
        entity.LodLevel == LodLevel.Hd && entity.ParentIndex >= 0
        && (archetype is null || (archetype.Flags & 0x20000000) == 0 && archetype.AssetType != ArchetypeAssetType.Fragment
            && archetype.PhysicsDictionaryHash == 0);
}

// Objects sharing the same guid in the exported map files (e.g. objects copied by hand in another editor): the game
// tolerates it, but scripts and tools that find an object by its guid get the wrong one.
public static class DuplicateGuids
{
    public sealed record Duplicate(uint Guid, uint ArchetypeHash, IReadOnlyList<string> Files);

    public static List<Duplicate> Find(IEnumerable<(string FileName, IEnumerable<MapEntity> Entities)> maps)
    {
        var seen = new Dictionary<uint, (uint Type, List<string> Files)>();
        foreach (var (fileName, entities) in maps)
        {
            foreach (var entity in entities)
            {
                if (entity.Guid == 0)
                    continue;
                if (!seen.TryGetValue(entity.Guid, out var entry))
                    seen[entity.Guid] = entry = (entity.ArchetypeHash, []);
                entry.Files.Add(fileName);
            }
        }
        return seen.Where(p => p.Value.Files.Count > 1)
            .Select(p => new Duplicate(p.Key, p.Value.Type, p.Value.Files))
            .OrderBy(d => d.Files[0], StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Guid)
            .ToList();
    }
}
