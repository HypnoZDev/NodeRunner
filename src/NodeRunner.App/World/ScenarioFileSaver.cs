using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Scenarios;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.App.World;

// Writes the modified scenario region files (.ymt) to a folder, with their game name, like PathFileSaver.
internal static class ScenarioFileSaver
{
    public static void Save(WorldLoader world, IEnumerable<ScenarioRegion> regions, string folder, MapFileSaver.Report report, bool markSaved = true)
    {
        if (world.Files is not { } files || world.Scenarios is not { } set)
            return;
        Directory.CreateDirectory(folder);
        foreach (var region in regions)
        {
            string name = region.Name + ".ymt";
            try
            {
                if (region.Path is not { } path || files.GetResourceHeader(path) is not { } header)
                    throw new InvalidDataException(L("fichier du jeu introuvable"));
                var source = region.SavedContent ?? files.ReadFile(path) ?? throw new InvalidDataException(L("fichier du jeu illisible"));
                var (content, pageFlags) = ScenarioRegionWriter.Write(source, region);
                var written = new ScenarioRegionReader().Read(content, region.Name);
                // Laid out again, the region's own points are in the order of the grid: compared as sets.
                var expected = region.Points.GroupBy(p => p.Values).ToDictionary(g => g.Key, g => g.Count());
                if (written.Points.Count != region.Points.Count
                    || written.Points.GroupBy(p => p.Values).Any(g => expected.GetValueOrDefault(g.Key) != g.Count()))
                    throw new InvalidDataException(L("vérification échouée (points)"));
                var file = ResourceFile.Write(pageFlags is { } flags
                    ? header with { SystemFlags = (header.SystemFlags & 0xF0000000) | flags }
                    : header, content);
                string target = Path.Combine(folder, name);
                string temporary = target + ".tmp";
                File.WriteAllBytes(temporary, file);
                File.Move(temporary, target, overwrite: true);
                if (markSaved)
                    set.MarkSaved(region);
                report.Saved.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                report.Errors.Add($"{name} : {ex.Message}");
            }
        }
    }

    // The scenario region files of a folder, uncompressed, by name (without extension); unreadable ones are reported.
    public static Dictionary<string, byte[]> ReadSaved(string? folder, IReadOnlyCollection<string> regionNames, List<string> errors)
    {
        var regions = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder))
            return regions;
        var known = new HashSet<string>(regionNames, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.ymt"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!known.Contains(name))
                continue;
            try
            {
                regions[name] = ResourceFile.Read(File.ReadAllBytes(file)).Content;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)} : {ex.Message}");
            }
        }
        return regions;
    }
}
