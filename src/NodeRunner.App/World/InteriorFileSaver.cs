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
                if (interiors.Count == 0 || files.ReadFile(path) is not { } source || files.GetResourceHeader(path) is not { } header)
                    throw new InvalidDataException("fichier du jeu introuvable");
                var written = writer.Write(source, interiors);
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
                || expected.Zip(actual).Any(p => System.Numerics.Vector3.Distance(p.First.Position, p.Second.Position) > Tolerance))
                throw new InvalidDataException("vérification échouée (position des objets)");
        }
    }
}
