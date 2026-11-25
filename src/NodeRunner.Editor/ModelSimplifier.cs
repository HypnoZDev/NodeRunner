using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.Editor;

// A lighter copy of a model, to be seen from afar (LOD): parts too small to be seen are left out, the others lose the
// triangles that change their shape least (edges collapsed by quadric error). The copies of a point (texture seams,
// hard edges) always move together, so no crack opens; textures stay the same.
public static class ModelSimplifier
{
    // ratio: the share of triangles to keep (0.25 = a quarter). maxError: the largest shape change allowed, as a share
    // of the model's size (0.02 = 2 %): a mesh stops losing triangles there even above the ratio. smallPart: parts
    // smaller than this share of the model are left out.
    public static ModelData Simplify(ModelData model, float ratio, float maxError = 0.02f, float smallPart = 0.03f) =>
        SimplifyWithSources(model, ratio, maxError, smallPart).Model;

    // Same, with the index in model.Meshes of each mesh kept (to give it the same textures).
    public static (ModelData Model, int[] Sources) SimplifyWithSources(ModelData model, float ratio, float maxError = 0.02f, float smallPart = 0.03f)
    {
        float size = Vector3.Distance(model.BoundsMin, model.BoundsMax);
        if (size <= 0f)
            size = MeshSize(model.Meshes);
        double limit = Math.Pow(size * maxError, 2);
        var meshes = new List<ModelMesh>();
        var sources = new List<int>();
        for (int i = 0; i < model.Meshes.Count; i++)
        {
            var mesh = model.Meshes[i];
            if (mesh.Indices.Length < 3 || MeshSize([mesh]) < size * smallPart)
                continue;
            var simplified = SimplifyMesh(mesh, Target(mesh, ratio), limit);
            if (simplified.Indices.Length < 3)
                continue;
            meshes.Add(simplified);
            sources.Add(i);
        }
        // Never empty: a model made of small parts only keeps its biggest one.
        if (meshes.Count == 0 && Enumerable.Range(0, model.Meshes.Count).Where(i => model.Meshes[i].Indices.Length >= 3)
                .Select(i => (int?)i).MaxBy(i => MeshSize([model.Meshes[i!.Value]])) is { } biggest)
        {
            meshes.Add(SimplifyMesh(model.Meshes[biggest], Target(model.Meshes[biggest], ratio), limit));
            sources.Add(biggest);
        }
        var result = new ModelData
        {
            NameHash = model.NameHash,
            BoundsMin = model.BoundsMin,
            BoundsMax = model.BoundsMax,
            Meshes = meshes,
            EmbeddedTextures = model.EmbeddedTextures,
            Skeleton = model.Skeleton,
        };
        return (result, sources.ToArray());
    }

    private const int MinTriangles = 4;

    private static int Target(ModelMesh mesh, float ratio) => Math.Max(MinTriangles, (int)MathF.Round(mesh.Indices.Length / 3 * ratio));

    private static float MeshSize(IEnumerable<ModelMesh> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
        {
            foreach (int i in mesh.Indices)
            {
                min = Vector3.Min(min, mesh.Positions[i]);
                max = Vector3.Max(max, mesh.Positions[i]);
            }
        }
        return min.X > max.X ? 0f : Vector3.Distance(min, max);
    }

    // The work is done on points (vertices at the same place are one point); a vertex keeps its own texture coordinates
    // and normal until it is merged into a vertex it shares a triangle with.
    public static ModelMesh SimplifyMesh(ModelMesh mesh, int targetTriangles, double maxError)
    {
        var (kept, indices, positions) = Simplify(mesh.Positions, Array.ConvertAll(mesh.Indices, i => (int)i), targetTriangles, maxError);
        return mesh.Subset(kept, Array.ConvertAll(indices, i => (ushort)i), positions);
    }

