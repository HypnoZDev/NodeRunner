using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Classic;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// The collision of GTA III / Vice City / San Andreas around the camera: each detailed object placed within the radius
// gets the collision of its model (from the ".col" files, read once), built into coloured triangles on a background
// thread, again once the camera has moved far enough.
internal sealed class ClassicCollisionLayer : IDisposable
{
    private const float RebuildDistance = 40f;
    private const float SurfaceOffset = 0.03f;
    private const float SurfaceAlpha = 0.5f;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.4f, 0.3f, 0.85f));

    // Collisions read for the current map (a new one for another map: a build still running keeps the old one).
    private sealed class Cache
    {
        public Dictionary<string, CollisionMesh?> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReadFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private Cache _cache = new();
    private ColorMesh?[] _meshes = [];
    private ClassicScene? _scene;
    private Vector2 _builtAt = new(float.NaN);
    private float _builtRadius;
    private Task<TriangleBatch[]>? _building;

    public int TriangleCount { get; private set; }

    public void Update(ClassicScene? scene, ClassicFileSystem files, ClassicGame game, ColorRenderer renderer, Vector3 cameraPosition,
        float radius, bool enabled)
    {
        if (scene != _scene)
        {
            Clear();
            _scene = scene;
            _cache = new Cache();
        }
        if (_building is { IsCompleted: true } done)
        {
            _building = null;
            if (done.IsCompletedSuccessfully && enabled)
            {
                Clear();
                _meshes = [.. done.Result.Select(b => b.TriangleCount > 0 ? renderer.CreateMesh(b) : null)];
                TriangleCount = done.Result.Sum(b => b.TriangleCount);
            }
        }
        if (!enabled)
        {
            Clear();
            _builtAt = new Vector2(float.NaN);
            return;
        }
        if (scene is null || _building is not null)
            return;
        var position = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (!float.IsNaN(_builtAt.X) && Vector2.Distance(position, _builtAt) < RebuildDistance && radius == _builtRadius)
            return;
        _builtAt = position;
        _builtRadius = radius;
        var cache = _cache;
        _building = Task.Run(() => Build(scene, files, game, cache, position, radius));
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<CollisionSurface, bool> isVisible)
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            if (_meshes[i] is { } mesh && isVisible((CollisionSurface)i))
                renderer.Draw(mesh, viewProjection, cameraPosition);
        }
    }

    private static TriangleBatch[] Build(ClassicScene scene, ClassicFileSystem files, ClassicGame game, Cache cache, Vector2 position, float radius)
    {
        var batches = CollisionLayer.Surfaces.Select(_ => new TriangleBatch()).ToArray();
        foreach (var entity in scene.Library.EntitiesAround(position, radius))
        {
            if (entity.LodLevel is not (LodLevel.Hd or LodLevel.OrphanHd) || entity.IsDeleted)
                continue;
            if (Collision(scene, files, cache, scene.Names.Describe(entity.ArchetypeHash)) is not { } mesh)
                continue;
            var transform = entity.Transform;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var a = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3]], transform);
                var b = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + 1]], transform);
                var c = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + 2]], transform);
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-12f)
                    continue;
                var normal = Vector3.Normalize(cross);
                var surface = ClassicCollisions.Surface(game, mesh.Materials[t]);
                var color = CollisionLayer.Color(surface);
                float shade = 0.45f + 0.55f * MathF.Abs(Vector3.Dot(normal, LightDirection));
                var lift = normal * SurfaceOffset;
                batches[(int)surface].AddTriangle(a + lift, b + lift, c + lift,
                    new Vector4(color.X * shade, color.Y * shade, color.Z * shade, SurfaceAlpha));
            }
        }
        return batches;
    }

    // A model's collision; its whole file is read the first time one of its models is needed.
    private static CollisionMesh? Collision(ClassicScene scene, ClassicFileSystem files, Cache cache, string model)
    {
        var models = cache.Models;
        lock (models)
        {
            if (models.TryGetValue(model, out var known))
                return known;
            if (!scene.CollisionFiles.TryGetValue(model, out var path) || !cache.ReadFiles.Add(path))
            {
                models[model] = null;
                return null;
            }
            try
            {
                if (files.ReadFile(path) is { } data)
                {
                    foreach (var (name, mesh) in ClassicCollisions.ReadMeshes(data))
                        models[name] = mesh;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Diagnostics.ErrorLog.Add(L("Collisions"), $"{path} : {ex.Message}");
            }
            if (!models.TryGetValue(model, out var found))
                models[model] = found = null;
            return found;
        }
    }

    private void Clear()
    {
        foreach (var mesh in _meshes)
            mesh?.Dispose();
        _meshes = [];
        TriangleCount = 0;
    }

    public void Dispose() => Clear();
}
