using System.Numerics;
using System.Text.RegularExpressions;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Paths;

// The whole road and pedestrian network: every nodesN.ynd file of the map.
public sealed partial class PathNetwork
{
    private readonly PathNodeArea?[] _areas = new PathNodeArea?[PathNodeArea.AreaCount];
    private readonly string?[] _paths = new string?[PathNodeArea.AreaCount];
    private readonly HashSet<int> _changed = [];
    private readonly HashSet<int> _saved = [];

    public IEnumerable<PathNodeArea> Areas => _areas.OfType<PathNodeArea>();

    public int NodeCount => Areas.Sum(a => a.Nodes.Count);
    public int LinkCount => Areas.Sum(a => a.Links.Count);

    public List<string> Errors { get; } = new();

    // Incremented on every change, so that what is drawn from the network can be rebuilt.
    public int Revision { get; private set; }

    // Areas changed since loading (saved or not): the node's own, and those whose links reach it (their lengths change).
    public IReadOnlySet<int> ChangedAreas => _changed;

    // Areas taken back from the user's saved files, or saved since.
    public IReadOnlySet<int> SavedAreas => _saved;

    public void MarkSaved(int areaId) => _saved.Add(areaId);

    // Path of an area's file in the game's archives.
    public string? FilePath(int areaId) => areaId is >= 0 and < PathNodeArea.AreaCount ? _paths[areaId] : null;

    public static string FileName(int areaId) => $"nodes{areaId}.ynd";

    public void MoveNode(PathNode node, Vector3 position)
    {
        node.Position = position;
        Revision++;
        _changed.Add(node.Id.AreaId);
        foreach (int areaId in AreasLinkingTo(node.Id))
            _changed.Add(areaId);
    }

    // Links only reach the neighbouring areas.
    private IEnumerable<int> AreasLinkingTo(PathNodeId id)
    {
        int x = id.AreaId % PathNodeArea.GridSize, y = id.AreaId / PathNodeArea.GridSize;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int ax = x + dx, ay = y + dy;
            if (ax < 0 || ay < 0 || ax >= PathNodeArea.GridSize || ay >= PathNodeArea.GridSize)
                continue;
            if (_areas[ay * PathNodeArea.GridSize + ax] is { } area && area.Links.Any(l => l.Target == id))
                yield return area.AreaId;
        }
    }

    // Length to store for a link. The game's lengths are not exact distances (often rounded down, sometimes doubled):
    // when an end has moved, the game's value is scaled like the distance.
    public int LinkLength(PathNode from, PathLink link)
    {
        if (GetNode(link.Target) is not { } to || from.Position == from.GamePosition && to.Position == to.GamePosition)
            return link.Length;
        float before = Vector3.Distance(from.GamePosition, to.GamePosition);
        float after = Vector3.Distance(from.Position, to.Position);
        float length = before > 0.5f && link.Length > 0 ? link.Length * after / before : after;
        return Math.Clamp((int)MathF.Round(length), 1, 255);
    }

    // A copy of this network where the changed areas are read again from the game, and the user's saved files (by
    // area) put back: only node positions are taken from them, links stay the game's. A saved file whose nodes and
    // links do not match the game's is reported in errors and left out.
    public PathNetwork WithSavedAreas(IGameFileSystem files, IPathNodeReader reader, IReadOnlyDictionary<int, byte[]> saved, List<string> errors)
    {
        var network = new PathNetwork();
        Array.Copy(_areas, network._areas, _areas.Length);
        Array.Copy(_paths, network._paths, _paths.Length);
        network.Errors.AddRange(Errors);

        foreach (int areaId in _changed.Concat(_saved).Concat(saved.Keys).Distinct())
        {
            if (FilePath(areaId) is not { } path || files.ReadFile(path) is not { Length: > 0 } data)
            {
                if (saved.ContainsKey(areaId))
                    errors.Add($"{FileName(areaId)} : aucun fichier du jeu de ce nom");
                continue;
            }
            try
            {
                var area = reader.Read(data, areaId);
                network._areas[areaId] = area;
                if (!saved.TryGetValue(areaId, out var savedData))
                    continue;
                var mine = reader.Read(savedData, areaId);
                if (mine.Nodes.Count != area.Nodes.Count || mine.Links.Count != area.Links.Count
                    || mine.Nodes.Where((n, i) => n.Id != area.Nodes[i].Id).Any())
                {
                    errors.Add($"{FileName(areaId)} : nœuds différents de ceux du jeu, non repris");
                    continue;
                }
                for (int i = 0; i < area.Nodes.Count; i++)
                    area.Nodes[i].Position = mine.Nodes[i].Position;
                network._saved.Add(areaId);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                errors.Add($"{FileName(areaId)} : {ex.Message}");
            }
        }
        return network;
    }

    internal void SetArea(PathNodeArea area) => _areas[area.AreaId] = area;

    public PathNodeArea? GetArea(int areaId) =>
        areaId is >= 0 and < PathNodeArea.AreaCount ? _areas[areaId] : null;

    public PathNode? GetNode(PathNodeId id)
    {
        var area = GetArea(id.AreaId);
        if (area is null)
            return null;

        // Node numbers normally match their position in the file; fall back to a search otherwise.
        if (id.NodeId < area.Nodes.Count && area.Nodes[id.NodeId].Id == id)
            return area.Nodes[id.NodeId];
        return area.Nodes.FirstOrDefault(n => n.Id == id);
    }

    // Reads every nodesN.ynd of the game.
    public static PathNetwork Load(IGameFileSystem files, IPathNodeReader reader, IProgress<string>? progress = null)
    {
        var network = new PathNetwork();

        var chosen = new Dictionary<int, string>();
        foreach (var path in files.EnumerateFiles(".ynd"))
        {
            var match = NodesFileName().Match(Path.GetFileName(path));
            if (!match.Success)
                continue;

            int areaId = int.Parse(match.Groups[1].Value);
            if (areaId >= PathNodeArea.AreaCount)
                continue;

            if (!chosen.TryGetValue(areaId, out var existing) || IsUpdatePath(path) && !IsUpdatePath(existing))
                chosen[areaId] = path;
        }

        int done = 0;
        foreach (var (areaId, path) in chosen)
        {
            if (++done % 50 == 0)
                progress?.Report($"Lecture des pathnodes : {done} / {chosen.Count} fichiers");

            try
            {
                var data = files.ReadFile(path);
                if (data is null || data.Length == 0)
                    continue;
                var area = reader.Read(data, areaId);
                if (area.Nodes.Count > 0)
                {
                    network._areas[areaId] = area;
                    network._paths[areaId] = path;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                network.Errors.Add($"{path} : {ex.Message}");
            }
        }

        return network;
    }

    private static bool IsUpdatePath(string path) =>
        path.StartsWith("update", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^nodes(\d+)\.ynd$", RegexOptions.IgnoreCase)]
    private static partial Regex NodesFileName();
}