    // Triangles over positions simplified: Kept = the vertices still used (in their order), Indices = the triangles
    // over them (numbered in Kept), Positions = where every vertex of positions now is (some moved onto a neighbour).
    public static (int[] Kept, int[] Indices, Vector3[] Positions) Simplify(Vector3[] positions, int[] triangles, int targetTriangles, double maxError)
    {
        int n = positions.Length;
        var vertexPositions = (Vector3[])positions.Clone();
        var pointOf = new int[n];
        var points = new List<Vector3>();
        var pointIndex = new Dictionary<Vector3, int>();
        for (int i = 0; i < n; i++)
        {
            if (!pointIndex.TryGetValue(vertexPositions[i], out int p))
            {
                p = points.Count;
                points.Add(vertexPositions[i]);
                pointIndex[vertexPositions[i]] = p;
            }
            pointOf[i] = p;
        }
        int pointCount = points.Count;

        int[] tris = (int[])triangles.Clone();
        int triCount = tris.Length / 3;
        var alive = new bool[triCount];
        var around = new List<int>[pointCount];
        for (int i = 0; i < pointCount; i++)
            around[i] = [];
        var quadrics = new Quadric[pointCount];
        var pointEdges = new Dictionary<(int, int), int>();
        var vertexEdges = new Dictionary<(int, int), int>();
        int aliveCount = 0;
        for (int t = 0; t < triCount; t++)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            if (a >= n || b >= n || c >= n)
                continue;
            int pa = pointOf[a], pb = pointOf[b], pc = pointOf[c];
            if (pa == pb || pb == pc || pa == pc)
                continue;
            alive[t] = true;
            aliveCount++;
            around[pa].Add(t);
            around[pb].Add(t);
            around[pc].Add(t);
            var cross = Vector3.Cross(points[pb] - points[pa], points[pc] - points[pa]);
            float area = cross.Length();
            if (area > 0f)
            {
                var q = Quadric.Plane(cross / area, points[pa], 1f);
                quadrics[pa].Add(q);
                quadrics[pb].Add(q);
                quadrics[pc].Add(q);
            }
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = Key(pointOf[u], pointOf[v]);
                pointEdges[key] = pointEdges.GetValueOrDefault(key) + 1;
                var vkey = Key(u, v);
                vertexEdges[vkey] = vertexEdges.GetValueOrDefault(vkey) + 1;
            }
        }

        // Open borders and texture seams: a plane across each such edge keeps the line in place.
        for (int t = 0; t < triCount; t++)
        {
            if (!alive[t])
                continue;
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            var normal = Vector3.Cross(points[pointOf[b]] - points[pointOf[a]], points[pointOf[c]] - points[pointOf[a]]);
            if (normal.LengthSquared() == 0f)
                continue;
            normal = Vector3.Normalize(normal);
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                int pu = pointOf[u], pv = pointOf[v];
                if (pointEdges[Key(pu, pv)] != 1 && vertexEdges[Key(u, v)] != 1)
                    continue;
                var along = points[pv] - points[pu];
                if (along.LengthSquared() == 0f)
                    continue;
                var q = Quadric.Plane(Vector3.Normalize(Vector3.Cross(along, normal)), points[pu], BorderWeight);
                quadrics[pu].Add(q);
                quadrics[pv].Add(q);
            }
        }

        var queue = new PriorityQueue<(int From, int To), double>();
        void Offer(int p, int q) => queue.Enqueue((p, q), Cost(quadrics, points, p, q));
        foreach (var (p, q) in pointEdges.Keys)
        {
            Offer(p, q);
            Offer(q, p);
        }

        var removed = new bool[pointCount];
        while (aliveCount > targetTriangles && queue.TryDequeue(out var edge, out double cost))
        {
            var (p, q) = edge;
            if (removed[p] || removed[q])
                continue;
            double now = Cost(quadrics, points, p, q);
            if (now > cost + 1e-12)
            {
                queue.Enqueue(edge, now);
                continue;
            }
            if (now > maxError)
                break;
            if (!Connected(around[p], tris, alive, pointOf, q) || Flips(around[p], tris, alive, pointOf, points, p, q))
                continue;

            // Each vertex at p goes into a vertex at q it shares a triangle with, or moves to q keeping its own attributes.
            var into = new Dictionary<int, int>();
            foreach (int t in around[p])
            {
                if (!alive[t])
                    continue;
                int o = t * 3;
                for (int k = 0; k < 3; k++)
                {
                    int u = tris[o + k];
                    if (pointOf[u] != p)
                        continue;
                    for (int m = 0; m < 3; m++)
                    {
                        if (pointOf[tris[o + m]] == q)
                            into.TryAdd(u, tris[o + m]);
                    }
                }
            }
            foreach (int t in around[p])
            {
                if (!alive[t])
                    continue;
                int o = t * 3;
                for (int k = 0; k < 3; k++)
                {
                    int u = tris[o + k];
                    if (pointOf[u] != p)
                        continue;
                    if (into.TryGetValue(u, out int v))
                        tris[o + k] = v;
                    else
                    {
                        pointOf[u] = q;
                        vertexPositions[u] = points[q];
                    }
                }
                int pa = pointOf[tris[o]], pb = pointOf[tris[o + 1]], pc = pointOf[tris[o + 2]];
                if (pa == pb || pb == pc || pa == pc)
                {
                    alive[t] = false;
                    aliveCount--;
                }
                else
                    around[q].Add(t);
            }
            around[p].Clear();
            removed[p] = true;
            quadrics[q].Add(quadrics[p]);
            around[q] = around[q].Where(t => alive[t]).Distinct().ToList();
            foreach (int w in Neighbours(around[q], tris, pointOf, q))
            {
                Offer(q, w);
                Offer(w, q);
            }
        }

        // The vertices still used, in their order.
        var newIndex = new int[n];
        Array.Fill(newIndex, -1);
        var kept = new List<int>();
        var indices = new List<int>();
        for (int t = 0; t < triCount; t++)
        {
            if (!alive[t])
                continue;
            for (int k = 0; k < 3; k++)
            {
                int i = tris[t * 3 + k];
                if (newIndex[i] < 0)
                {
                    newIndex[i] = kept.Count;
                    kept.Add(i);
                }
                indices.Add(newIndex[i]);
            }
        }
        return (kept.ToArray(), indices.ToArray(), vertexPositions);
    }

    private const float BorderWeight = 10f;

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    private static double Cost(Quadric[] quadrics, List<Vector3> points, int p, int q)
    {
        var sum = quadrics[p];
        sum.Add(quadrics[q]);
        return Math.Max(0d, sum.Error(points[q]));
    }

    private static bool Connected(List<int> around, int[] tris, bool[] alive, int[] pointOf, int q)
    {
        foreach (int t in around)
        {
            if (alive[t] && (pointOf[tris[t * 3]] == q || pointOf[tris[t * 3 + 1]] == q || pointOf[tris[t * 3 + 2]] == q))
                return true;
        }
        return false;
    }

    // Whether moving p onto q would turn a triangle over (or squash it flat).
    private static bool Flips(List<int> around, int[] tris, bool[] alive, int[] pointOf, List<Vector3> points, int p, int q)
    {
        Span<Vector3> before = stackalloc Vector3[3];
        Span<Vector3> after = stackalloc Vector3[3];
        foreach (int t in around)
        {
            int o = t * 3;
            if (!alive[t] || pointOf[tris[o]] == q || pointOf[tris[o + 1]] == q || pointOf[tris[o + 2]] == q)
                continue;
            for (int k = 0; k < 3; k++)
            {
                int point = pointOf[tris[o + k]];
                before[k] = points[point];
                after[k] = point == p ? points[q] : before[k];
            }
            var n0 = Vector3.Cross(before[1] - before[0], before[2] - before[0]);
            var n1 = Vector3.Cross(after[1] - after[0], after[2] - after[0]);
            float lengths = n0.Length() * n1.Length();
            if (lengths == 0f || Vector3.Dot(n0, n1) < lengths * 0.3f)
                return true;
        }
        return false;
    }

    private static IEnumerable<int> Neighbours(List<int> around, int[] tris, int[] pointOf, int q)
    {
        var seen = new HashSet<int>();
        foreach (int t in around)
        {
            for (int k = 0; k < 3; k++)
            {
                int w = pointOf[tris[t * 3 + k]];
                if (w != q && seen.Add(w))
                    yield return w;
            }
        }
    }

    // Sum of squared distances to planes (symmetric 4x4 matrix, upper half).
    private struct Quadric
    {
        private double _a, _b, _c, _d, _e, _f, _g, _h, _i, _j;

        public static Quadric Plane(Vector3 normal, Vector3 point, float weight)
        {
            double a = normal.X, b = normal.Y, c = normal.Z, d = -Vector3.Dot(normal, point);
            return new Quadric
            {
                _a = weight * a * a, _b = weight * a * b, _c = weight * a * c, _d = weight * a * d,
                _e = weight * b * b, _f = weight * b * c, _g = weight * b * d,
                _h = weight * c * c, _i = weight * c * d,
                _j = weight * d * d,
            };
        }

        public void Add(Quadric q)
        {
            _a += q._a; _b += q._b; _c += q._c; _d += q._d; _e += q._e;
            _f += q._f; _g += q._g; _h += q._h; _i += q._i; _j += q._j;
        }

        public readonly double Error(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return _a * x * x + 2 * _b * x * y + 2 * _c * x * z + 2 * _d * x
                   + _e * y * y + 2 * _f * y * z + 2 * _g * y
                   + _h * z * z + 2 * _i * z + _j;
        }
    }
}
