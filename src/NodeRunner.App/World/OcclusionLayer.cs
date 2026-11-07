using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// What the "Occlusion" layer shows.
internal enum OcclusionLayerPart
{
    Boxes,
    Models,
}

// A box or a mesh of an occluder map file.
internal sealed record OccluderSelection(OccluderMapFile File, OcclusionLayerPart Kind, int Index)
{
    public OccluderBox? Box => Kind == OcclusionLayerPart.Boxes ? File.Occluders.Boxes[Index] : null;

    public OccluderModel? Model => Kind == OcclusionLayerPart.Models ? File.Occluders.Models[Index] : null;

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        IEnumerable<Vector3> points = Box is { } box ? box.Corners() : Model!.Vertices;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    public Vector3 Center => Box?.Center ?? (Bounds().Min + Bounds().Max) / 2;
}

// The occluders near the camera: boxes and meshes of the game's occluder map files, as translucent surfaces with
// their outlines, seen through the buildings they sit in.
internal sealed class OcclusionLayer : IDisposable
{
    public const string LayerPrefix = "Occlusion.";
    public const float Radius = 500f;

    private const float RebuildDistance = 60f;
    private const float SurfaceAlpha = 0.22f;
    private const float HiddenOpacity = 0.45f;
    private const float HiddenLineOpacity = 0.3f;
    private const float MaxPickDistance = 1500f;

    // About 35 degrees: the flat sides of a round tower are not outlined.
    private const float SharpCosine = 0.82f;

    public static readonly IReadOnlyList<OcclusionLayerPart> Parts = Enum.GetValues<OcclusionLayerPart>();

