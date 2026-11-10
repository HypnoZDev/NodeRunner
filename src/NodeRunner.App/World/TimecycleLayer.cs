using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A timecycle zone of a map file.
internal sealed record TimecycleSelection(MapData Map, int Index)
{
    public TimecycleModifier? Zone => Index >= 0 && Index < Map.TimecycleModifiers.Count && !Map.TimecycleModifiers[Index].IsDeleted
        ? Map.TimecycleModifiers[Index] : null;
}

// The timecycle zones near the camera: translucent boxes, seen through walls (many are inside buildings and tunnels).
internal sealed class TimecycleLayer : IDisposable
{
    public const string LayerName = "Timecycle";
    public const float Radius = 500f;

    private const float RebuildDistance = 60f;
    private const float SurfaceAlpha = 0.12f;
    private const float HiddenOpacity = 0.35f;
    private const float HiddenLineOpacity = 0.4f;
    private const float MaxPickDistance = 1500f;

    public static readonly Vector4 Color = new(0.8f, 0.45f, 1f, 0.95f);
    private static readonly Vector4 SelectedColor = new(1f, 0.95f, 0.35f, 1f);
    private static readonly Vector4 RangeColor = new(1f, 0.95f, 0.35f, 0.45f);

    private ColorMesh? _lines;
    private ColorMesh? _surfaces;
    private MapLibrary? _library;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private int _builtRevision;
    private List<TimecycleSelection> _zones = [];

    public int Count => _zones.Count;

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, bool enabled)
    {
        if (loader.Maps != _library)
        {
            _library = loader.Maps;
            _builtCenter = new Vector2(float.NaN);
            DisposeMeshes();
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (!finished.IsCompletedSuccessfully)
            {
                Diagnostics.ErrorLog.Add(L("Zones de timecycle"), finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN);
            }
            else if (finished.Result.Library == _library)
            {
                DisposeMeshes();
                _lines = renderer.CreateMesh(finished.Result.Lines);
                _surfaces = renderer.CreateMesh(finished.Result.Surfaces);
                _zones = finished.Result.Zones;
            }
        }

        if (_library is null || !enabled || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || _builtRevision != MapData.TimecycleRevision)
        {
            var library = _library;
            _builtCenter = center;
            _builtRevision = MapData.TimecycleRevision;
            _building = Task.Run(() => Build(library, center));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (_surfaces is { } surfaces)
            renderer.DrawXRay(surfaces, viewProjection, cameraPosition, Radius * 2f, HiddenOpacity);
        if (_lines is { } lines)
            renderer.DrawXRay(lines, viewProjection, cameraPosition, Radius * 2f, HiddenLineOpacity);
    }

    // The selected zone outlined over everything, with the box where it fades in (its range) around it.
    public static void DrawSelected(ColorRenderer lines, TimecycleModifier zone, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        AddBox(lines.AddLine, zone.BoundsMin, zone.BoundsMax, SelectedColor);
        if (zone.Range > 0.01f)
            AddBox(lines.AddLine, zone.BoundsMin - new Vector3(zone.Range), zone.BoundsMax + new Vector3(zone.Range), RangeColor);
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    // The zone under the mouse (point in pixels inside a viewport of the given size), or null: the nearest face hit,
    // the far side of a zone the camera is in (clicking a wall of the zone around you selects it).
    public TimecycleSelection? Pick(Matrix4x4 viewProjection, Vector2 point, Vector2 viewport)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var ndc = new Vector2(point.X / viewport.X * 2f - 1f, 1f - point.Y / viewport.Y * 2f);
        // Reversed Z: depth 1 = near plane.
        var origin = Unproject(ndc, 1f, inverse);
        var direction = Vector3.Normalize(Unproject(ndc, 0.1f, inverse) - origin);

        TimecycleSelection? best = null;
        float bestDistance = MaxPickDistance;
        foreach (var selection in _zones)
        {
            if (selection.Zone is not { } zone || RayBox(origin, direction, zone.BoundsMin, zone.BoundsMax) is not { } d || d >= bestDistance)
                continue;
            bestDistance = d;
            best = selection;
        }
        return best;
    }

    private static Vector3 Unproject(Vector2 ndc, float depth, Matrix4x4 inverse)
    {
        var p = Vector4.Transform(new Vector4(ndc, depth, 1f), inverse);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    // Distance along the ray to the first face of the box hit (the exit face from inside), or null.
    private static float? RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
    {
        float near = float.MinValue, far = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            if (MathF.Abs(direction[i]) < 1e-9f)
            {
                if (origin[i] < min[i] || origin[i] > max[i])
                    return null;
                continue;
            }
            float t1 = (min[i] - origin[i]) / direction[i];
            float t2 = (max[i] - origin[i]) / direction[i];
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far)
                return null;
        }
        if (far < 0)
            return null;
        return near >= 0 ? near : far;
    }

    private static void AddBox(Action<Vector3, Vector3, Vector4> addLine, Vector3 min, Vector3 max, Vector4 color)
    {
        var c = Corners(min, max);
        for (int i = 0; i < 4; i++)
        {
            addLine(c[i], c[(i + 1) % 4], color);
            addLine(c[i + 4], c[(i + 1) % 4 + 4], color);
            addLine(c[i], c[i + 4], color);
        }
    }

    // Bottom corners counterclockwise, then the top ones.
    private static Vector3[] Corners(Vector3 min, Vector3 max) =>
    [
        new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
        new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
    ];

    private sealed record BuildResult(MapLibrary Library, LineBatch Lines, TriangleBatch Surfaces, List<TimecycleSelection> Zones);

    private static BuildResult Build(MapLibrary library, Vector2 center)
    {
        var lines = new LineBatch();
        var surfaces = new TriangleBatch();
        var zones = new List<TimecycleSelection>();
        var fill = Color with { W = SurfaceAlpha };
        foreach (var map in library.Maps)
        {
            var list = map.TimecycleModifiers;
            for (int i = 0; i < list.Count; i++)
            {
                var zone = list[i];
                if (zone.IsDeleted)
                    continue;
                // Distance from the camera to the nearest point of the box, on the ground plane.
                var nearest = Vector2.Clamp(center, new Vector2(zone.BoundsMin.X, zone.BoundsMin.Y), new Vector2(zone.BoundsMax.X, zone.BoundsMax.Y));
                if (Vector2.DistanceSquared(nearest, center) > Radius * Radius)
                    continue;
                zones.Add(new TimecycleSelection(map, i));
                AddBox(lines.AddLine, zone.BoundsMin, zone.BoundsMax, Color);
                var c = Corners(zone.BoundsMin, zone.BoundsMax);
                int[][] faces = [[0, 1, 2, 3], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
                foreach (var f in faces)
                {
                    surfaces.AddTriangle(c[f[0]], c[f[1]], c[f[2]], fill);
                    surfaces.AddTriangle(c[f[0]], c[f[2]], c[f[3]], fill);
                }
            }
        }
        return new BuildResult(library, lines, surfaces, zones);
    }

    private void DisposeMeshes()
    {
        _lines?.Dispose();
        _lines = null;
        _surfaces?.Dispose();
        _surfaces = null;
        _zones = [];
    }

    public void Dispose() => DisposeMeshes();
}
