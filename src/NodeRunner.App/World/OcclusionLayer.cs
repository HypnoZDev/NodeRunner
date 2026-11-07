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

// The occluders near the camera: boxes and meshes of the game's occluder map files, as translucent surfaces with
// their outlines.
internal sealed class OcclusionLayer : IDisposable
{
    public const string LayerPrefix = "Occlusion.";
    public const float Radius = 500f;

    private const float RebuildDistance = 60f;
    private const float SurfaceAlpha = 0.22f;
    private const float HiddenOpacity = 0.45f;

    public static readonly IReadOnlyList<OcclusionLayerPart> Parts = Enum.GetValues<OcclusionLayerPart>();

    private readonly ColorMesh?[] _lines = new ColorMesh?[Parts.Count];
    private readonly ColorMesh?[] _surfaces = new ColorMesh?[Parts.Count];
    private MapLibrary? _library;
    private Task<BuildResult>? _building;
    private Vector2 _builtCenter = new(float.NaN);

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

    public int BoxCount { get; private set; }

    public int ModelCount { get; private set; }

    public int Count(OcclusionLayerPart part) => part == OcclusionLayerPart.Boxes ? BoxCount : ModelCount;

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
                renderer.DrawXRay(lines, viewProjection, cameraPosition, Radius * 1.2f, HiddenOpacity);
        }
    }

    private sealed record BuildResult(MapLibrary Library, LineBatch[] Lines, TriangleBatch[] Surfaces, int BoxCount, int ModelCount);

    private static BuildResult Build(MapLibrary library, IReadOnlyList<OccluderMapFile> files, Vector2 center)
    {
        var lines = Parts.Select(_ => new LineBatch()).ToArray();
        var surfaces = Parts.Select(_ => new TriangleBatch()).ToArray();
        int boxCount = 0, modelCount = 0;

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
                boxCount++;
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
                modelCount++;
                var v = model.Vertices;
                var indices = model.Indices;
                for (int t = 0; t + 2 < indices.Count; t += 3)
                    modelSurfaces.AddTriangle(v[indices[t]], v[indices[t + 1]], v[indices[t + 2]], modelFill);
                foreach (var (from, to) in FeatureEdges(v, indices))
                    modelLines.AddLine(from, to, modelColor);
            }
        }

        return new BuildResult(library, lines, surfaces, boxCount, modelCount);
    }

    // Edges worth an outline: on the border of the mesh, or between faces at an angle (corners of walls, roof edges).
    // Edges inside a flat surface are left out, as are duplicates.
    private static IEnumerable<(Vector3 From, Vector3 To)> FeatureEdges(IReadOnlyList<Vector3> vertices, IReadOnlyList<byte> indices)
    {
        var edges = new Dictionary<(Vector3, Vector3), (Vector3 Normal, int Faces, bool Sharp)>();
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            var p = new[] { Snap(vertices[indices[t]]), Snap(vertices[indices[t + 1]]), Snap(vertices[indices[t + 2]]) };
            var normal = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
            if (normal.LengthSquared() < 1e-8f)
                continue;
            normal = Vector3.Normalize(normal);
            for (int k = 0; k < 3; k++)
            {
                var key = Key(p[k], p[(k + 1) % 3]);
                if (!edges.TryGetValue(key, out var edge))
                    edges[key] = (normal, 1, false);
                else
                    edges[key] = (edge.Normal, edge.Faces + 1, edge.Sharp || MathF.Abs(Vector3.Dot(edge.Normal, normal)) < SharpCosine);
            }
        }
        foreach (var (key, edge) in edges)
        {
            if (edge.Faces == 1 || edge.Sharp)
                yield return key;
        }
    }

    // About 20 degrees.
    private const float SharpCosine = 0.94f;

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
        BoxCount = result.BoxCount;
        ModelCount = result.ModelCount;
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
        BoxCount = 0;
        ModelCount = 0;
    }

    public void Dispose() => DisposeMeshes();
}
