using System.Numerics;
using System.Text.RegularExpressions;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.NavMeshes;

/// <summary>Position of a navmesh tile on the map grid (one tile = one navmesh[X][Y].ynv file).</summary>
public readonly record struct NavMeshTileKey(int X, int Y)
{
    public string FileName => $"navmesh[{X}][{Y}].ynv";
}

/// <summary>
/// Knows which navmesh file covers which part of the map, and reads tiles on demand.
/// Tiles are 150 x 150 m squares. File indices go in steps of 3 (one step = 50 m) and index 120 is
/// at coordinate 0, so navmesh[X][Y] covers x from (X - 120) * 50 to that + 150 (same for y).
/// </summary>
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

    /// <summary>
    /// Lists the map navmeshes of the base game and update.rpf (DLC packs are not handled yet).
    /// Files of the update win over the base game, as in the game.
    /// </summary>
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

    /// <summary>Reads a tile, or returns null if no file covers this square.</summary>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public NavMeshTile? ReadTile(NavMeshTileKey key)
    {
        if (!_paths.TryGetValue(key, out var path))
            return null;
        var data = _files.ReadFile(path);
        return data is null || data.Length == 0 ? null : _reader.Read(data);
    }

    /// <summary>The tile containing a world position.</summary>
    public static NavMeshTileKey TileAt(Vector2 position) => new(
        (int)MathF.Floor(position.X / TileSize) * IndexStep + IndexAtOrigin,
        (int)MathF.Floor(position.Y / TileSize) * IndexStep + IndexAtOrigin);

    /// <summary>Centre of a tile in world coordinates.</summary>
    public static Vector2 TileCenter(NavMeshTileKey key) => new(
        ((key.X - IndexAtOrigin) / IndexStep + 0.5f) * TileSize,
        ((key.Y - IndexAtOrigin) / IndexStep + 0.5f) * TileSize);

    /// <summary>Existing tiles whose centre is within <paramref name="radius"/> of a position, nearest first.</summary>
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
