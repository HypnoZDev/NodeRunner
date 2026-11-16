using System.Numerics;

namespace NodeRunner.Editor;

// A triangle of one of a model's meshes.
public readonly record struct MeshTriangle(int Mesh, int Triangle);

// Parts of a model, for removing a piece of it (a logo, a sign): the triangle under a ray, the piece it belongs to
// (triangles touching each other), and the matching triangles of the simpler versions of the model.
public static class ModelParts
{
    private const float Weld = 1000f; // Corners closer than 1 mm are the same point.

    public static bool IsEmpty(Vector3[] positions, ushort[] indices, int triangle)
    {
        int i = triangle * 3;
        return indices[i] == indices[i + 1] || indices[i + 1] == indices[i + 2] || indices[i] == indices[i + 2];
    }

    // The nearest triangle crossed by the ray (in the model's space), null when none.
    public static (MeshTriangle Triangle, float Distance)? Hit(IReadOnlyList<(Vector3[] Positions, ushort[] Indices)> meshes,
        Vector3 origin, Vector3 direction)
    {
        (MeshTriangle, float)? best = null;
        for (int m = 0; m < meshes.Count; m++)
        {
            var (p, idx) = meshes[m];
            for (int t = 0; t * 3 + 2 < idx.Length; t++)
            {
                if (IsEmpty(p, idx, t))
                    continue;
                if (RayTriangle(origin, direction, p[idx[t * 3]], p[idx[t * 3 + 1]], p[idx[t * 3 + 2]]) is { } d
                    && (best is null || d < best.Value.Item2))
                    best = (new MeshTriangle(m, t), d);
            }
        }
        return best;
    }

    // The triangles of the mesh joined to this one through shared corners: one separate piece of the model.
    public static HashSet<int> Connected(Vector3[] positions, ushort[] indices, int triangle)
    {
        var byCorner = new Dictionary<(int, int, int), List<int>>();
        int count = indices.Length / 3;
        for (int t = 0; t < count; t++)
        {
            if (IsEmpty(positions, indices, t))
                continue;
            for (int k = 0; k < 3; k++)
            {
                var key = Key(positions[indices[t * 3 + k]]);
                if (!byCorner.TryGetValue(key, out var list))
                    byCorner[key] = list = [];
                list.Add(t);
            }
        }
        var piece = new HashSet<int> { triangle };
        var queue = new Queue<int>([triangle]);
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                if (!byCorner.TryGetValue(Key(positions[indices[t * 3 + k]]), out var touching))
                    continue;
                foreach (int other in touching)
                {
                    if (piece.Add(other))
                        queue.Enqueue(other);
                }
            }
        }
        return piece;
    }

    // The triangles whose three corners are in the box (the same piece in a simpler version of the model).
    public static List<int> Inside(Vector3[] positions, ushort[] indices, Vector3 min, Vector3 max)
    {
        var found = new List<int>();
        for (int t = 0; t * 3 + 2 < indices.Length; t++)
        {
            if (IsEmpty(positions, indices, t))
                continue;
            bool inside = true;
            for (int k = 0; k < 3 && inside; k++)
            {
                var p = positions[indices[t * 3 + k]];
                inside = Vector3.Clamp(p, min, max) == p;
            }
            if (inside)
                found.Add(t);
        }
        return found;
    }

    public static (Vector3 Min, Vector3 Max) Box(Vector3[] positions, ushort[] indices, IEnumerable<int> triangles)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int t in triangles)
        {
            for (int k = 0; k < 3; k++)
            {
                var p = positions[indices[t * 3 + k]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return (min, max);
    }

    private static (int, int, int) Key(Vector3 p) =>
        ((int)MathF.Round(p.X * Weld), (int)MathF.Round(p.Y * Weld), (int)MathF.Round(p.Z * Weld));

    private static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
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
}
