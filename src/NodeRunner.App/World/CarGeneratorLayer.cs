using System.Collections.Concurrent;
using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A parking spot of a map file.
internal sealed record CarGeneratorSelection(MapData Map, int Index)
{
    public CarGenerator? Spot => Index >= 0 && Index < Map.CarGenerators.Count ? Map.CarGenerators[Index] : null;
}

// The parking spots near the camera: a translucent box the size of the spot, with an arrow towards the front of the car.
internal sealed class CarGeneratorLayer : IDisposable
{
    public const string LayerName = "CarGenerators";
    public const float Radius = 500f;

    // Height of the box drawn over a spot. The game's positions are above the ground (measured against the road nodes:
    // median 0.94 m, up to 4 m), the car is put on the ground below: the box stands on the collision under the spot,
    // or Below the position without collision there.
    public const float Height = 1.5f;
    private const float Below = 1f;
    private const float ProbeAbove = 0.5f;  // Ground looked for from a little above the position...
    private const float ProbeDepth = 15f;   // ...down to this far below it.

    // Bottom of the box of each spot (by position), found in the collision.
    private readonly ConcurrentDictionary<Vector3, float> _grounds = new();
    private readonly CollisionQuery _buildQuery = new();  // Used by one build at a time.
    private readonly CollisionQuery _mainQuery = new();   // Used on the main thread (the selected spot).
    private CollisionCatalog? _collisions;

    private Vector3[] BaseCorners(CarGenerator spot)
    {
        float drop = spot.Position.Z - Bottom(spot);
        return [.. spot.Corners().Select(c => c - new Vector3(0, 0, drop))];
    }

    private float Bottom(CarGenerator spot) => _grounds.TryGetValue(spot.Position, out float z) ? z : spot.Position.Z - Below;

    // The ground under the spot, looked up and remembered (null: collisions not loaded yet).
    private float? FindBottom(WorldLoader loader, CollisionQuery query, CarGenerator spot)
    {
        if (_grounds.TryGetValue(spot.Position, out float known))
            return known;
        if (loader.Collisions is null)
            return null;
        var p = spot.Position;
        float from = p.Z + ProbeAbove;
        var collisions = query.CollisionIn(loader, p with { Z = p.Z - ProbeDepth }, p with { Z = from });
        float bottom = GroundSnap.HighestBelow(collisions, new Vector2(p.X, p.Y), from) is { } z && z > p.Z - ProbeDepth ? z : p.Z - Below;
        _grounds[p] = bottom;
        return bottom;
    }

    private const float RebuildDistance = 60f;
    private const float SurfaceAlpha = 0.25f;
    private const float HiddenOpacity = 0.35f;
    private const float HiddenLineOpacity = 0.3f;
    private const float MaxPickDistance = 1500f;

    public static readonly Vector4 Color = new(0.3f, 0.85f, 1f, 0.95f);

    private ColorMesh? _lines;
    private ColorMesh? _surfaces;
    private MapLibrary? _library;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private int _builtRevision;
    private List<CarGeneratorSelection> _spots = [];

