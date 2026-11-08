using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using NodeRunner.Data.Collisions;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Streams the map collision (.ybn, in the chosen version: peds, vehicles or other) around the camera: files within the
// chosen radius are read and turned into triangles on a background thread (nearest first), sent to the GPU a few per
// frame, and released when the camera moves away. The collision of the interiors (MLO) placed there is shown too (in the
// peds version, the only one kept for interiors). Hand changes (CollisionEdits) are shown, and the triangles are kept
// for selecting polygons.
internal sealed class CollisionLayer : IDisposable
{
    public const float MinRadius = 50f;
    public const float MaxRadius = 600f;
    public const string LayerPrefix = "Collision.";

    private const int MaxPendingReads = 4;
    private const int MaxMeshUploadsPerFrame = 2;
    private const float UnloadMargin = 150f;
    private const int InteriorRefreshFrames = 30;

    // Lifted along the triangle normal so the surfaces do not flicker against the models they follow.
    private const float SurfaceOffset = 0.03f;
    private const float SurfaceAlpha = 0.5f;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.4f, 0.3f, 0.85f));

    public static readonly IReadOnlyList<CollisionSurface> Surfaces = Enum.GetValues<CollisionSurface>();

    private readonly Dictionary<CollisionFile, FileMeshes> _loaded = new();
    private readonly HashSet<CollisionFile> _pending = new();
    private readonly Channel<(CollisionFile File, int Generation, CollisionFileEdit? Edit)> _requests =
        Channel.CreateUnbounded<(CollisionFile, int, CollisionFileEdit?)>();
    private readonly ConcurrentQueue<(CollisionFile File, int Generation, CollisionMesh? Mesh, CollisionMesh? Shown, TriangleBatch[]? Batches, string? Error)> _results = new();
    private readonly CancellationTokenSource _stop = new();

    private CollisionCatalog? _catalog;
    private volatile int _generation;
    private CollisionKind _kind;
    private CollisionEdits? _edits;
    private int _editsRevision = -1;
    private List<CollisionFile> _interiors = [];
    private int _interiorFrames;

    public CollisionLayer()
    {
        Task.Run(ReadFilesInBackground);
    }

    public static string Label(CollisionSurface surface) => surface switch
    {
        CollisionSurface.Road => L("Routes, trottoirs, béton"),
        CollisionSurface.Terrain => L("Terrain (terre, sable, roche)"),
        CollisionSurface.Vegetation => L("Végétation (herbe, buissons)"),
        CollisionSurface.Water => L("Eau"),
        CollisionSurface.Glass => L("Verre"),
        CollisionSurface.Building => L("Bâtiments et objets"),
        _ => surface.ToString(),
    };

    public static string KindLabel(CollisionKind kind) => kind switch
    {
        CollisionKind.Vehicle => L("Véhicules (hi@)"),
        CollisionKind.Other => L("Autre (ma@)"),
        _ => L("Piétons et objets"),
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

    public CollisionKind Kind => _kind;

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, float radius, bool enabled,
        CollisionKind kind, CollisionEdits edits)
    {
        if (loader.Collisions != _catalog || kind != _kind)
        {
            Clear();
            _catalog = loader.Collisions;
            _kind = kind;
        }
        if (_catalog is null)
            return;
        _edits = edits;
        if (edits.Revision != _editsRevision)
        {
            _editsRevision = edits.Revision;
            ApplyEdits(renderer, edits);
        }

        var position = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (enabled)
        {
            if (_interiorFrames-- <= 0)
            {
                _interiorFrames = InteriorRefreshFrames;
                FindInteriors(loader, position, radius);
            }
            RequestFiles(position, radius);
        }

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

    // The interiors placed around, each with its collision file; one moved or deleted is dropped.
    private void FindInteriors(WorldLoader loader, Vector2 position, float radius)
    {
        var found = new List<CollisionFile>();
        if (_kind == CollisionKind.Standard && loader.Maps is { } library)
        {
            foreach (var interior in library.EntitiesAround(position, radius))
            {
                if (!interior.IsInterior || _catalog!.FindInteriorFile(interior.ArchetypeHash) is not { } path
                    || GroundSnap.WorldBox(interior, library.GetArchetype(interior.ArchetypeHash)) is not var (min, max))
                    continue;
                found.Add(new CollisionFile(path, Path.GetFileNameWithoutExtension(path), min, max, CollisionKind.Standard)
                {
                    Transform = interior.Transform,
                    IsInterior = true,
                });
            }
        }
        var kept = found.ToHashSet();
        foreach (var file in _loaded.Keys.Where(f => f.IsInterior && !kept.Contains(f)).ToList())
        {
            _loaded[file].Dispose();
            _loaded.Remove(file);
            _selectionShown = null;
        }
        _pending.RemoveWhere(f => f.IsInterior && !kept.Contains(f));
        _interiors = found;
    }

    private void RequestFiles(Vector2 position, float radius)
    {
        var interiors = _interiors.Where(f => DistanceTo(f, position) <= radius).OrderBy(f => DistanceTo(f, position));
        foreach (var file in _catalog!.FilesAround(position, radius, _kind).Concat(interiors))
        {
            if (_pending.Count >= MaxPendingReads)
                break;
            if (_loaded.ContainsKey(file) || !_pending.Add(file))
                continue;
            _requests.Writer.TryWrite((file, _generation, Snapshot(file.Path)));
        }
    }

    private void UploadArrivedFiles(ColorRenderer renderer)
    {
        for (int uploaded = 0; uploaded < MaxMeshUploadsPerFrame && _results.TryDequeue(out var result);)
        {
            if (result.Generation != _generation)
                continue; // Result of a previous map.

            if (!_pending.Remove(result.File))
                continue; // An interior moved or deleted meanwhile.
            if (result.Error is not null)
            {
                Errors.Add($"{result.File.Name}.ybn : {result.Error}");
                _loaded[result.File] = FileMeshes.Empty; // Don't retry a damaged file every frame.
                continue;
            }

            var batches = result.Batches ?? [];
            _loaded[result.File] = new FileMeshes(batches.Select(renderer.CreateMesh).ToArray(), batches.Sum(b => b.TriangleCount))
            {
                Mesh = result.Mesh,
                Shown = result.Shown,
                WasEdited = result.Shown != result.Mesh,
            };
            // Changed while it was being read: shown again with the current changes.
            if (result.Mesh is not null && (_edits?.For(result.File.Path) is not null || result.Shown != result.Mesh))
                Rebuild(renderer, result.File);
            _selectionShown = null;
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
            await foreach (var (file, generation, edit) in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                var catalog = _catalog;
                if (generation != _generation || catalog is null)
                    continue;
                try
                {
                    var mesh = catalog.Read(file);
                    if (mesh is not null && file.IsInterior)
                        mesh = Placed(mesh, file.Transform);
                    var shown = mesh is null ? null : edit?.Apply(mesh) ?? mesh;
                    _results.Enqueue((file, generation, mesh, shown, shown is null ? null : BuildBatches(shown, catalog.Materials), null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) // Any error would stop the loop for good.
                {
                    if (ex is not (InvalidDataException or IOException))
                        Diagnostics.ErrorLog.Add(L("Collisions"), ex);
                    _results.Enqueue((file, generation, null, null, null, ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Application closing.
        }
    }

    // The triangles in world space (polygon numbers and materials unchanged).
    private static CollisionMesh Placed(CollisionMesh mesh, Matrix4x4 transform)
    {
        var vertices = new Vector3[mesh.Vertices.Count];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = Vector3.Transform(mesh.Vertices[i], transform);
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }
        return new CollisionMesh
        {
            BoundsMin = vertices.Length > 0 ? min : mesh.BoundsMin,
            BoundsMax = vertices.Length > 0 ? max : mesh.BoundsMax,
            Vertices = vertices,
            Indices = mesh.Indices,
            Materials = mesh.Materials,
            Polygons = mesh.Polygons,
        };
    }

    // A copy, read by the background thread while the interface may change the original.
    private CollisionFileEdit? Snapshot(string path) => _edits?.For(path) is { } edit
        ? new CollisionFileEdit { Removed = [.. edit.Removed], Materials = new(edit.Materials) }
        : null;

    private void ApplyEdits(ColorRenderer renderer, CollisionEdits edits)
    {
        foreach (var (file, meshes) in _loaded.ToList())
        {
            if (meshes.Mesh is not null && (meshes.WasEdited || edits.For(file.Path) is not null))
                Rebuild(renderer, file);
        }
    }

    private void Rebuild(ColorRenderer renderer, CollisionFile file)
    {
        var meshes = _loaded[file];
        if (meshes.Mesh is null || _catalog is null)
            return;
        var edit = _edits?.For(file.Path);
        meshes.Shown = edit?.Apply(meshes.Mesh) ?? meshes.Mesh;
        meshes.WasEdited = edit is not null;
        var batches = BuildBatches(meshes.Shown, _catalog.Materials);
        foreach (var mesh in meshes.Surfaces) mesh?.Dispose();
        meshes.Surfaces = batches.Select(renderer.CreateMesh).ToArray();
        meshes.TriangleCount = batches.Sum(b => b.TriangleCount);
        _selectionShown = null; // its triangles may have changed
    }

    // Loaded collision as shown, for selecting polygons.
    public IEnumerable<(string Path, CollisionMesh Mesh)> ShownMeshes() =>
        _loaded.Where(f => f.Value.Shown is not null).Select(f => (f.Key.Path, f.Value.Shown!));

    public HashSet<CollisionPolygon> Pick(Matrix4x4 viewProjection, Vector2 viewport, Vector2 rectMin, Vector2 rectMax) =>
        CollisionPicker.Pick(ShownMeshes(), viewProjection, viewport, rectMin, rectMax);

    // Materials of these polygons as shown (files not loaded are skipped).
    public Dictionary<CollisionPolygon, byte> MaterialsOf(IReadOnlySet<CollisionPolygon> polygons)
    {
        var result = new Dictionary<CollisionPolygon, byte>();
        foreach (var (file, meshes) in _loaded)
        {
            if (meshes.Shown is not { } shown)
                continue;
            for (int t = 0; t < shown.TriangleCount; t++)
            {
                var key = new CollisionPolygon(file.Path, shown.Polygons[t]);
                if (polygons.Contains(key))
                    result.TryAdd(key, shown.Materials[t]);
            }
        }
        return result;
    }

    private static readonly Vector4 SelectionColor = new(1f, 0.62f, 0.1f, 0.55f);
    private static readonly Vector4 SelectionEdgeColor = new(1f, 0.85f, 0.3f, 1f);
    private object? _selectionShown;
    private int _selectionVersion = -1;
    private ColorMesh? _selectionSurface;
    private ColorMesh? _selectionEdges;

    // The selected polygons, orange with their edges, over the collision.
    public void DrawSelection(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition,
        IReadOnlySet<CollisionPolygon> selection, int selectionVersion)
    {
        if (_selectionShown is null || selectionVersion != _selectionVersion)
        {
            _selectionShown = new object();
            _selectionVersion = selectionVersion;
            _selectionSurface?.Dispose();
            _selectionEdges?.Dispose();
            _selectionSurface = _selectionEdges = null;
            if (selection.Count > 0)
            {
                var surface = new TriangleBatch();
                var edges = new LineBatch();
                foreach (var (file, meshes) in _loaded)
                {
                    if (meshes.Shown is not { } shown)
                        continue;
                    for (int t = 0; t < shown.TriangleCount; t++)
                    {
                        if (!selection.Contains(new CollisionPolygon(file.Path, shown.Polygons[t])))
                            continue;
                        var a = shown.Vertices[shown.Indices[t * 3]];
                        var b = shown.Vertices[shown.Indices[t * 3 + 1]];
                        var c = shown.Vertices[shown.Indices[t * 3 + 2]];
                        var cross = Vector3.Cross(b - a, c - a);
                        var lift = cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) * (SurfaceOffset * 2) : Vector3.Zero;
                        surface.AddTriangle(a + lift, b + lift, c + lift, SelectionColor);
                        edges.AddLine(a + lift, b + lift, SelectionEdgeColor);
                        edges.AddLine(b + lift, c + lift, SelectionEdgeColor);
                        edges.AddLine(c + lift, a + lift, SelectionEdgeColor);
                    }
                }
                _selectionSurface = renderer.CreateMesh(surface);
                _selectionEdges = renderer.CreateMesh(edges);
            }
        }
        if (_selectionSurface is not null)
            renderer.Draw(_selectionSurface, viewProjection, cameraPosition);
        if (_selectionEdges is not null)
            renderer.Draw(_selectionEdges, viewProjection, cameraPosition);
    }

    private void Clear()
    {
        _generation++;
        foreach (var file in _loaded.Values)
            file.Dispose();
        _loaded.Clear();
        _pending.Clear();
        _interiors = [];
        _interiorFrames = 0;
        Errors.Clear();
        _selectionShown = null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _requests.Writer.TryComplete();
        Clear();
        _selectionSurface?.Dispose();
        _selectionEdges?.Dispose();
    }

    private sealed class FileMeshes(ColorMesh?[] surfaces, int triangleCount) : IDisposable
    {
        public static FileMeshes Empty => new([], 0);

        public ColorMesh?[] Surfaces { get; set; } = surfaces;
        public int TriangleCount { get; set; } = triangleCount;

        // Triangles of the game's file, and as shown (with the hand changes).
        public CollisionMesh? Mesh { get; init; }
        public CollisionMesh? Shown { get; set; }
        public bool WasEdited { get; set; }

        public void Dispose()
        {
            foreach (var mesh in Surfaces) mesh?.Dispose();
        }
    }
}
