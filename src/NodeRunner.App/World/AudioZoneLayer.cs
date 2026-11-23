using System.Numerics;
using NodeRunner.Data.Audio;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// An ambient zone or a static emitter of the audio game data (read-only).
internal sealed record AudioSelection(AmbientZone? Zone, StaticEmitter? Emitter)
{
    public uint NameHash => Zone?.NameHash ?? Emitter!.NameHash;
    public string File => Zone?.File ?? Emitter!.File;
}

// The ambient zones (where the game plays birds, city noise, water...) and the static emitters (fixed sounds and radios)
// near the camera, seen through walls. A zone is drawn where its sounds are placed (its positioning shape) and, fainter,
// where the player must be for it to play (its activation shape) when that one is elsewhere.
internal sealed class AudioZoneLayer : IDisposable
{
    public const string ZonesLayerName = "AudioZones";
    public const string EmittersLayerName = "AudioEmitters";
    public const float ZoneRadius = 600f;
    public const float EmitterRadius = 300f;

    private const float RebuildDistance = 60f;
    private const float FilledZoneSize = 250f;
    private const float SurfaceAlpha = 0.1f;
    private const float HiddenOpacity = 0.35f;
    private const float HiddenLineOpacity = 0.4f;
    private const float MarkSize = 0.6f;
    private const float LineWidth = 1f;
    private const float MaxPickDistance = 2000f;
    private const float PickPixels = 10f;
    private const int Segments = 48;

    public static readonly Vector4 ZoneColor = new(0.3f, 0.85f, 0.75f, 0.95f);
    private static readonly Vector4 ActivationColor = ZoneColor with { W = 0.45f };
    public static readonly Vector4 EmitterColor = new(1f, 0.5f, 0.75f, 1f);
    private static readonly Vector4 SelectedColor = new(1f, 0.95f, 0.35f, 1f);
    private static readonly Vector4 OuterColor = new(1f, 0.95f, 0.35f, 0.4f);

    private ColorMesh? _lines;
    private ColorMesh? _surfaces;
    private AudioGameData? _data;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private (bool Zones, bool Emitters) _builtFor;
    private List<AmbientZone> _zones = [];
    private List<StaticEmitter> _emitters = [];

