using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using NodeRunner.Data.Collisions;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Streams the map collision (standard version, .ybn) around the camera: files within the chosen radius are read and
// turned into triangles on a background thread (nearest first), sent to the GPU a few per frame, and released when
// the camera moves away.
internal sealed class CollisionLayer : IDisposable
{
    public const float MinRadius = 50f;
    public const float MaxRadius = 600f;
    public const string LayerPrefix = "Collision.";

    private const int MaxPendingReads = 4;
    private const int MaxMeshUploadsPerFrame = 2;
    private const float UnloadMargin = 150f;

    // Lifted along the triangle normal so the surfaces do not flicker against the models they follow.
    private const float SurfaceOffset = 0.03f;
    private const float SurfaceAlpha = 0.5f;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.4f, 0.3f, 0.85f));

    public static readonly IReadOnlyList<CollisionSurface> Surfaces = Enum.GetValues<CollisionSurface>();

    private readonly Dictionary<CollisionFile, FileMeshes> _loaded = new();
    private readonly HashSet<CollisionFile> _pending = new();
    private readonly Channel<(CollisionFile File, int Generation)> _requests = Channel.CreateUnbounded<(CollisionFile, int)>();
    private readonly ConcurrentQueue<(CollisionFile File, int Generation, TriangleBatch[]? Batches, string? Error)> _results = new();
    private readonly CancellationTokenSource _stop = new();

    private CollisionCatalog? _catalog;
    private volatile int _generation;

    public CollisionLayer()
    {
        Task.Run(ReadFilesInBackground);
    }

    public static string Label(CollisionSurface surface) => surface switch
    {
        CollisionSurface.Road => "Routes, trottoirs, béton",
        CollisionSurface.Terrain => "Terrain (terre, sable, roche)",
        CollisionSurface.Vegetation => "Végétation (herbe, buissons)",
        CollisionSurface.Water => "Eau",
        CollisionSurface.Glass => "Verre",
        CollisionSurface.Building => "Bâtiments et objets",
        _ => surface.ToString(),
    };

    public static Vector4 Color(CollisionSurface surface) => surface switch
    {
        CollisionSurface.Road => new(0.62f, 0.64f, 0.7f, 1f),
        CollisionSurface.Terrain => new(0.85f, 0.62f, 0.35f, 1f),
        CollisionSurface.Vegetation => new(0.35f, 0.8f, 0.3f, 1f),
        CollisionSurface.Water => new(0.2f, 0.55f, 1f, 1f),
        CollisionSurface.Glass => new(0.55f, 0.9f, 0.95f, 1f),
        CollisionSurface.Building => new(0.95f, 0.45f, 0.75f, 1f),
        _ => Vector4.One,
    };

    public static string LayerName(CollisionSurface surface) => LayerPrefix + surface;

    public int LoadedFileCount => _loaded.Count;
    public int PendingFileCount => _pending.Count;
    public int TriangleCount => _loaded.Values.Sum(f => f.TriangleCount);

    public List<string> Errors { get; } = new();

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, float radius, bool enabled)
    {
        if (loader.Collisions != _catalog)
        {
            Clear();
            _catalog = loader.Collisions;
        }
        if (_catalog is null)
            return;

        var position = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (enabled)
            RequestFiles(position, radius);

        UploadArrivedFiles(renderer);
        ReleaseFarFiles(position, enabled ? radius + UnloadMargin : -1f);
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<CollisionSurface, bool> isVisible)
    {
        foreach (var file in _loaded.Values)
        {
            for (int i = 0; i < file.Surfaces.Length; i++)
            {
                if (file.Surfaces[i] is { } mesh && isVisible((CollisionSurface)i))
                    renderer.Draw(mesh, viewProjection, cameraPosition);
            }
        }
    }

    private void RequestFiles(Vector2 position, float radius)
    {
        foreach (var file in _catalog!.FilesAround(position, radius))
        {
            if (_pending.Count >= MaxPendingReads)
                break;
            if (_loaded.ContainsKey(file) || !_pending.Add(file))
                continue;
            _requests.Writer.TryWrite((file, _generation));
        }
    }

    private void UploadArrivedFiles(ColorRenderer renderer)
    {
        for (int uploaded = 0; uploaded < MaxMeshUploadsPerFrame && _results.TryDequeue(out var result);)
        {
            if (result.Generation != _generation)
                continue; // Result of a previous map.

            _pending.Remove(result.File);
            if (result.Error is not null)
            {
                Errors.Add($"{result.File.Name}.ybn : {result.Error}");
                _loaded[result.File] = FileMeshes.Empty; // Don't retry a damaged file every frame.
                continue;
            }

            var batches = result.Batches ?? [];
            _loaded[result.File] = new FileMeshes(batches.Select(renderer.CreateMesh).ToArray(), batches.Sum(b => b.TriangleCount));
            uploaded++;
        }
    }

    private void ReleaseFarFiles(Vector2 position, float keepRadius)
    {
        var far = _loaded.Keys.Where(f => keepRadius < 0f || DistanceTo(f, position) > keepRadius).ToList();
        foreach (var file in far)
        {
            _loaded[file].Dispose();
            _loaded.Remove(file);
        }
    }

    private static float DistanceTo(CollisionFile file, Vector2 position)
    {
        var closest = Vector2.Clamp(position, new Vector2(file.BoundsMin.X, file.BoundsMin.Y), new Vector2(file.BoundsMax.X, file.BoundsMax.Y));
        return Vector2.Distance(closest, position);
    }

    private static TriangleBatch[] BuildBatches(CollisionMesh mesh, CollisionMaterials materials)
    {
        var batches = Surfaces.Select(_ => new TriangleBatch()).ToArray();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.Vertices[mesh.Indices[t * 3]];
            var b = mesh.Vertices[mesh.Indices[t * 3 + 1]];
            var c = mesh.Vertices[mesh.Indices[t * 3 + 2]];
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.LengthSquared() < 1e-12f)
                continue; // degenerate
            var normal = Vector3.Normalize(cross);

            var surface = materials.Surface(mesh.Materials[t]);
            var baseColor = Color(surface);
            // Simple lighting so slopes, steps and walls are readable.
            float shade = 0.45f + 0.55f * MathF.Abs(Vector3.Dot(normal, LightDirection));
            var color = new Vector4(baseColor.X * shade, baseColor.Y * shade, baseColor.Z * shade, SurfaceAlpha);

            var lift = normal * SurfaceOffset;
            batches[(int)surface].AddTriangle(a + lift, b + lift, c + lift, color);
        }
        return batches;
    }

    private async Task ReadFilesInBackground()
    {
        try
        {
            await foreach (var (file, generation) in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                var catalog = _catalog;
                if (generation != _generation || catalog is null)
                    continue;
                try
                {
                    var mesh = catalog.Read(file);
                    _results.Enqueue((file, generation, mesh is null ? null : BuildBatches(mesh, catalog.Materials), null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) // Any error would stop the loop for good.
                {
                    _results.Enqueue((file, generation, null, ex.Message));
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
        foreach (var file in _loaded.Values)
            file.Dispose();
        _loaded.Clear();
        _pending.Clear();
        Errors.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _requests.Writer.TryComplete();
        Clear();
    }

    private sealed class FileMeshes(ColorMesh?[] surfaces, int triangleCount) : IDisposable
    {
        public static readonly FileMeshes Empty = new([], 0);

        public ColorMesh?[] Surfaces { get; } = surfaces;
        public int TriangleCount { get; } = triangleCount;

        public void Dispose()
        {
            foreach (var mesh in Surfaces) mesh?.Dispose();
        }
    }
}