    public int Count => _spots.Count;

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, bool enabled)
    {
        if (loader.Maps != _library)
        {
            _library = loader.Maps;
            _builtCenter = new Vector2(float.NaN);
            DisposeMeshes();
        }
        if (loader.Collisions != _collisions && _building is null)
        {
            // Collisions loaded (after the maps) or changed: the boxes are put on the ground again.
            _collisions = loader.Collisions;
            _grounds.Clear();
            _builtCenter = new Vector2(float.NaN);
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (!finished.IsCompletedSuccessfully)
            {
                Diagnostics.ErrorLog.Add(L("Véhicules garés"), finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN);
            }
            else if (finished.Result.Library == _library)
            {
                Apply(finished.Result, renderer);
            }
        }

        if (_library is null || !enabled || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || _builtRevision != MapData.CarGeneratorRevision)
        {
            var library = _library;
            _builtCenter = center;
            _builtRevision = MapData.CarGeneratorRevision;
            _building = Task.Run(() => Build(library, loader, center));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        // Seen through walls: spots are often in garages and under bridges.
        if (_surfaces is { } surfaces)
            renderer.DrawXRay(surfaces, viewProjection, cameraPosition, Radius * 1.2f, HiddenOpacity);
        if (_lines is { } lines)
            renderer.DrawXRay(lines, viewProjection, cameraPosition, Radius * 1.2f, HiddenLineOpacity);
    }

    // The spot under the mouse (point in pixels inside a viewport of the given size), or null.
    public CarGeneratorSelection? Pick(Matrix4x4 viewProjection, Vector2 point, Vector2 viewport)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var ndc = new Vector2(point.X / viewport.X * 2f - 1f, 1f - point.Y / viewport.Y * 2f);
        // Reversed Z: depth 1 = near plane.
        var origin = Unproject(ndc, 1f, inverse);
        var direction = Vector3.Normalize(Unproject(ndc, 0.1f, inverse) - origin);

        CarGeneratorSelection? best = null;
        float bestDistance = MaxPickDistance;
        foreach (var selection in _spots)
        {
            if (selection.Spot is not { } spot)
                continue;
            if (RayBox(origin, direction, spot) is { } d && d < bestDistance)
            {
                bestDistance = d;
                best = selection;
            }
        }
        return best;
    }

    // The selected spot, outlined over everything (its ground looked up again when it was moved).
    public void DrawSelected(ColorRenderer lines, WorldLoader loader, CarGenerator spot, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        FindBottom(loader, _mainQuery, spot);
        AddOutline(lines.AddLine, spot, new Vector4(1f, 0.95f, 0.35f, 1f));
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    // Box of the spot and arrow on its top, towards the front.
    private void AddOutline(Action<Vector3, Vector3, Vector4> addLine, CarGenerator spot, Vector4 color)
    {
        var c = BaseCorners(spot);
        var up = new Vector3(0, 0, Height);
        for (int i = 0; i < 4; i++)
        {
            var a = c[i];
            var b = c[(i + 1) % 4];
            addLine(a, b, color);
            addLine(a + up, b + up, color);
            addLine(a, a + up, color);
        }
        var forward = spot.Length > 1e-4f ? Vector2.Normalize(spot.Orientation) : Vector2.UnitX;
        var f = new Vector3(forward, 0);
        var side = new Vector3(forward.Y, -forward.X, 0);
        float half = spot.Length / 2;
        var top = spot.Position with { Z = Bottom(spot) } + up;
        var tip = top + f * (half * 0.9f);
        float head = MathF.Min(spot.Width, spot.Length) * 0.3f;
        addLine(top - f * (half * 0.6f), tip, color);
        addLine(tip, tip - f * head + side * head, color);
        addLine(tip, tip - f * head - side * head, color);
    }

    private static Vector3 Unproject(Vector2 ndc, float depth, Matrix4x4 inverse)
    {
        var p = Vector4.Transform(new Vector4(ndc, depth, 1f), inverse);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    // Distance along the ray to the box of the spot, or null (slabs, in the spot's own axes).
    private float? RayBox(Vector3 origin, Vector3 direction, CarGenerator spot)
    {
        var forward = spot.Length > 1e-4f ? Vector2.Normalize(spot.Orientation) : Vector2.UnitX;
        var axes = new[] { new Vector3(forward, 0), new Vector3(forward.Y, -forward.X, 0), Vector3.UnitZ };
        var center = spot.Position with { Z = Bottom(spot) + Height / 2 };
        var half = new Vector3(spot.Length / 2, spot.Width / 2, Height / 2);
        var delta = center - origin;
        float near = 0f, far = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float e = Vector3.Dot(axes[i], delta);
            float f = Vector3.Dot(axes[i], direction);
            if (MathF.Abs(f) < 1e-9f)
            {
                if (MathF.Abs(e) > half[i])
                    return null;
                continue;
            }
            float t1 = (e - half[i]) / f;
            float t2 = (e + half[i]) / f;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far)
                return null;
        }
        return near;
    }

    private sealed record BuildResult(MapLibrary Library, LineBatch Lines, TriangleBatch Surfaces, List<CarGeneratorSelection> Spots);

    private BuildResult Build(MapLibrary library, WorldLoader loader, Vector2 center)
    {
        var lines = new LineBatch();
        var surfaces = new TriangleBatch();
        var spots = new List<CarGeneratorSelection>();
        var fill = Color with { W = SurfaceAlpha };
        var up = new Vector3(0, 0, Height);
        foreach (var map in library.Maps)
        {
            var list = map.CarGenerators;
            for (int i = 0; i < list.Count; i++)
            {
                var spot = list[i];
                float reach = Radius + spot.Length;
                if (Vector2.DistanceSquared(new Vector2(spot.Position.X, spot.Position.Y), center) > reach * reach)
                    continue;
                spots.Add(new CarGeneratorSelection(map, i));
                FindBottom(loader, _buildQuery, spot);
                AddOutline(lines.AddLine, spot, Color);
                var c = BaseCorners(spot);
                surfaces.AddTriangle(c[0], c[1], c[2], fill);
                surfaces.AddTriangle(c[0], c[2], c[3], fill);
                surfaces.AddTriangle(c[0] + up, c[1] + up, c[2] + up, fill);
                surfaces.AddTriangle(c[0] + up, c[2] + up, c[3] + up, fill);
                for (int k = 0; k < 4; k++)
                {
                    var a = c[k];
                    var b = c[(k + 1) % 4];
                    surfaces.AddTriangle(a, b, b + up, fill);
                    surfaces.AddTriangle(a, b + up, a + up, fill);
                }
            }
        }
        return new BuildResult(library, lines, surfaces, spots);
    }

    private void Apply(BuildResult result, ColorRenderer renderer)
    {
        DisposeMeshes();
        _lines = renderer.CreateMesh(result.Lines);
        _surfaces = renderer.CreateMesh(result.Surfaces);
        _spots = result.Spots;
    }

    private void DisposeMeshes()
    {
        _lines?.Dispose();
        _lines = null;
        _surfaces?.Dispose();
        _surfaces = null;
        _spots = [];
    }

    public void Dispose() => DisposeMeshes();
}