    private readonly ColorMesh?[] _lines = new ColorMesh?[Parts.Count];
    private readonly ColorMesh?[] _surfaces = new ColorMesh?[Parts.Count];
    private MapLibrary? _library;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);
    private List<(OccluderMapFile File, OccluderBox Box)> _boxes = [];
    private List<(OccluderMapFile File, OccluderModel Model)> _models = [];
    private OccluderSelection? _outlined;
    private List<(Vector3 From, Vector3 To)> _outline = [];

    public static string Label(OcclusionLayerPart part) => part switch
    {
        OcclusionLayerPart.Boxes => "Boîtes d'occlusion",
        OcclusionLayerPart.Models => "Maillages d'occlusion",
        _ => part.ToString(),
    };

    public static Vector4 Color(OcclusionLayerPart part) => part switch
    {
        OcclusionLayerPart.Boxes => new(1f, 0.72f, 0.2f, 0.9f),
        OcclusionLayerPart.Models => new(0.95f, 0.4f, 0.7f, 0.9f),
        _ => Vector4.One,
    };

    public static string LayerName(OcclusionLayerPart part) => LayerPrefix + part;

    public int Count(OcclusionLayerPart part) => part == OcclusionLayerPart.Boxes ? _boxes.Count : _models.Count;

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
                Diagnostics.ErrorLog.Add("Occlusion", finished.Exception!.GetBaseException());
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
        if (float.IsNaN(_builtCenter.X) || Vector2.Distance(center, _builtCenter) > RebuildDistance)
        {
            var library = _library;
            _builtCenter = center;
            _building = Task.Run(() => Build(library, OccluderMaps.Get(loader), center));
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<OcclusionLayerPart, bool> isVisible)
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            if (!isVisible((OcclusionLayerPart)i))
                continue;
            // Occluders sit inside the walls they stand for: shown through the scene, paler there.
            if (_surfaces[i] is { } surface)
                renderer.DrawXRay(surface, viewProjection, cameraPosition, Radius * 1.2f, HiddenOpacity);
            if (_lines[i] is { } lines)
                renderer.DrawXRay(lines, viewProjection, cameraPosition, Radius * 1.2f, HiddenLineOpacity);
        }
    }

    // The occluder under the mouse (point in pixels inside a viewport of the given size), or null. A box wins over the
    // mesh around it: boxes often sit inside a block's mesh.
    public OccluderSelection? Pick(Matrix4x4 viewProjection, Vector2 point, Vector2 viewport, Func<OcclusionLayerPart, bool> isVisible)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var ndc = new Vector2(point.X / viewport.X * 2f - 1f, 1f - point.Y / viewport.Y * 2f);
        // Reversed Z: depth 1 = near plane.
        var origin = Unproject(ndc, 1f, inverse);
        var direction = Vector3.Normalize(Unproject(ndc, 0.1f, inverse) - origin);

        if (isVisible(OcclusionLayerPart.Boxes))
        {
            OccluderSelection? best = null;
            float bestDistance = MaxPickDistance;
            foreach (var (file, box) in _boxes)
            {
                var triangles = box.Triangles();
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    if (RayTriangle(origin, direction, triangles[t], triangles[t + 1], triangles[t + 2]) is { } d && d < bestDistance)
                    {
                        bestDistance = d;
                        best = new OccluderSelection(file, OcclusionLayerPart.Boxes, box.Index);
                    }
                }
            }
            if (best is not null)
                return best;
        }

        if (isVisible(OcclusionLayerPart.Models))
        {
            OccluderSelection? best = null;
            float bestDistance = MaxPickDistance;
            foreach (var (file, model) in _models)
            {
                var v = model.Vertices;
                var indices = model.Indices;
                for (int t = 0; t + 2 < indices.Count; t += 3)
                {
                    if (RayTriangle(origin, direction, v[indices[t]], v[indices[t + 1]], v[indices[t + 2]]) is { } d && d < bestDistance)
                    {
                        bestDistance = d;
                        best = new OccluderSelection(file, OcclusionLayerPart.Models, model.Index);
                    }
                }
            }
            return best;
        }
        return null;
    }

    // The selected occluder, outlined over everything.
    public void DrawSelected(ColorRenderer lines, OccluderSelection selection, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        var color = new Vector4(1f, 0.95f, 0.35f, 1f);
        if (selection.Box is { } box)
        {
            var transform = Matrix4x4.CreateRotationZ(box.Angle) * Matrix4x4.CreateTranslation(box.Center);
            lines.AddBox(-box.Size / 2, box.Size / 2, transform, color);
        }
        else if (selection.Model is { } model)
        {
            if (!Equals(_outlined, selection))
            {
                _outlined = selection;
                _outline = FeatureEdges(model.Vertices, model.Indices).ToList();
            }
            foreach (var (from, to) in _outline)
                lines.AddLine(from, to, color);
        }
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    private static Vector3 Unproject(Vector2 ndc, float depth, Matrix4x4 inverse)
    {
        var p = Vector4.Transform(new Vector4(ndc, depth, 1f), inverse);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    // Distance along the ray to the triangle (both faces), or null (Moller-Trumbore).
    private static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return null;
        float inv = 1f / det;
        var s = origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f)
            return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0f || u + v > 1f)
            return null;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 0f ? t : null;
    }

    private sealed record BuildResult(MapLibrary Library, LineBatch[] Lines, TriangleBatch[] Surfaces,
        List<(OccluderMapFile, OccluderBox)> Boxes, List<(OccluderMapFile, OccluderModel)> Models);

    private static BuildResult Build(MapLibrary library, IReadOnlyList<OccluderMapFile> files, Vector2 center)
    {
        var lines = Parts.Select(_ => new LineBatch()).ToArray();
        var surfaces = Parts.Select(_ => new TriangleBatch()).ToArray();
        var boxes = new List<(OccluderMapFile, OccluderBox)>();
        var models = new List<(OccluderMapFile, OccluderModel)>();

        var boxColor = Color(OcclusionLayerPart.Boxes);
        var boxFill = boxColor with { W = SurfaceAlpha };
        var modelColor = Color(OcclusionLayerPart.Models);
        var modelFill = modelColor with { W = SurfaceAlpha };
        var boxLines = lines[(int)OcclusionLayerPart.Boxes];
        var boxSurfaces = surfaces[(int)OcclusionLayerPart.Boxes];
        var modelLines = lines[(int)OcclusionLayerPart.Models];
        var modelSurfaces = surfaces[(int)OcclusionLayerPart.Models];

        foreach (var file in files)
        {
            foreach (var box in file.Occluders.Boxes)
            {
                float reach = Radius + 0.5f * MathF.Max(box.Size.X, box.Size.Y);
                if (Vector2.DistanceSquared(new Vector2(box.Center.X, box.Center.Y), center) > reach * reach)
                    continue;
                boxes.Add((file, box));
                var transform = Matrix4x4.CreateRotationZ(box.Angle) * Matrix4x4.CreateTranslation(box.Center);
                boxLines.AddBox(-box.Size / 2, box.Size / 2, transform, boxColor);
                var triangles = box.Triangles();
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                    boxSurfaces.AddTriangle(triangles[t], triangles[t + 1], triangles[t + 2], boxFill);
            }

            foreach (var model in file.Occluders.Models)
            {
                if (!Near(model.Vertices, center))
                    continue;
                models.Add((file, model));
                var v = model.Vertices;
                var indices = model.Indices;
                for (int t = 0; t + 2 < indices.Count; t += 3)
                    modelSurfaces.AddTriangle(v[indices[t]], v[indices[t + 1]], v[indices[t + 2]], modelFill);
                foreach (var (from, to) in FeatureEdges(v, indices))
                    modelLines.AddLine(from, to, modelColor);
            }
        }

        return new BuildResult(library, lines, surfaces, boxes, models);
    }

    // Edges worth an outline: on the border of the mesh, or between faces at an angle (corners of walls, roof edges).
    // Edges inside a flat surface are left out, as are duplicates.
    private static IEnumerable<(Vector3 From, Vector3 To)> FeatureEdges(IReadOnlyList<Vector3> vertices, IReadOnlyList<byte> indices)
    {
        var edges = new Dictionary<(Vector3, Vector3), (Vector3 Normal, int Faces, bool Sharp)>();
        Span<Vector3> p = stackalloc Vector3[3];
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            p[0] = Snap(vertices[indices[t]]);
            p[1] = Snap(vertices[indices[t + 1]]);
            p[2] = Snap(vertices[indices[t + 2]]);
            var normal = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
            if (normal.LengthSquared() < 1e-8f)
                continue;
            normal = Vector3.Normalize(normal);
            for (int k = 0; k < 3; k++)
            {
                var key = Key(p[k], p[(k + 1) % 3]);
                edges[key] = edges.TryGetValue(key, out var edge)
                    ? (edge.Normal, edge.Faces + 1, edge.Sharp || MathF.Abs(Vector3.Dot(edge.Normal, normal)) < SharpCosine)
                    : (normal, 1, false);
            }
        }
        var result = new List<(Vector3, Vector3)>();
        foreach (var (key, edge) in edges)
        {
            if (edge.Faces == 1 || edge.Sharp)
                result.Add(key);
        }
        return result;
    }

    private static Vector3 Snap(Vector3 v) => new(MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2));

    private static (Vector3, Vector3) Key(Vector3 a, Vector3 b) =>
        (a.X, a.Y, a.Z).CompareTo((b.X, b.Y, b.Z)) <= 0 ? (a, b) : (b, a);

    // Whether the mesh's footprint reaches the circle around the camera.
    private static bool Near(IReadOnlyList<Vector3> vertices, Vector2 center)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var v in vertices)
        {
            min = Vector2.Min(min, new Vector2(v.X, v.Y));
            max = Vector2.Max(max, new Vector2(v.X, v.Y));
        }
        var closest = Vector2.Clamp(center, min, max);
        return Vector2.DistanceSquared(closest, center) <= Radius * Radius;
    }

    private void Apply(BuildResult result, ColorRenderer renderer)
    {
        DisposeMeshes();
        for (int i = 0; i < Parts.Count; i++)
        {
            _lines[i] = renderer.CreateMesh(result.Lines[i]);
            _surfaces[i] = renderer.CreateMesh(result.Surfaces[i]);
        }
        _boxes = result.Boxes;
        _models = result.Models;
    }

    private void DisposeMeshes()
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            _lines[i]?.Dispose();
            _lines[i] = null;
            _surfaces[i]?.Dispose();
            _surfaces[i] = null;
        }
        _boxes = [];
        _models = [];
    }

    public void Dispose() => DisposeMeshes();
}
