using System.Xml;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Scenarios;

// Every scenario region of the map.
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

    public List<string> Errors { get; } = new();

    private Dictionary<uint, string> _typeNames = new();
    private readonly HashSet<ScenarioRegion> _changed = new();
    private readonly HashSet<ScenarioRegion> _saved = new();

    // Changes at each edit (the layer is drawn again).
    public int Revision { get; private set; }

    // Regions to write when saving: changed since loading, or taken back from the project.
    public IEnumerable<ScenarioRegion> RegionsToSave => Regions.Where(r => _changed.Contains(r) || _saved.Contains(r));

    public string? TypeName(uint hash) => _typeNames.GetValueOrDefault(hash);

    public void SetValues(ScenarioPoint point, ScenarioValues values)
    {
        point.Values = values;
        point.TypeName = TypeName(values.TypeHash);
        point.Category = ScenarioCategories.FromTypeName(point.TypeName);
        if (point.Region is { } region)
            _changed.Add(region);
        Revision++;
    }

    public void MarkSaved(ScenarioRegion region) => _saved.Add(region);

    // Every scenario type the game lists, by name.
    public IEnumerable<(uint Hash, string Name)> Types => _typeNames.Select(p => (p.Key, p.Value)).OrderBy(t => t.Value, StringComparer.Ordinal);

    // Adds a point among the region's own points (at index, by default after them). template: a point of the same
    // region whose other fields (models, group, interior...) the new one takes.
    public ScenarioPoint AddPoint(ScenarioRegion region, ScenarioValues values, ScenarioPoint? template, int? index = null)
    {
        if (template is not null && template.Region != region)
            throw new ArgumentException("The template must be a point of the same region.", nameof(template));
        var point = new ScenarioPoint
        {
            Position = values.Position,
            Source = template?.Source?.ToArray(),
            PedModelSetHash = template?.PedModelSetHash ?? 0,
            VehicleModelSetHash = template?.VehicleModelSetHash ?? 0,
            GroupHash = template?.GroupHash ?? 0,
            InteriorHash = template?.InteriorHash ?? 0,
            RequiredMapHash = template?.RequiredMapHash ?? 0,
        };
        InsertPoint(region, Math.Clamp(index ?? region.OwnPointCount, 0, region.OwnPointCount), point, values);
        return point;
    }

    // Puts a deleted point back at its place.
    public void InsertPoint(ScenarioRegion region, int index, ScenarioPoint point, ScenarioValues values)
    {
        region.Insert(index, point);
        _changed.Add(region);
        SetValues(point, values);
    }

    // Deletes one of a region's own points (those of its clusters stay); its index before the deletion, or -1.
    public int DeletePoint(ScenarioPoint point)
    {
        if (point.Region is not { } region || point.Index >= region.OwnPointCount || region.Points[point.Index] != point)
            return -1;
        int index = point.Index;
        region.RemoveAt(index);
        _changed.Add(region);
        Revision++;
        return index;
    }

    // The point of a region closest to position (within maxDistance), or null.
    public ScenarioPoint? Nearest(System.Numerics.Vector3 position, float maxDistance = float.MaxValue)
    {
        ScenarioPoint? best = null;
        float bestDistance = maxDistance * maxDistance;
        foreach (var point in AllPoints)
        {
            float d = System.Numerics.Vector3.DistanceSquared(point.Position, position);
            if (d < bestDistance)
                (best, bestDistance) = (point, d);
        }
        return best;
    }

    // A copy of this set where the changed regions are read again from the game, and the user's saved files (by region
    // name) put in their place. Unreadable files are reported in errors.
    public ScenarioSet WithSavedRegions(IGameFileSystem files, IScenarioRegionReader reader, IReadOnlyDictionary<string, byte[]> saved, List<string> errors)
    {
        var set = new ScenarioSet { _typeNames = _typeNames };
        set.Errors.AddRange(Errors);
        foreach (var region in Regions)
        {
            if (!_changed.Contains(region) && !_saved.Contains(region) && !saved.ContainsKey(region.Name))
            {
                set.Regions.Add(region);
                continue;
            }
            try
            {
                bool fromProject = saved.TryGetValue(region.Name, out var data);
                if (!fromProject)
                    data = region.Path is { } path ? files.ReadFile(path) : null;
                if (data is null)
                    continue;
                var copy = set.Prepare(reader.Read(data, region.Name), region.Path);
                if (fromProject)
                {
                    copy.SavedContent = data;
                    set._saved.Add(copy);
                }
                set.Regions.Add(copy);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                errors.Add($"{region.Name}.ymt : {ex.Message}");
                set.Regions.Add(region);
            }
        }
        return set;
    }

    private ScenarioRegion Prepare(ScenarioRegion region, string? path)
    {
        region.Path = path;
        foreach (var (point, index) in region.Points.Select((p, i) => (p, i)))
        {
            point.Region = region;
            point.Index = index;
            point.TypeName = _typeNames.GetValueOrDefault(point.TypeHash);
            point.Category = ScenarioCategories.FromTypeName(point.TypeName);
        }
        return region;
    }

    public IEnumerable<ScenarioPoint> AllPoints => Regions.SelectMany(r => r.Points);

    // Reads every scenario region of the base game.
    public static ScenarioSet Load(IGameFileSystem files, IScenarioRegionReader reader, IProgress<string>? progress = null)
    {
        var set = new ScenarioSet();
        set._typeNames = LoadTypeNames(files, set.Errors);

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

                set.Regions.Add(set.Prepare(reader.Read(data, name), path));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                set.Errors.Add($"{path} : {ex.Message}");
            }
        }

        return set;
    }

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
