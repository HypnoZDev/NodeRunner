using System.Numerics;
using System.Text.RegularExpressions;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.NavMeshes;

// Position of a navmesh tile on the map grid (one tile = one navmesh[X][Y].ynv file).
public readonly record struct NavMeshTileKey(int X, int Y)
{
    public string FileName => $"navmesh[{X}][{Y}].ynv";
}

// Knows which navmesh file covers which part of the map, and reads tiles on demand.
public sealed partial class NavMeshCatalog
{
    public const float TileSize = 150f;
    private const int IndexStep = 3;
    private const int IndexAtOrigin = 120;

    private readonly IGameFileSystem _files;
    private readonly INavMeshReader _reader;
    private readonly Dictionary<NavMeshTileKey, string> _paths = new();

    private NavMeshCatalog(IGameFileSystem files, INavMeshReader reader)
    {
        _files = files;
        _reader = reader;
    }

    public int TileCount => _paths.Count;

    // Lists the map navmeshes of the base game and update.rpf (DLC packs are not handled yet).
    public static NavMeshCatalog Create(IGameFileSystem files, INavMeshReader reader)
    {
        var catalog = new NavMeshCatalog(files, reader);
        foreach (var path in files.EnumerateFiles(".ynv"))
        {
            if (path.Contains("dlcpacks", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = TileFileName().Match(Path.GetFileName(path));
            if (!match.Success)
                continue; // Vehicle navmeshes (trailers, boats...) are not part of the map.

            var key = new NavMeshTileKey(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            if (!catalog._paths.TryGetValue(key, out var existing) || IsUpdatePath(path) && !IsUpdatePath(existing))
                catalog._paths[key] = path;
        }
        return catalog;
    }

    public bool Contains(NavMeshTileKey key) => _paths.ContainsKey(key);

    // Reads a tile, or returns null if no file covers this square.
    public NavMeshTile? ReadTile(NavMeshTileKey key)
    {
        if (!_paths.TryGetValue(key, out var path))
            return null;
        var data = _files.ReadFile(path);
        return data is null || data.Length == 0 ? null : _reader.Read(data);
    }

    public static NavMeshTileKey TileAt(Vector2 position) => new(
        (int)MathF.Floor(position.X / TileSize) * IndexStep + IndexAtOrigin,
        (int)MathF.Floor(position.Y / TileSize) * IndexStep + IndexAtOrigin);

    public static Vector2 TileCenter(NavMeshTileKey key) => new(
        ((key.X - IndexAtOrigin) / IndexStep + 0.5f) * TileSize,
        ((key.Y - IndexAtOrigin) / IndexStep + 0.5f) * TileSize);

    public IEnumerable<NavMeshTileKey> TilesAround(Vector2 position, float radius)
    {
        int range = (int)MathF.Ceiling(radius / TileSize) + 1;
        var center = TileAt(position);
        var tiles = new List<(NavMeshTileKey Key, float Distance)>();
        for (int dy = -range; dy <= range; dy++)
        {
            for (int dx = -range; dx <= range; dx++)
            {
                var key = new NavMeshTileKey(center.X + dx * IndexStep, center.Y + dy * IndexStep);
                float distance = Vector2.Distance(TileCenter(key), position);
                if (distance <= radius + TileSize * 0.71f && _paths.ContainsKey(key))
                    tiles.Add((key, distance));
            }
        }
        return tiles.OrderBy(t => t.Distance).Select(t => t.Key);
    }

    private static bool IsUpdatePath(string path) =>
        path.StartsWith("update", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^navmesh\[(\d+)\]\[(\d+)\]\.ynv$", RegexOptions.IgnoreCase)]
    private static partial Regex TileFileName();
}