    public int ZoneCount => _zones.Count;
    public int EmitterCount => _emitters.Count;

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, bool zones, bool emitters)
    {
        if (!zones && !emitters)
            return;
        // Asking for the data starts reading it, the first time.
        var data = loader.AudioGameData;
        if (data != _data)
        {
            _data = data;
            _builtCenter = new Vector2(float.NaN);
            DisposeMeshes();
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (!finished.IsCompletedSuccessfully)
            {
                Diagnostics.ErrorLog.Add(L("Zones audio"), finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN);
            }
            else if (finished.Result.Data == _data)
            {
                DisposeMeshes();
                _lines = renderer.CreateMesh(finished.Result.Lines);
                _surfaces = renderer.CreateMesh(finished.Result.Surfaces);
                _zones = finished.Result.Zones;
                _emitters = finished.Result.Emitters;
            }
        }

        if (_data is null || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || _builtFor != (zones, emitters))
        {
            var source = _data;
            _builtCenter = center;
            _builtFor = (zones, emitters);
            _building = Task.Run(() => Build(source, center, zones, emitters));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (_surfaces is { } surfaces)
            renderer.DrawXRay(surfaces, viewProjection, cameraPosition, ZoneRadius * 2f, HiddenOpacity);
        if (_lines is { } lines)
            renderer.DrawXRay(lines, viewProjection, cameraPosition, ZoneRadius * 2f, HiddenLineOpacity);
    }

    // The selection outlined over everything. Zone: where its sounds are placed, and the wider box where it starts
    // playing. Emitter: the distance where it is heard fully (inner) and the farthest one (outer).
    public static void DrawSelected(ColorRenderer lines, AudioSelection selection, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (selection.Zone is { } zone)
        {
            AddZone(lines.AddLine, zone, SelectedColor);
            AddActivation(lines.AddLine, zone, OuterColor);
        }
        else if (selection.Emitter is { } emitter)
        {
            AddMark(lines.AddLine, emitter.Position, MarkSize * 2.5f, SelectedColor);
            if (emitter.MinDistance > 0.05f)
                AddSphere(lines.AddLine, emitter.Position, emitter.MinDistance, SelectedColor);
            if (emitter.MaxDistance > 0.05f)
                AddSphere(lines.AddLine, emitter.Position, emitter.MaxDistance, OuterColor);
        }
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    // What is under the mouse: an emitter within a few pixels first, else the nearest zone face hit (the far side of
    // a zone the camera is in).
    public AudioSelection? Pick(Matrix4x4 viewProjection, Vector2 point, Vector2 viewport, bool zones, bool emitters)
    {
        if (emitters)
        {
            StaticEmitter? best = null;
            float bestScore = PickPixels * PickPixels;
            foreach (var emitter in _emitters)
            {
                var clip = Vector4.Transform(new Vector4(emitter.Position, 1f), viewProjection);
                if (clip.W <= 0.1f)
                    continue;
                var screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * viewport.X, (0.5f - clip.Y / clip.W * 0.5f) * viewport.Y);
                float d = Vector2.DistanceSquared(screen, point);
                if (d < bestScore)
                {
                    bestScore = d;
                    best = emitter;
                }
            }
            if (best is not null)
                return new AudioSelection(null, best);
        }
        if (!zones || !Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var ndc = new Vector2(point.X / viewport.X * 2f - 1f, 1f - point.Y / viewport.Y * 2f);
        // Reversed Z: depth 1 = near plane.
        var origin = Unproject(ndc, 1f, inverse);
        var direction = Vector3.Normalize(Unproject(ndc, 0.1f, inverse) - origin);
        AmbientZone? nearest = null;
        float nearestDistance = MaxPickDistance;
        foreach (var zone in _zones)
        {
            if (Nearest(RayZone(origin, direction, zone), RayActivation(origin, direction, zone)) is { } d && d < nearestDistance)
            {
                nearestDistance = d;
                nearest = zone;
            }
        }
        return nearest is null ? null : new AudioSelection(nearest, null);
    }

    // Centre and size of the drawn shape, to frame it.
    public static (Vector3 Centre, float Size) Extent(AudioSelection selection)
    {
        if (selection.Emitter is { } emitter)
            return (emitter.Position, MathF.Max(4f, MathF.Min(emitter.MaxDistance, 60f)));
        var zone = selection.Zone!;
        var v = zone.Positioning;
        return zone.Shape switch
        {
            AudioZoneShape.Sphere => (v.Centre, MathF.Max(4f, v.Size.X * 2f)),
            AudioZoneShape.Line => ((v.Centre + v.Size) * 0.5f, MathF.Max(4f, Vector3.Distance(v.Centre, v.Size))),
            _ => (v.Centre, MathF.Max(4f, MathF.Max(v.Size.X, v.Size.Y))),
        };
    }

    private static Vector3 Unproject(Vector2 ndc, float depth, Matrix4x4 inverse)
    {
        var p = Vector4.Transform(new Vector4(ndc, depth, 1f), inverse);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    // Box turned around Z by the volume's angle (counterclockwise seen from above).
    private static Matrix4x4 BoxTransform(AudioZoneVolume volume) =>
        Matrix4x4.CreateRotationZ(volume.RotationDegrees * MathF.PI / 180f) * Matrix4x4.CreateTranslation(volume.Centre);

    // The line zone as a thin box from one end to the other.
    private static (Vector3 Min, Vector3 Max, Matrix4x4 Transform) LineBox(AudioZoneVolume volume)
    {
        var axis = volume.Size - volume.Centre;
        float length = axis.Length();
        if (length < 1e-4f)
            return (new Vector3(-LineWidth), new Vector3(LineWidth), Matrix4x4.CreateTranslation(volume.Centre));
        var x = axis / length;
        var helper = MathF.Abs(x.Z) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        var y = Vector3.Normalize(Vector3.Cross(helper, x));
        var z = Vector3.Cross(x, y);
        var transform = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0,
            volume.Centre.X, volume.Centre.Y, volume.Centre.Z, 1);
        return (new Vector3(0, -LineWidth, -LineWidth), new Vector3(length, LineWidth, LineWidth), transform);
    }

    private static float? RayZone(Vector3 origin, Vector3 direction, AmbientZone zone)
    {
        var v = zone.Positioning;
        if (zone.Shape == AudioZoneShape.Sphere)
            return RaySphere(origin, direction, v.Centre, v.Size.X);
        var (min, max, transform) = zone.Shape == AudioZoneShape.Line ? LineBox(v) : (-v.Size * 0.5f, v.Size * 0.5f, BoxTransform(v));
        if (!Matrix4x4.Invert(transform, out var inverse))
            return null;
        var localOrigin = Vector3.Transform(origin, inverse);
        var localDirection = Vector3.TransformNormal(direction, inverse);
        return RayBox(localOrigin, localDirection, min, max);
    }

    private static float? Nearest(float? a, float? b) => a is null ? b : b is null ? a : MathF.Min(a.Value, b.Value);

    private static float? RayActivation(Vector3 origin, Vector3 direction, AmbientZone zone)
    {
        if (!HasOwnActivation(zone))
            return null;
        var v = zone.Activation;
        if (zone.Shape == AudioZoneShape.Sphere)
            return RaySphere(origin, direction, v.Centre, v.Size.X);
        if (!Matrix4x4.Invert(BoxTransform(v), out var inverse))
            return null;
        return RayBox(Vector3.Transform(origin, inverse), Vector3.TransformNormal(direction, inverse), -v.Size * 0.5f, v.Size * 0.5f);
    }

    // An activation shape worth drawing: not empty, and not the positioning shape again.
    private static bool HasOwnActivation(AmbientZone zone)
    {
        var a = zone.Activation;
        if (a.Size.X < 0.01f || (zone.Shape != AudioZoneShape.Sphere && (a.Size.Y < 0.01f || a.Size.Z < 0.01f)))
            return false;
        var p = zone.Positioning;
        return zone.Shape == AudioZoneShape.Line || Vector3.Distance(a.Centre, p.Centre) > 0.5f
            || Vector3.Distance(a.Size, p.Size) > 0.5f || a.RotationDegrees != p.RotationDegrees;
    }

    private static void AddActivation(Action<Vector3, Vector3, Vector4> addLine, AmbientZone zone, Vector4 color)
    {
        if (!HasOwnActivation(zone))
            return;
        if (zone.Shape == AudioZoneShape.Sphere)
            AddSphere(addLine, zone.Activation.Centre, zone.Activation.Size.X, color);
        else
            AddBox(addLine, zone.Activation, color);
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

    private static float? RaySphere(Vector3 origin, Vector3 direction, Vector3 centre, float radius)
    {
        var offset = origin - centre;
        float b = Vector3.Dot(offset, direction);
        float c = offset.LengthSquared() - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0)
            return null;
        float root = MathF.Sqrt(discriminant);
        float near = -b - root, far = -b + root;
        if (far < 0)
            return null;
        return near >= 0 ? near : far;
    }

    private static void AddZone(Action<Vector3, Vector3, Vector4> addLine, AmbientZone zone, Vector4 color)
    {
        var v = zone.Positioning;
        switch (zone.Shape)
        {
            case AudioZoneShape.Sphere:
                AddSphere(addLine, v.Centre, v.Size.X, color);
                break;
            case AudioZoneShape.Line:
                var (min, max, transform) = LineBox(v);
                AddBox(addLine, min, max, transform, color);
                break;
            default:
                AddBox(addLine, v, color);
                break;
        }
    }

    private static void AddBox(Action<Vector3, Vector3, Vector4> addLine, AudioZoneVolume volume, Vector4 color) =>
        AddBox(addLine, -volume.Size * 0.5f, volume.Size * 0.5f, BoxTransform(volume), color);

    private static void AddBox(Action<Vector3, Vector3, Vector4> addLine, Vector3 min, Vector3 max, Matrix4x4 transform, Vector4 color)
    {
        var c = Corners(min, max, transform);
        for (int i = 0; i < 4; i++)
        {
            addLine(c[i], c[(i + 1) % 4], color);
            addLine(c[i + 4], c[(i + 1) % 4 + 4], color);
            addLine(c[i], c[i + 4], color);
        }
    }

    // Bottom corners counterclockwise, then the top ones.
    private static Vector3[] Corners(Vector3 min, Vector3 max, Matrix4x4 transform)
    {
        Vector3[] c =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        for (int i = 0; i < c.Length; i++)
            c[i] = Vector3.Transform(c[i], transform);
        return c;
    }

    // Three circles: horizontal and two vertical ones.
    private static void AddSphere(Action<Vector3, Vector3, Vector4> addLine, Vector3 centre, float radius, Vector4 color)
    {
        for (int i = 0; i < Segments; i++)
        {
            float a0 = MathF.Tau * i / Segments, a1 = MathF.Tau * (i + 1) / Segments;
            var (c0, s0, c1, s1) = (MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, MathF.Cos(a1) * radius, MathF.Sin(a1) * radius);
            addLine(centre + new Vector3(c0, s0, 0), centre + new Vector3(c1, s1, 0), color);
            addLine(centre + new Vector3(c0, 0, s0), centre + new Vector3(c1, 0, s1), color);
            addLine(centre + new Vector3(0, c0, s0), centre + new Vector3(0, c1, s1), color);
        }
    }

    // A small diamond.
    private static void AddMark(Action<Vector3, Vector3, Vector4> addLine, Vector3 p, float size, Vector4 color)
    {
        Vector3[] tips = [new(size, 0, 0), new(0, size, 0), new(-size, 0, 0), new(0, -size, 0)];
        for (int i = 0; i < 4; i++)
        {
            var a = p + tips[i];
            var b = p + tips[(i + 1) % 4];
            addLine(a, b, color);
            addLine(a, p + new Vector3(0, 0, size), color);
            addLine(a, p - new Vector3(0, 0, size), color);
        }
    }

    // Distance on the ground from the point to the zone's nearest shape, roughly (their bounding circles).
    private static float GroundDistance(AmbientZone zone, Vector2 point)
    {
        var v = zone.Positioning;
        (Vector3 centre, float radius) = zone.Shape switch
        {
            AudioZoneShape.Sphere => (v.Centre, v.Size.X),
            AudioZoneShape.Line => ((v.Centre + v.Size) * 0.5f, Vector3.Distance(v.Centre, v.Size) * 0.5f),
            _ => (v.Centre, new Vector2(v.Size.X, v.Size.Y).Length() * 0.5f),
        };
        float distance = Vector2.Distance(new Vector2(centre.X, centre.Y), point) - radius;
        if (!HasOwnActivation(zone))
            return distance;
        var a = zone.Activation;
        float activationRadius = zone.Shape == AudioZoneShape.Sphere ? a.Size.X : new Vector2(a.Size.X, a.Size.Y).Length() * 0.5f;
        return MathF.Min(distance, Vector2.Distance(new Vector2(a.Centre.X, a.Centre.Y), point) - activationRadius);
    }

    private sealed record BuildResult(AudioGameData Data, LineBatch Lines, TriangleBatch Surfaces, List<AmbientZone> Zones,
        List<StaticEmitter> Emitters);

    private static BuildResult Build(AudioGameData data, Vector2 center, bool showZones, bool showEmitters)
    {
        var lines = new LineBatch();
        var surfaces = new TriangleBatch();
        var zones = new List<AmbientZone>();
        var emitters = new List<StaticEmitter>();
        if (showZones)
        {
            var fill = ZoneColor with { W = SurfaceAlpha };
            int[][] faces = [[0, 1, 2, 3], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
            foreach (var zone in data.AmbientZones)
            {
                if (GroundDistance(zone, center) > ZoneRadius)
                    continue;
                zones.Add(zone);
                AddZone(lines.AddLine, zone, ZoneColor);
                AddActivation(lines.AddLine, zone, ActivationColor);
                // Only the small boxes are filled: the big ones (a whole district) would veil the view.
                var v = zone.Positioning;
                if (zone.Shape != AudioZoneShape.Box || MathF.Max(v.Size.X, v.Size.Y) > FilledZoneSize)
                    continue;
                var c = Corners(-v.Size * 0.5f, v.Size * 0.5f, BoxTransform(v));
                foreach (var f in faces)
                {
                    surfaces.AddTriangle(c[f[0]], c[f[1]], c[f[2]], fill);
                    surfaces.AddTriangle(c[f[0]], c[f[2]], c[f[3]], fill);
                }
            }
        }
        if (showEmitters)
        {
            foreach (var emitter in data.StaticEmitters)
            {
                if (Vector2.Distance(new Vector2(emitter.Position.X, emitter.Position.Y), center) > EmitterRadius)
                    continue;
                emitters.Add(emitter);
                AddMark(lines.AddLine, emitter.Position, MarkSize, EmitterColor);
            }
        }
        return new BuildResult(data, lines, surfaces, zones, emitters);
    }

    private void DisposeMeshes()
    {
        _lines?.Dispose();
        _lines = null;
        _surfaces?.Dispose();
        _surfaces = null;
        _zones = [];
        _emitters = [];
    }

    public void Dispose() => DisposeMeshes();
}
