using System.Numerics;
using NodeRunner.Data.Collisions;

namespace NodeRunner.Editor;

// Finds the collision polygons seen in a screen rectangle: the meshes are drawn on the CPU into a small buffer covering
// only the rectangle, keeping the nearest polygon per sample, so what is hidden behind a wall is not taken.
public static class CollisionPicker
{
    private const int MaxSamples = 256; // per side
    private const float NearW = 0.05f;

    public static HashSet<CollisionPolygon> Pick(IEnumerable<(string Path, CollisionMesh Mesh)> meshes, Matrix4x4 viewProjection,
        Vector2 viewport, Vector2 rectMin, Vector2 rectMax)
    {
        float step = MathF.Max(1f, MathF.Max(rectMax.X - rectMin.X, rectMax.Y - rectMin.Y) / MaxSamples);
        int nx = Math.Max(1, (int)MathF.Ceiling((rectMax.X - rectMin.X) / step));
        int ny = Math.Max(1, (int)MathF.Ceiling((rectMax.Y - rectMin.Y) / step));
        var depth = new float[nx * ny]; // 1 / w of the nearest surface, 0 = nothing
        var ids = new int[nx * ny];
        Array.Fill(ids, -1);
        var polygons = new List<CollisionPolygon>();
        var polygonIds = new Dictionary<CollisionPolygon, int>();

        Span<Vector4> clipped = stackalloc Vector4[8];
        foreach (var (path, mesh) in meshes)
        {
            var clip = new Vector4[mesh.Vertices.Count];
            for (int v = 0; v < clip.Length; v++)
                clip[v] = Vector4.Transform(new Vector4(mesh.Vertices[v], 1f), viewProjection);

            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                var a = clip[mesh.Indices[t * 3]];
                var b = clip[mesh.Indices[t * 3 + 1]];
                var c = clip[mesh.Indices[t * 3 + 2]];
                if (a.W < NearW && b.W < NearW && c.W < NearW)
                    continue;
                int count = ClipNear(a, b, c, clipped);
                if (count < 3)
                    continue;

                // Lone primitives hide what is behind them but cannot be selected.
                int polygon = t < mesh.Polygons.Count ? mesh.Polygons[t] : -1;
                int id = -2;
                if (polygon >= 0)
                {
                    var key = new CollisionPolygon(path, polygon);
                    if (!polygonIds.TryGetValue(key, out id))
                    {
                        id = polygons.Count;
                        polygonIds[key] = id;
                        polygons.Add(key);
                    }
                }

                var s0 = Screen(clipped[0], viewport);
                for (int i = 1; i + 1 < count; i++)
                    Rasterize(s0, Screen(clipped[i], viewport), Screen(clipped[i + 1], viewport), id, rectMin, step, nx, ny, depth, ids);
            }
        }

        var picked = new HashSet<CollisionPolygon>();
        foreach (int id in ids)
        {
            if (id >= 0)
                picked.Add(polygons[id]);
        }
        return picked;
    }

    // Screen position (pixels) and 1 / w, which varies linearly across the screen.
    private static Vector3 Screen(Vector4 clip, Vector2 viewport) => new(
        (clip.X / clip.W * 0.5f + 0.5f) * viewport.X,
        (0.5f - clip.Y / clip.W * 0.5f) * viewport.Y,
        1f / clip.W);

    // The triangle cut by the near plane (w = NearW): up to 4 points.
    private static int ClipNear(Vector4 a, Vector4 b, Vector4 c, Span<Vector4> output)
    {
        int count = 0;
        ReadOnlySpan<Vector4> input = [a, b, c];
        for (int i = 0; i < 3; i++)
        {
            var p = input[i];
            var q = input[(i + 1) % 3];
            bool pIn = p.W >= NearW, qIn = q.W >= NearW;
            if (pIn)
                output[count++] = p;
            if (pIn != qIn)
                output[count++] = Vector4.Lerp(p, q, (NearW - p.W) / (q.W - p.W));
        }
        return count;
    }

    private static void Rasterize(Vector3 a, Vector3 b, Vector3 c, int id, Vector2 rectMin, float step, int nx, int ny,
        float[] depth, int[] ids)
    {
        float area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 1e-6f)
            return;
        // Sample i is at rectMin + (i + 0.5) * step.
        int x0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - rectMin.X) / step - 0.5f));
        int x1 = Math.Min(nx - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - rectMin.X) / step - 0.5f));
        int y0 = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - rectMin.Y) / step - 0.5f));
        int y1 = Math.Min(ny - 1, (int)MathF.Floor((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - rectMin.Y) / step - 0.5f));
        for (int y = y0; y <= y1; y++)
        {
            float py = rectMin.Y + (y + 0.5f) * step;
            for (int x = x0; x <= x1; x++)
            {
                float px = rectMin.X + (x + 0.5f) * step;
                float w0 = Edge(b, c, px, py) / area;
                float w1 = Edge(c, a, px, py) / area;
                float w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;
                float inverseW = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int i = y * nx + x;
                if (inverseW > depth[i])
                {
                    depth[i] = inverseW;
                    ids[i] = id;
                }
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
}
