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
                    throw new InvalidDataException(L("fichier du jeu introuvable"));
                var written = writer.Write(source, interiors, Borrowed(files, library, YtypWriter.MissingStructures(source, interiors)));
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

    // Descriptions of the structures the file lacks (a first entity set or portal), from the first game .ytyp that has them.
    private static List<Data.Native.Resources.MetaDocument.StructureInfo>? Borrowed(Data.FileSystem.GameContent files, MapLibrary library,
        IReadOnlyList<uint> missing)
    {
        if (missing.Count == 0)
            return null;
        var found = new List<Data.Native.Resources.MetaDocument.StructureInfo>();
        foreach (uint hash in library.GetInteriorArchetypeHashes())
        {
            if (library.GetArchetypeFile(hash) is not { } file || Source(files, library, file) is not { } source)
                continue;
            foreach (uint structure in missing)
            {
                if (found.All(f => f.NameHash != structure) && YtypWriter.Structure(source, structure) is { } info)
                    found.Add(info);
            }
            if (found.Count == missing.Count)
                break;
        }
        return found;
    }

    private static void Verify(byte[] file, List<Archetype> interiors)
    {
        var written = new YtypReader().Read(ResourceFile.Read(file).Content);
        foreach (var interior in interiors)
        {
            var read = written.Archetypes.FirstOrDefault(a => a.NameHash == interior.NameHash)
                       ?? throw new InvalidDataException(L("vérification échouée (intérieur absent)"));
            var expected = interior.InteriorEntities.Where(e => !e.IsDeleted).Concat(interior.InteriorEntitySets.SelectMany(s => s.Entities).Where(e => !e.IsDeleted)).ToList();
            var actual = read.InteriorEntities.Concat(read.InteriorEntitySets.SelectMany(s => s.Entities)).ToList();
            if (expected.Count != actual.Count
                || expected.Zip(actual).Any(p => Vector3.Distance(p.First.Position, p.Second.Position) > Tolerance))
                throw new InvalidDataException(L("vérification échouée (position des objets)"));
            if (read.InteriorRooms.Count != interior.InteriorRooms.Count
                || read.InteriorRooms.Zip(interior.InteriorRooms).Any(p => p.First.Name != p.Second.Name || p.First.Flags != p.Second.Flags
                    || p.First.TimecycleHash != p.Second.TimecycleHash || p.First.SecondaryTimecycleHash != p.Second.SecondaryTimecycleHash
                    || Vector3.Distance(p.First.BoundsMin, p.Second.BoundsMin) > Tolerance || Vector3.Distance(p.First.BoundsMax, p.Second.BoundsMax) > Tolerance))
                throw new InvalidDataException(L("vérification échouée (salles)"));
            if (read.InteriorPortals.Count != interior.InteriorPortals.Count
                || read.InteriorPortals.Zip(interior.InteriorPortals).Any(p => p.First.RoomFrom != p.Second.RoomFrom || p.First.RoomTo != p.Second.RoomTo
                    || p.First.Flags != p.Second.Flags || p.First.Opacity != p.Second.Opacity || p.First.MirrorPriority != p.Second.MirrorPriority
                    || p.First.AudioOcclusionHash != p.Second.AudioOcclusionHash || p.First.Corners.Count != p.Second.Corners.Count
                    || p.First.Corners.Zip(p.Second.Corners).Any(c => Vector3.Distance(c.First, c.Second) > Tolerance)))
                throw new InvalidDataException(L("vérification échouée (portails)"));
        }
    }
}
