using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// A light of a distlodlights file (Distant), with its details in the lodlights file that is its child (Lod), if any.
internal sealed record LodLightSelection(MapData Distant, int Index, MapData? Lod)
{
    public DistantLodLight? Light => Index >= 0 && Index < Distant.DistantLights.Count ? Distant.DistantLights[Index] : null;

    public LodLight? Details => Lod is { } lod && Index < lod.LodLights.Count ? lod.LodLights[Index] : null;
}

// The lights the game shows from far away at night (street lamps, windows...) near the camera: a small star in the
// light's own colour, with a line towards where a spot light points.
internal sealed class LodLightLayer : IDisposable
{
    public const string LayerName = "LodLights";
    public const float Radius = 400f;

    private const float RebuildDistance = 50f;
    private const float StarSize = 0.45f;
    private const float SpotLine = 2.5f;
    private const float HiddenLineOpacity = 0.35f;
    private const float PickPixels = 10f;

    public static readonly Vector4 Color = new(1f, 0.78f, 0.3f, 1f);

    private ColorMesh? _lines;
    private MapLibrary? _library;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private int _builtRevision = -1;
    private List<(LodLightSelection Selection, Vector3 Position)> _lights = [];
    private Index? _index;

    public int Count => _lights.Count;

    public void Update(WorldLoader loader, ColorRenderer renderer, Vector3 cameraPosition, bool enabled)
    {
        if (loader.Maps != _library)
        {
            _library = loader.Maps;
            _index = null;
            _builtCenter = new Vector2(float.NaN);
            DisposeMeshes();
        }

        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            if (!finished.IsCompletedSuccessfully)
            {
                Diagnostics.ErrorLog.Add(L("Lumières LOD"), finished.Exception!.GetBaseException());
                _builtCenter = new Vector2(float.NaN);
            }
            else if (finished.Result.Library == _library)
            {
                DisposeMeshes();
                _lines = renderer.CreateMesh(finished.Result.Lines);
                _lights = finished.Result.Lights;
                _index = finished.Result.Index;
            }
        }

        if (_library is null || !enabled || _building is not null)
            return;

