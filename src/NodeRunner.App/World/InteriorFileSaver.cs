using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.App.World;

// Writes the archetype files (.ytyp) of the interiors whose objects were moved to a folder, with their game name.
internal static class InteriorFileSaver
{
    private const float Tolerance = 0.001f;

    public static void Save(WorldLoader world, IEnumerable<string> paths, string folder, MapFileSaver.Report report)
    {
        if (world.Content is not { } files || world.Maps is not { } library)
            return;
        Directory.CreateDirectory(folder);
        var writer = new YtypWriter();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            try
            {
                var interiors = library.InteriorsInFile(path);
                if (interiors.Count == 0 || Source(files, library, path) is not { } source || files.GetResourceHeader(path) is not { } header)
                    throw new InvalidDataException("fichier du jeu introuvable");
                var written = writer.Write(source, interiors, NeedsEntitySetInfo(source, interiors) ? EntitySetInfo(files, library) : null);
                var file = ResourceFile.Write(header with { SystemFlags = (header.SystemFlags & 0xF0000000) | written.SystemPageFlags }, written.Content);
                Verify(file, interiors);

                string target = Path.Combine(folder, name);
                string temporary = target + ".tmp";
                File.WriteAllBytes(temporary, file);
                File.Move(temporary, target, overwrite: true);
                report.Saved.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
    }

    // The file as it was when the map was loaded: a project's file is read from disk, and saving overwrites it, while
    // the interiors' objects still match its first version.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapLibrary, Dictionary<string, byte[]>> Sources = new();

    private static byte[]? Source(Data.FileSystem.GameContent files, MapLibrary library, string path)
    {
        var cache = Sources.GetOrCreateValue(library);
        lock (cache)
        {
            if (!cache.TryGetValue(path, out var source) && files.ReadFile(path) is { } read)
                cache[path] = source = read;
            return source;
        }
    }

    private static bool NeedsEntitySetInfo(byte[] source, List<Archetype> interiors) =>
        interiors.Any(i => i.InteriorEntitySets.Count > 0) && YtypWriter.EntitySetInfo(source) is null;

    // The description of entity sets, from the first game .ytyp that has sets.
    private static Data.Native.Resources.MetaDocument.StructureInfo? EntitySetInfo(Data.FileSystem.GameContent files, MapLibrary library)
    {
        foreach (uint hash in library.GetInteriorArchetypeHashes())
        {
            if (library.GetArchetype(hash) is not { InteriorEntitySets.Count: > 0 } || library.GetArchetypeFile(hash) is not { } file
                || Source(files, library, file) is not { } source)
                continue;
            if (YtypWriter.EntitySetInfo(source) is { } info)
                return info;
        }
        return null;
    }

    private static void Verify(byte[] file, List<Archetype> interiors)
    {
        var written = new YtypReader().Read(ResourceFile.Read(file).Content);
        foreach (var interior in interiors)
        {
            var read = written.Archetypes.FirstOrDefault(a => a.NameHash == interior.NameHash)
                       ?? throw new InvalidDataException("vérification échouée (intérieur absent)");
            var expected = interior.InteriorEntities.Where(e => !e.IsDeleted).Concat(interior.InteriorEntitySets.SelectMany(s => s.Entities).Where(e => !e.IsDeleted)).ToList();
            var actual = read.InteriorEntities.Concat(read.InteriorEntitySets.SelectMany(s => s.Entities)).ToList();
            if (expected.Count != actual.Count
                || expected.Zip(actual).Any(p => Vector3.Distance(p.First.Position, p.Second.Position) > Tolerance))
                throw new InvalidDataException("vérification échouée (position des objets)");
            if (read.InteriorRooms.Count != interior.InteriorRooms.Count
                || read.InteriorRooms.Zip(interior.InteriorRooms).Any(p => p.First.Name != p.Second.Name || p.First.Flags != p.Second.Flags
                    || p.First.TimecycleHash != p.Second.TimecycleHash || p.First.SecondaryTimecycleHash != p.Second.SecondaryTimecycleHash
                    || Vector3.Distance(p.First.BoundsMin, p.Second.BoundsMin) > Tolerance || Vector3.Distance(p.First.BoundsMax, p.Second.BoundsMax) > Tolerance))
                throw new InvalidDataException("vérification échouée (salles)");
        }
    }
}
