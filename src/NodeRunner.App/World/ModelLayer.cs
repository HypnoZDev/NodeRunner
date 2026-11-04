using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Draws the real 3D models of the map, following the game's level-of-detail tree: detailed objects up close,
// simplified district models (LOD, SLOD) far away, so the whole map is visible.
internal sealed class ModelLayer : IDisposable
{
    public const string LayerName = "Models";
    public const float MinDistanceMultiplier = 0.25f;
    public const float MaxDistanceMultiplier = 4f;

    private const float RebuildDistance = 15f;
    private const int MaxPendingReads = 24;
    private const int UploadTriangleBudgetPerFrame = 300_000;
    private const long UploadTextureBytesBudgetPerFrame = 48L << 20;
    private const double UnusedModelLifetime = 15.0;
    private const int RecentFileCacheSize = 16;

    private readonly Dictionary<ModelLocation, GpuModel?> _models = new();
    private readonly Dictionary<ModelLocation, double> _lastUsed = new();
    private readonly Dictionary<TextureKey, SharedTexture> _textures = new();
    private readonly Dictionary<ModelLocation, TextureKey[]> _modelTextures = new();
    private readonly HashSet<ModelLocation> _pending = new();
    private readonly Channel<(ModelLocation Location, int Generation)> _requests = Channel.CreateUnbounded<(ModelLocation, int)>();
    private readonly ConcurrentQueue<LoadedModel> _results = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile int _generation;
    private volatile IModelReader? _reader;
    private volatile TextureResolver? _resolver;
    private MapLibrary? _library;
    private ModelCatalog? _catalog;
    private List<VisibleEntity> _visible = new();
    private Task<List<VisibleEntity>>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private float _builtMultiplier;
    private int _builtStructure = -1; // MapLibrary.StructureVersion: objects deleted or brought back since.
    private float _builtAltitude;
    private int _builtHour = -1;
    private double _lastEviction;

    public ModelLayer()
    {
        Task.Run(ReadModelsInBackground);
    }

    // An object to draw: where (computed for Revision of the entity), which model, its bounding sphere in the world
    // (for culling), and the lower-detail object to show while its model is not loaded yet.
    private sealed record VisibleEntity(
        MapEntity Entity, int Revision, Matrix4x4 Transform, ModelLocation Location, Vector3 SphereCenter, float SphereRadius)
    {
        public VisibleEntity? Fallback { get; init; }

        public bool IsCurrent => Revision == Entity.Revision && (Fallback?.IsCurrent ?? true);
    }

    // An object drawn in the last frame (kept to find the one under the mouse).
    private sealed record DrawnEntity(MapEntity Entity, GpuModel Model, Matrix4x4 Transform, Vector3 Tint);

    private static readonly Vector3 SelectedTint = new(1.7f, 1.35f, 0.6f);

    private readonly List<DrawnEntity> _drawn = new();

    // A model read by the background thread (null if the file has none), with the texture of each mesh.
    private sealed record LoadedModel(
        ModelLocation Location, int Generation, ModelData? Model, ResolvedTexture?[][] Textures, List<string> Errors);

    // A texture on the GPU (null if its format is not supported) and the number of loaded models using it.
    private sealed class SharedTexture(GpuTexture? texture)
    {
        public GpuTexture? Texture { get; } = texture;
        public int Users { get; set; }
    }

    public int NearbyObjectCount => _visible.Count;
    public int DrawnObjectCount { get; private set; }
    public long DrawnTriangleCount { get; private set; }
    public int LoadedModelCount => _models.Count(m => m.Value is not null);
    public int LoadedTextureCount => _textures.Count;
    public int PendingModelCount => _pending.Count;
    public long GpuBytes => _models.Values.Sum(m => m?.ByteSize ?? 0) + _textures.Values.Sum(t => t.Texture?.ByteSize ?? 0);

    public List<string> Errors { get; } = new();

    public void Update(WorldLoader loader, ModelRenderer renderer, Vector3 cameraPosition, float distanceMultiplier, int hour, bool enabled)
    {
        if (loader.Maps != _library || loader.Models != _catalog)
        {
            Clear();
            _library = loader.Maps;
            _catalog = loader.Models;
            _reader = loader.ModelReader;
            _resolver = loader.Textures is { } textures && loader.TextureReader is { } textureReader
                ? new TextureResolver(textures, textureReader)
                : null;
        }
        if (_library is null || _catalog is null)
            return;

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            _visible = finished.Result;
        }

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        bool moved = float.IsNaN(_builtCenter.X)
            || Vector2.Distance(center, _builtCenter) > RebuildDistance
            || MathF.Abs(cameraPosition.Z - _builtAltitude) > RebuildDistance;
        if (enabled && _building is null
            && (moved || MathF.Abs(distanceMultiplier - _builtMultiplier) > 0.01f || _library.StructureVersion != _builtStructure
                || hour != _builtHour))
        {
            var (library, catalog, position) = (_library, _catalog, cameraPosition);
            _builtStructure = library.StructureVersion;
            _builtCenter = center;
            _builtAltitude = cameraPosition.Z;
            _builtMultiplier = distanceMultiplier;
            _builtHour = hour;
            _building = Task.Run(() => FindVisible(library, catalog, position, distanceMultiplier, hour));
        }

