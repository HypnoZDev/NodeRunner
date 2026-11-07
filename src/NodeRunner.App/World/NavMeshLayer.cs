using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using NodeRunner.Data.NavMeshes;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Streams the navmesh around the camera: tiles within the chosen radius are read on a background thread (nearest
// first), turned into GPU meshes a few per frame, and released when the camera moves away.
internal sealed class NavMeshLayer : IDisposable
{
    public const float MinRadius = 150f;
    public const float MaxRadius = 1500f;
    public const string OutlinesLayer = "NavMesh.Outlines";

    private const int MaxPendingReads = 8;
    private const int MaxMeshBuildsPerFrame = 2;
    private const float UnloadMargin = 300f;
    private const float SurfaceOffset = 0.03f;
    private const float OutlineOffset = 0.06f;
    private const float SurfaceAlpha = 0.45f;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.4f, 0.3f, 0.85f));

    public static readonly IReadOnlyList<NavMeshSurface> Surfaces = Enum.GetValues<NavMeshSurface>();

    private readonly Dictionary<NavMeshTileKey, TileMeshes> _tiles = new();
    private readonly HashSet<NavMeshTileKey> _pending = new();
    private readonly Channel<(NavMeshTileKey Key, int Generation)> _requests = Channel.CreateUnbounded<(NavMeshTileKey, int)>();
    private readonly ConcurrentQueue<(NavMeshTileKey Key, int Generation, NavMeshTile? Tile, string? Error)> _results = new();
    private readonly CancellationTokenSource _stop = new();

    private NavMeshCatalog? _catalog;
    private volatile int _generation;

    public NavMeshLayer()
    {
        Task.Run(ReadTilesInBackground);
    }

    public static string Label(NavMeshSurface surface) => surface switch
    {
        NavMeshSurface.Pavement => L("Trottoirs et chemins"),
        NavMeshSurface.Road => L("Routes"),
        NavMeshSurface.Ground => L("Sol (terrain, places...)"),
        NavMeshSurface.SteepSlope => L("Pentes raides"),
        NavMeshSurface.Interior => L("Intérieurs"),
        NavMeshSurface.Water => L("Eau"),
        NavMeshSurface.TrainTrack => L("Voies ferrées"),
        _ => surface.ToString(),
    };

    public static Vector4 Color(NavMeshSurface surface) => surface switch
    {
        NavMeshSurface.Pavement => new(0.25f, 0.85f, 0.35f, 1f),
        NavMeshSurface.Road => new(0.55f, 0.58f, 0.65f, 1f),
        NavMeshSurface.Ground => new(0.8f, 0.68f, 0.42f, 1f),
        NavMeshSurface.SteepSlope => new(0.9f, 0.3f, 0.25f, 1f),
        NavMeshSurface.Interior => new(0.65f, 0.45f, 0.95f, 1f),
        NavMeshSurface.Water => new(0.2f, 0.5f, 1f, 1f),
        NavMeshSurface.TrainTrack => new(0.95f, 0.8f, 0.2f, 1f),
        _ => Vector4.One,
    };

    public static string LayerName(NavMeshSurface surface) => "NavMesh." + surface;

    public int LoadedTileCount => _tiles.Count;
    public int PendingTileCount => _pending.Count;
    public int AvailableTileCount => _catalog?.TileCount ?? 0;
    public int TriangleCount => _tiles.Values.Sum(t => t.TriangleCount);

    public List<string> Errors { get; } = new();

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, float radius, bool enabled)
    {
        if (loader.NavMeshes != _catalog)
        {
            Clear();
            _catalog = loader.NavMeshes;
        }
        if (_catalog is null)
            return;

        var position = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (enabled)
            RequestTiles(position, radius);

        BuildArrivedTiles(renderer);
        ReleaseFarTiles(position, enabled ? radius + UnloadMargin : -1f);
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<NavMeshSurface, bool> isVisible, bool outlines)
    {
        // Surfaces first, then outlines, so outlines are never hidden by the translucent surfaces.
        foreach (var tile in _tiles.Values)
        {
            for (int i = 0; i < tile.Surfaces.Length; i++)
            {
                if (tile.Surfaces[i] is { } mesh && isVisible((NavMeshSurface)i))
                    renderer.Draw(mesh, viewProjection, cameraPosition);
            }
        }

        if (!outlines)
            return;
        foreach (var tile in _tiles.Values)
        {
            for (int i = 0; i < tile.Outlines.Length; i++)
            {
                if (tile.Outlines[i] is { } mesh && isVisible((NavMeshSurface)i))
                    renderer.Draw(mesh, viewProjection, cameraPosition);
            }
        }
    }

    private void RequestTiles(Vector2 position, float radius)
    {
        foreach (var key in _catalog!.TilesAround(position, radius))
        {
            if (_pending.Count >= MaxPendingReads)
                break;
            if (_tiles.ContainsKey(key) || !_pending.Add(key))
                continue;
            _requests.Writer.TryWrite((key, _generation));
        }
    }

    private void BuildArrivedTiles(ColorRenderer renderer)
    {
        for (int built = 0; built < MaxMeshBuildsPerFrame && _results.TryDequeue(out var result);)
        {
            if (result.Generation != _generation)
                continue; // Result of a previous game installation.

            _pending.Remove(result.Key);
            if (result.Error is not null)
            {
                Errors.Add($"{result.Key.FileName} : {result.Error}");
                _tiles[result.Key] = TileMeshes.Empty; // Don't retry a damaged file every frame.
                continue;
            }

            _tiles[result.Key] = result.Tile is null ? TileMeshes.Empty : BuildMeshes(result.Tile, renderer);
            built++;
        }
    }

    private void ReleaseFarTiles(Vector2 position, float keepRadius)
    {
        var far = _tiles.Keys
            .Where(k => keepRadius < 0f || Vector2.Distance(NavMeshCatalog.TileCenter(k), position) > keepRadius)
            .ToList();
        foreach (var key in far)
        {
            _tiles[key].Dispose();
            _tiles.Remove(key);
        }
    }

    private static TileMeshes BuildMeshes(NavMeshTile tile, ColorRenderer renderer)
    {
        var surfaces = Surfaces.Select(_ => new TriangleBatch()).ToArray();
        var outlines = Surfaces.Select(_ => new LineBatch()).ToArray();
        var corners = new List<Vector3>(16);

        foreach (var polygon in tile.Polygons)
        {
            corners.Clear();
            corners.AddRange(tile.PolygonVertices(polygon));
            if (corners.Count < 3)
                continue;

            int kind = (int)polygon.Surface;
            var baseColor = Color(polygon.Surface);

            // Simple lighting so slopes and steps are readable: brighter when facing the light.
            float shade = 0.55f + 0.45f * MathF.Abs(Vector3.Dot(Normal(corners), LightDirection));
            var surfaceColor = new Vector4(baseColor.X * shade, baseColor.Y * shade, baseColor.Z * shade, SurfaceAlpha);
            var outlineColor = new Vector4(
                MathF.Min(1f, baseColor.X * 1.2f), MathF.Min(1f, baseColor.Y * 1.2f), MathF.Min(1f, baseColor.Z * 1.2f), 0.9f);

            // Navmesh polygons are convex: a fan from the first corner covers them.
            var up = new Vector3(0f, 0f, SurfaceOffset);
            for (int i = 1; i < corners.Count - 1; i++)
                surfaces[kind].AddTriangle(corners[0] + up, corners[i] + up, corners[i + 1] + up, surfaceColor);

            var lift = new Vector3(0f, 0f, OutlineOffset);
            for (int i = 0; i < corners.Count; i++)
                outlines[kind].AddLine(corners[i] + lift, corners[(i + 1) % corners.Count] + lift, outlineColor);
        }

        return new TileMeshes(
            surfaces.Select(renderer.CreateMesh).ToArray(),
            outlines.Select(renderer.CreateMesh).ToArray(),
            surfaces.Sum(b => b.TriangleCount));
    }

    private static Vector3 Normal(List<Vector3> corners)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < corners.Count; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Count];
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitZ;
    }

    private async Task ReadTilesInBackground()
    {
        try
        {
            await foreach (var (key, generation) in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                var catalog = _catalog;
                if (generation != _generation || catalog is null)
                    continue;
                try
                {
                    _results.Enqueue((key, generation, catalog.ReadTile(key), null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) // Any error would stop the loop for good.
                {
                    if (ex is not (InvalidDataException or IOException))
                        Diagnostics.ErrorLog.Add(L("Navmesh"), ex);
                    _results.Enqueue((key, generation, null, ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Application closing.
        }
    }

    private void Clear()
    {
        _generation++;
        foreach (var tile in _tiles.Values)
            tile.Dispose();
        _tiles.Clear();
        _pending.Clear();
        Errors.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _requests.Writer.TryComplete();
        Clear();
    }

    private sealed class TileMeshes(ColorMesh?[] surfaces, ColorMesh?[] outlines, int triangleCount) : IDisposable
    {
        public static readonly TileMeshes Empty = new([], [], 0);

        public ColorMesh?[] Surfaces { get; } = surfaces;
        public ColorMesh?[] Outlines { get; } = outlines;
        public int TriangleCount { get; } = triangleCount;

        public void Dispose()
        {
            foreach (var mesh in Surfaces) mesh?.Dispose();
            foreach (var mesh in Outlines) mesh?.Dispose();
        }
    }
}
