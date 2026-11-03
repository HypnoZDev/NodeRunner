using System.Numerics;
using System.Text.RegularExpressions;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Paths;

// The whole road and pedestrian network: every nodesN.ynd file of the map.
public sealed partial class PathNetwork
{
    private readonly PathNodeArea?[] _areas = new PathNodeArea?[PathNodeArea.AreaCount];

    public IEnumerable<PathNodeArea> Areas => _areas.OfType<PathNodeArea>();

    public int NodeCount => Areas.Sum(a => a.Nodes.Count);
    public int LinkCount => Areas.Sum(a => a.Links.Count);

    public List<string> Errors { get; } = new();

    // Incremented on every change, so that what is drawn from the network can be rebuilt.
    public int Revision { get; private set; }

    public void MoveNode(PathNode node, Vector3 position)
    {
        node.Position = position;
        Revision++;
    }

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
                    network._areas[areaId] = area;
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
