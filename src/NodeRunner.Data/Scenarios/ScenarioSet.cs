using System.Xml;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Scenarios;

/// <summary>Every scenario region of the map.</summary>
public sealed class ScenarioSet
{
    private const string RegionFolder = @"\levels\gta5\scenario\";

    // Lists every scenario type by name; the newest copy (update.rpf) is listed first.
    private static readonly string[] ScenarioInfoFiles =
    [
        @"update\update.rpf\common\data\ai\scenarios.meta",
        @"common.rpf\data\ai\scenarios.meta",
    ];

    public List<ScenarioRegion> Regions { get; } = new();

    public int PointCount => Regions.Sum(r => r.Points.Count);

    /// <summary>Files that could not be read, with the reason (shown to the user, not fatal).</summary>
    public List<string> Errors { get; } = new();

    public IEnumerable<ScenarioPoint> AllPoints => Regions.SelectMany(r => r.Points);

    /// <summary>
    /// Reads every scenario region of the base game. When a region exists in both the base archives
    /// and update.rpf, the update version wins, as in the game.
    /// </summary>
    public static ScenarioSet Load(IGameFileSystem files, IScenarioRegionReader reader, IProgress<string>? progress = null)
    {
        var set = new ScenarioSet();
        var typeNames = LoadTypeNames(files, set.Errors);

        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.EnumerateFiles(".ymt"))
        {
            if (!path.Contains(RegionFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("manifest", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!chosen.TryGetValue(name, out var existing) || IsUpdatePath(path) && !IsUpdatePath(existing))
                chosen[name] = path;
        }

        int done = 0;
        foreach (var (name, path) in chosen.OrderBy(p => p.Key))
        {
            if (++done % 20 == 0)
                progress?.Report($"Lecture des scénarios : {done} / {chosen.Count} fichiers");

            try
            {
                var data = files.ReadFile(path);
                if (data is null || data.Length == 0)
                    continue;

                var region = reader.Read(data, name);
                foreach (var point in region.Points)
                {
                    point.TypeName = typeNames.GetValueOrDefault(point.TypeHash);
                    point.Category = ScenarioCategories.FromTypeName(point.TypeName);
                }
                set.Regions.Add(region);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                set.Errors.Add($"{path} : {ex.Message}");
            }
        }

        return set;
    }

    /// <summary>Hash -> name of every scenario type, read from the game's scenarios.meta (plain XML).</summary>
    private static Dictionary<uint, string> LoadTypeNames(IGameFileSystem files, List<string> errors)
    {
        var names = new Dictionary<uint, string>();
        foreach (var path in ScenarioInfoFiles)
        {
            var data = files.ReadFile(path);
            if (data is null)
                continue;

            try
            {
                using var xml = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                while (xml.Read())
                {
                    if (xml.NodeType == XmlNodeType.Element && xml.Name == "Name")
                    {
                        var name = xml.ReadElementContentAsString().Trim();
                        if (name.Length > 0)
                            names.TryAdd(Joaat.Hash(name), name);
                    }
                }
            }
            catch (XmlException ex)
            {
                errors.Add($"{path} : {ex.Message}");
            }
        }
        return names;
    }

    private static bool IsUpdatePath(string path) =>
        path.StartsWith("update", StringComparison.OrdinalIgnoreCase);
}
