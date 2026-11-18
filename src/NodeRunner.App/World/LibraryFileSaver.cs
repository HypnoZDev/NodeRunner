using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// FiveM export: copies into the resource the prop library's files that its maps and interiors use (PropLibrary).
internal static class LibraryFileSaver
{
    // written: names already in the stream folder (the project's own files win over the library's).
    public static List<string> Copy(WorldLoader world, string stream, IReadOnlyCollection<string> written, MapFileSaver.Report report)
    {
        var copied = new List<string>();
        if (world.ProjectFiles is not { LibraryFileCount: > 0 } overlay || world.Maps is not { } library || world.Models is not { } models
            || world.Content is not { } content)
            return copied;
        var used = UsedTypes(stream, written);
        if (used.Count == 0)
            return copied;

        var names = written.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, disk) in Needed(world, used))
        {
            if (names.Contains(name))
                continue;
            try
            {
                Directory.CreateDirectory(stream);
                File.Copy(disk, Path.Combine(stream, name), overwrite: true);
                copied.Add(name);
                names.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
        return copied;
    }

    // The library's files these object types need (file name, file on disk).
    public static List<(string Name, string Disk)> Needed(WorldLoader world, IReadOnlyCollection<uint> used)
    {
        var found = new List<(string, string)>();
        if (used.Count == 0 || world.ProjectFiles is not { LibraryFileCount: > 0 } overlay || world.Maps is not { } library
            || world.Models is not { } models || world.Content is not { } content)
            return found;
        var paths = new[] { ".ytyp", ".ydr", ".ydd", ".yft", ".ytd", ".ybn" }
            .SelectMany(e => content.EnumerateFiles(e)).Where(Data.FileSystem.GameContent.IsLibraryFile);
        foreach (var path in PropLibrary.Dependencies(used, library.GetArchetype, library.GetArchetypeFile, a => models.Locate(a)?.Path, paths))
        {
            if (overlay.LibraryFileOf(path) is { } file)
                found.Add((Path.GetFileName(path), file.Disk));
        }
        return found;
    }

    // Library types placed on the loaded map (objects of the maps and of the interiors, entity sets included, shown or
    // not: another participant may turn a set on).
    public static HashSet<uint> PlacedTypes(WorldLoader world) =>
        world.Maps is { } library && library.LibraryArchetypes() is { Count: > 0 } types ? PlacedTypes(library.Maps, types) : [];

    internal static HashSet<uint> PlacedTypes(IEnumerable<MapData> maps, IReadOnlySet<uint> types)
    {
        var used = new HashSet<uint>();
        foreach (var map in maps)
        {
            foreach (var entity in map.Entities)
            {
                var inside = entity.InteriorEntities.Concat(entity.EntitySets.SelectMany(s => s.Entities));
                foreach (var placed in inside.Prepend(entity))
                {
                    if (types.Contains(placed.ArchetypeHash))
                        used.Add(placed.ArchetypeHash);
                }
            }
        }
        return used;
    }

    // Object types placed by the exported maps and inside the exported interiors.
    private static HashSet<uint> UsedTypes(string stream, IEnumerable<string> files)
    {
        var used = new HashSet<uint>();
        foreach (string name in files)
        {
            bool map = name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase);
            if (!map && !name.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var content = ResourceFile.Read(File.ReadAllBytes(Path.Combine(stream, name))).Content;
                if (map)
                {
                    foreach (var entity in new YmapReader().Read(content).Entities)
                        used.Add(entity.ArchetypeHash);
                }
                else
                {
                    foreach (var archetype in new YtypReader().Read(content).Archetypes)
                    {
                        foreach (var entity in archetype.InteriorEntities.Concat(archetype.InteriorEntitySets.SelectMany(s => s.Entities)))
                            used.Add(entity.ArchetypeHash);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Occlusion or grass only, or locked by FiveM: no objects to list.
            }
        }
        return used;
    }
}