        if (enabled)
            RequestModels();
        UploadArrivedModels(renderer);
        EvictUnusedModels(enabled);
    }

    // Share of the models around the camera already on the GPU (0 to 1), while no frame is drawn yet (start-up
    // screen): they are marked used, so they are not released before the first frame.
    public float PreloadProgress()
    {
        if (_library is null || _building is not null || float.IsNaN(_builtCenter.X))
            return 0f;
        if (_visible.Count == 0)
            return 1f;
        double now = _clock.Elapsed.TotalSeconds;
        int loaded = 0;
        foreach (var entity in _visible)
        {
            if (!_models.ContainsKey(entity.Location))
                continue;
            loaded++;
            _lastUsed[entity.Location] = now;
        }
        return (float)loaded / _visible.Count;
    }

    public void Draw(ModelRenderer renderer, Matrix4x4 viewProjection, IReadOnlyList<MapEntity> selected, Vector3 camera, float maxDistance,
        Action? beforeTranslucent = null)
    {
        var frustum = new Frustum(viewProjection);
        double now = _clock.Elapsed.TotalSeconds;
        long triangles = 0;
        var fallbacksDrawn = new HashSet<MapEntity>();
        _drawn.Clear();

        renderer.Begin(ModelPass.Solid);
        for (int i = 0; i < _visible.Count; i++)
        {
            if (!_visible[i].IsCurrent)
                _visible[i] = Refresh(_visible[i]);
            var entity = _visible[i];
            if (entity.Entity.IsDeleted)
                continue; // Deleted since the list was made (it is being made again).
            if (!_models.TryGetValue(entity.Location, out var model))
            {
                // Not loaded yet: show the lower-detail parent instead (once), to avoid holes.
                if (entity.Fallback is not { } fallback || fallback.Entity.IsDeleted || !fallbacksDrawn.Add(fallback.Entity)
                    || !_models.TryGetValue(fallback.Location, out model))
                    continue;
                entity = fallback;
            }

            _lastUsed[entity.Location] = now;
            if (model is null || !frustum.Intersects(entity.SphereCenter, entity.SphereRadius)
                || Vector3.Distance(camera, entity.SphereCenter) - entity.SphereRadius > maxDistance)
                continue;

            var tint = selected.Count > 0 && selected.Contains(entity.Entity) ? SelectedTint : Vector3.One;
            renderer.Draw(model, entity.Transform, viewProjection, tint);
            _drawn.Add(new DrawnEntity(entity.Entity, model, entity.Transform, tint));
            triangles += model.TriangleCount;
        }

        beforeTranslucent?.Invoke();

        // Decals and glass over the solid meshes of every object.
        renderer.Begin(ModelPass.Translucent);
        foreach (var drawn in _drawn)
            renderer.Draw(drawn.Model, drawn.Transform, viewProjection, drawn.Tint);

        DrawnObjectCount = _drawn.Count;
        DrawnTriangleCount = triangles;
    }

    // Draws the objects around the camera into the sun's shadow images, also those off screen (their shadow may fall
    // in view).
    public void DrawShadows(ModelRenderer renderer, ShadowMaps shadows)
    {
        renderer.BeginShadow();
        var fallbacksDrawn = new HashSet<MapEntity>();
        for (int cascade = 0; cascade < ShadowMaps.CascadeCount; cascade++)
        {
            shadows.BeginCascade(cascade);
            var lightViewProjection = shadows.Cascades[cascade];
            var frustum = new Frustum(lightViewProjection);
            float minRadius = shadows.TexelSizes[cascade] * 2f;
            fallbacksDrawn.Clear();
            for (int i = 0; i < _visible.Count; i++)
            {
                if (!_visible[i].IsCurrent)
                    _visible[i] = Refresh(_visible[i]);
                var entity = _visible[i];
                if (entity.Entity.IsDeleted)
                    continue;
                if (!_models.TryGetValue(entity.Location, out var model))
                {
                    // Not loaded yet: the parent shown instead casts the shadow.
                    if (entity.Fallback is not { } fallback || fallback.Entity.IsDeleted || !fallbacksDrawn.Add(fallback.Entity)
                        || !_models.TryGetValue(fallback.Location, out model))
                        continue;
                    entity = fallback;
                }
                if (model is null || entity.SphereRadius < minRadius || !frustum.Intersects(entity.SphereCenter, entity.SphereRadius))
                    continue;
                renderer.DrawShadow(model, entity.Transform, lightViewProjection);
            }
        }
    }

    public void DrawWater(WaterRenderer water)
    {
        foreach (var drawn in _drawn)
        {
            if (drawn.Model.HasWaterParts)
                water.DrawMesh(drawn.Model, drawn.Transform);
        }
    }

    private VisibleEntity Refresh(VisibleEntity visible)
    {
        if (_library is null || _catalog is null)
            return visible;
        var fallback = visible.Fallback is { IsCurrent: false } old ? Describe(_library, _catalog, old.Entity) : visible.Fallback;
        var placed = visible.Revision != visible.Entity.Revision ? Describe(_library, _catalog, visible.Entity) : visible;
        return (placed ?? visible) with { Fallback = fallback };
    }

    // The object seen at pixel (x, y) of the last frame drawn, null if none: the objects are drawn again into buffer,
    // each with its number (back buffer to rebind afterwards).
    public MapEntity? Pick(ModelRenderer renderer, PickBuffer buffer, Matrix4x4 viewProjection, int width, int height, int x, int y)
    {
        buffer.Begin(width, height);
        renderer.BeginPick();
        for (int i = 0; i < _drawn.Count; i++)
            renderer.DrawPick(_drawn[i].Model, _drawn[i].Transform, viewProjection, (uint)(i + 1));

        uint number = buffer.Read(x, y);
        return number >= 1 && number <= _drawn.Count ? _drawn[(int)number - 1].Entity : null;
    }

    // The 3D point of the drawn models at pixel (x, y) of the view, null if none is there (sky).
    public Vector3? PickPoint(ModelRenderer renderer, PickBuffer buffer, Matrix4x4 viewProjection, int width, int height, int x, int y)
    {
        if (Pick(renderer, buffer, viewProjection, width, height, x, y) is null)
            return null;
        float depth = buffer.ReadDepth(x, y);
        if (depth <= 0f || !Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var clip = new Vector4((x + 0.5f) / width * 2f - 1f, 1f - (y + 0.5f) / height * 2f, depth, 1f);
        var world = Vector4.Transform(clip, inverse);
        return Math.Abs(world.W) < 1e-12f ? null : new Vector3(world.X, world.Y, world.Z) / world.W;
    }

    private static List<VisibleEntity> FindVisible(MapLibrary library, ModelCatalog catalog, Vector3 camera, float multiplier, int hour)
    {
        var parents = new Dictionary<MapEntity, VisibleEntity?>();
        var result = new List<(VisibleEntity Entity, float Distance)>();
        foreach (var entity in LodSelector.Select(library, camera, multiplier, hour))
        {
            if (Describe(library, catalog, entity) is not { } visible)
                continue;
            if (entity.Parent is { } parent)
            {
                if (!parents.TryGetValue(parent, out var fallback))
                    parents[parent] = fallback = Describe(library, catalog, parent);
                visible = visible with { Fallback = fallback };
            }
            result.Add((visible, Vector3.Distance(camera, visible.SphereCenter)));
        }
        return result.OrderBy(r => r.Distance).Select(r => r.Entity).ToList();
    }

    private static VisibleEntity? Describe(MapLibrary library, ModelCatalog catalog, MapEntity entity)
    {
        var archetype = library.GetArchetype(entity.ArchetypeHash);
        if (archetype is null || catalog.Locate(archetype) is not { } location)
            return null;

        // Revision read first: if the editor moves the object meanwhile (this may run on a background thread),
        // the transform is computed again at the next frame drawn.
        int revision = entity.Revision;
        var transform = entity.Transform;
        var sphereCenter = Vector3.Transform(archetype.SphereCenter, transform);
        float sphereRadius = archetype.SphereRadius * MathF.Max(entity.Scale.X, entity.Scale.Z);
        return new VisibleEntity(entity, revision, transform, location, sphereCenter, sphereRadius);
    }

    private void RequestModels()
    {
        foreach (var entity in _visible)
        {
            if (_pending.Count >= MaxPendingReads)
                break;
            if (_models.ContainsKey(entity.Location) || !_pending.Add(entity.Location))
                continue;
            _requests.Writer.TryWrite((entity.Location, _generation));
        }
    }

    private void UploadArrivedModels(ModelRenderer renderer)
    {
        int triangleBudget = UploadTriangleBudgetPerFrame;
        long textureBudget = UploadTextureBytesBudgetPerFrame;
        while (triangleBudget > 0 && textureBudget > 0 && _results.TryDequeue(out var result))
        {
            if (result.Generation != _generation)
                continue;
            _pending.Remove(result.Location);
            Errors.AddRange(result.Errors);

            // A null entry means "no model": remembered so it is not requested again.
            GpuModel? model = null;
            if (result.Model is not null)
            {
                var textures = new GpuTexture?[result.Textures.Length][];
                var keys = new List<TextureKey>();
                for (int i = 0; i < textures.Length; i++)
                {
                    textures[i] = new GpuTexture?[result.Textures[i].Length];
                    for (int t = 0; t < textures[i].Length; t++)
                    {
                        if (result.Textures[i][t] is not { } resolved)
                            continue;
                        if (!_textures.TryGetValue(resolved.Key, out var shared))
                        {
                            _textures[resolved.Key] = shared = new SharedTexture(renderer.UploadTexture(resolved.Data));
                            textureBudget -= resolved.Data.Data.Length;
                        }
                        textures[i][t] = shared.Texture;
                        if (!keys.Contains(resolved.Key))
                        {
                            shared.Users++;
                            keys.Add(resolved.Key);
                        }
                    }
                }
                _modelTextures[result.Location] = keys.ToArray();
                model = renderer.Upload(result.Model, textures);
            }
            _models[result.Location] = model;
            _lastUsed[result.Location] = _clock.Elapsed.TotalSeconds;
            triangleBudget -= result.Model?.TriangleCount ?? 0;
        }
    }

    private void EvictUnusedModels(bool enabled)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastEviction < 1.0)
            return;
        _lastEviction = now;

        var unused = _lastUsed
            .Where(kv => !enabled || now - kv.Value > UnusedModelLifetime)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var location in unused)
            ReleaseModel(location);
        if (unused.Count > 0)
            _drawn.Clear(); // May refer to released models: rebuilt at the next frame drawn.
    }

    private void ReleaseModel(ModelLocation location)
    {
        if (_models.Remove(location, out var model))
            model?.Dispose();
        _lastUsed.Remove(location);
        if (!_modelTextures.Remove(location, out var keys))
            return;
        foreach (var key in keys)
        {
            if (_textures.TryGetValue(key, out var shared) && --shared.Users <= 0)
            {
                shared.Texture?.Dispose();
                _textures.Remove(key);
            }
        }
    }

    private async Task ReadModelsInBackground()
    {
        // .ydd files hold many models: keep the last few files read to avoid reading them again.
        var recentFiles = new Dictionary<string, IReadOnlyDictionary<uint, ModelData>>();
        var recentOrder = new Queue<string>();
        try
        {
            await foreach (var (location, generation) in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                var (reader, resolver) = (_reader, _resolver);
                if (generation != _generation || reader is null)
                    continue;
                var errors = new List<string>();
                try
                {
                    if (!recentFiles.TryGetValue(location.Path, out var models))
                    {
                        models = reader.ReadModels(location.Path);
                        recentFiles[location.Path] = models;
                        recentOrder.Enqueue(location.Path);
                        if (recentOrder.Count > RecentFileCacheSize)
                            recentFiles.Remove(recentOrder.Dequeue());
                    }
                    var model = models.GetValueOrDefault(location.ModelHash);
                    var textures = model is not null && resolver is not null
                        ? resolver.Resolve(location, model, errors)
                        : [];
                    _results.Enqueue(new LoadedModel(location, generation, model, textures, errors));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Any error, not only the expected ones: an exception escaping this loop would stop it for good,
                    // and no detailed model would load anymore (the map would stay on its LOD models).
                    errors.Add($"{Path.GetFileName(location.Path)} : {ex.Message}");
                    if (ex is not (InvalidDataException or IOException))
                        Diagnostics.ErrorLog.Add($"Modèle {Path.GetFileName(location.Path)}", ex);
                    _results.Enqueue(new LoadedModel(location, generation, null, [], errors));
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
        _drawn.Clear();
        foreach (var model in _models.Values)
            model?.Dispose();
        foreach (var shared in _textures.Values)
            shared.Texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _modelTextures.Clear();
        _lastUsed.Clear();
        _pending.Clear();
        _visible = new List<VisibleEntity>();
        _builtCenter = new Vector2(float.NaN);
        Errors.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _requests.Writer.TryComplete();
        Clear();
    }
}