        var center = new Vector2(cameraPosition.X, cameraPosition.Y);
        bool changed = _builtRevision != MapData.LodLightRevision;
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance || changed)
        {
            var library = _library;
            // A light moved: the boxes of the files are computed again.
            var index = changed && _builtRevision >= 0 ? null : _index;
            _builtCenter = center;
            _builtRevision = MapData.LodLightRevision;
            _building = Task.Run(() => Build(library, index, center));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        // Seen through walls: many are on facades and in the trees.
        if (_lines is { } lines)
            renderer.DrawXRay(lines, viewProjection, cameraPosition, Radius * 1.2f, HiddenLineOpacity);
    }

    // The light closest to the mouse on screen (within a few pixels), or null.
    public LodLightSelection? Pick(Matrix4x4 viewProjection, Vector2 mouse, Vector2 viewSize)
    {
        LodLightSelection? best = null;
        float bestScore = PickPixels * PickPixels;
        foreach (var (selection, p) in _lights)
        {
            var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
            if (clip.W <= 0.1f)
                continue;
            var screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * viewSize.X, (0.5f - clip.Y / clip.W * 0.5f) * viewSize.Y);
            float d = Vector2.DistanceSquared(screen, mouse);
            if (d < bestScore)
            {
                bestScore = d;
                best = selection;
            }
        }
        return best;
    }

    // The selected light over everything: a bigger star, its direction, and a circle as wide as its reach (falloff).
    public static void DrawSelected(ColorRenderer lines, LodLightSelection selection, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (selection.Light is not { } light)
            return;
        var highlight = new Vector4(1f, 0.95f, 0.35f, 1f);
        var p = light.Position;
        AddStar(lines.AddLine, p, StarSize * 2.5f, highlight);
        if (selection.Details is { } details)
        {
            if (details.Type == LodLightType.Spot && details.Direction.LengthSquared() > 1e-6f)
                lines.AddLine(p, p + Vector3.Normalize(details.Direction) * MathF.Max(SpotLine * 2, details.Falloff), highlight);
            if (details.Falloff > 0.05f)
            {
                const int segments = 48;
                var reach = LightColor(light) with { W = 0.8f };
                for (int i = 0; i < segments; i++)
                {
                    float a0 = MathF.Tau * i / segments, a1 = MathF.Tau * (i + 1) / segments;
                    lines.AddLine(p + new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0) * details.Falloff,
                        p + new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0) * details.Falloff, reach);
                }
            }
        }
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    // The light's colour at full brightness (its intensity is shown apart).
    public static Vector4 LightColor(DistantLodLight light)
    {
        var c = new Vector3(light.Red, light.Green, light.Blue) / 255f;
        float max = MathF.Max(MathF.Max(c.X, c.Y), c.Z);
        return max > 0.01f ? new Vector4(c / max, 1f) : Color;
    }

    private static void AddStar(Action<Vector3, Vector3, Vector4> addLine, Vector3 p, float size, Vector4 color)
    {
        addLine(p - new Vector3(size, 0, 0), p + new Vector3(size, 0, 0), color);
        addLine(p - new Vector3(0, size, 0), p + new Vector3(0, size, 0), color);
        addLine(p - new Vector3(0, 0, size), p + new Vector3(0, 0, size), color);
    }

    // Each distlodlights file: the lodlights file that holds the details of its lights (its child), and the box
    // around its lights (to skip the far files whole). Made once per map loading.
    private sealed record Index(Dictionary<MapData, MapData> Children, Dictionary<MapData, (Vector2 Min, Vector2 Max)> Boxes);

    private sealed record BuildResult(MapLibrary Library, LineBatch Lines, List<(LodLightSelection, Vector3)> Lights, Index Index);

    private static BuildResult Build(MapLibrary library, Index? index, Vector2 center)
    {
        index ??= MakeIndex(library);
        var children = index.Children;
        var lines = new LineBatch();
        var lights = new List<(LodLightSelection, Vector3)>();
        foreach (var distant in library.Maps)
        {
            var list = distant.DistantLights;
            if (list.Count == 0)
                continue;
            if (index.Boxes.TryGetValue(distant, out var box)
                && (center.X < box.Min.X - Radius || center.X > box.Max.X + Radius || center.Y < box.Min.Y - Radius || center.Y > box.Max.Y + Radius))
                continue;
            children.TryGetValue(distant, out var lod);
            for (int i = 0; i < list.Count; i++)
            {
                var light = list[i];
                var p = light.Position;
                if (Vector2.DistanceSquared(new Vector2(p.X, p.Y), center) > Radius * Radius)
                    continue;
                var selection = new LodLightSelection(distant, i, lod);
                lights.Add((selection, p));
                var color = LightColor(light);
                AddStar(lines.AddLine, p, StarSize, color);
                if (selection.Details is { Type: LodLightType.Spot } details && details.Direction.LengthSquared() > 1e-6f)
                    lines.AddLine(p, p + Vector3.Normalize(details.Direction) * SpotLine, color with { W = 0.7f });
            }
        }
        return new BuildResult(library, lines, lights, index);
    }

    private static Index MakeIndex(MapLibrary library)
    {
        var distant = new Dictionary<uint, MapData>();
        var boxes = new Dictionary<MapData, (Vector2, Vector2)>();
        foreach (var map in library.Maps)
        {
            if (map.DistantLights.Count == 0)
                continue;
            distant.TryAdd(map.NameHash, map);
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var light in map.DistantLights)
            {
                var p = new Vector2(light.Position.X, light.Position.Y);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            boxes[map] = (min, max);
        }
        var children = new Dictionary<MapData, MapData>();
        foreach (var map in library.Maps)
        {
            if (map.LodLights.Count > 0 && distant.TryGetValue(map.ParentHash, out var parent))
                children.TryAdd(parent, map);
        }
        return new Index(children, boxes);
    }

    private void DisposeMeshes()
    {
        _lines?.Dispose();
        _lines = null;
        _lights = [];
    }

    public void Dispose() => DisposeMeshes();
}
