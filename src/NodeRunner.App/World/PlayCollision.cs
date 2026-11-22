using System.Numerics;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// The collision around the character of the play mode, as triangles sorted into square cells: read from the game
// again in the background when she walks away from where it was read.
internal sealed class PlayCollision
{
    private const float Radius = 50f;
    private const float ReadAgain = 20f; // Metres from where the area was read.
    private const float CellSize = 2f;
    private const int MaxCellsPerTriangle = 4096;

    // The body: spheres stacked above the highest step climbed (they never touch a step, only walls).
    public const float BodyRadius = 0.3f;
    private static readonly float[] BodyHeights = [0.8f, 1.2f, 1.55f];
    // Surfaces this close to flat are floors or ceilings: left to the ground test.
    private const float FlatNormal = 0.7f;

    private sealed class Area(object data, int revision, Vector2 centre, Vector3[] triangles, bool[] walls, Dictionary<(int, int), List<int>> cells)
    {
        public readonly object Data = data; // IPlayGame.Data and CollisionRevision when read.
        public readonly int Revision = revision;
        public readonly Vector2 Centre = centre;
        public readonly Vector3[] Triangles = triangles; // Three corners each.
        public readonly bool[] Walls = walls; // Per triangle: steep enough to stop the body.
        public readonly Dictionary<(int, int), List<int>> Cells = cells;
    }

    private Area? _area;
    private Task<Area>? _reading;

    // The collision under the character is known (false while the first area is read, or without collisions).
    public bool IsReady(Vector3 position) => _area is { } area && Vector2.Distance(area.Centre, Flat(position)) < Radius - 5f;

    public void Update(IPlayGame game, Vector3 position)
    {
        if (_reading is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                _area = done.Result;
            _reading = null;
        }
        if (_reading is not null || game.Data is not { } data)
            return;
        int revision = game.CollisionRevision;
        if (_area is { } area && ReferenceEquals(area.Data, data) && area.Revision == revision
            && Vector2.Distance(area.Centre, Flat(position)) < ReadAgain)
            return;
        var read = game.CollisionReader();
        var (min, max) = AreaAround(position);
        _reading = Task.Run(() => Build(data, revision, position, read(min, max)));
    }

    // A reading under way goes on (the game reads one area at a time): its area is replaced once the character is
    // elsewhere.
    public void Clear() => _area = null;

    // Height of the highest surface under (x, y) that is not above fromZ; null when there is none (or not read yet).
    public float? Ground(Vector2 point, float fromZ)
    {
        if (_area is not { } area || !area.Cells.TryGetValue(CellOf(point), out var list))
            return null;
        var triangles = area.Triangles;
        float? best = null;
        foreach (int t in list)
        {
            if (GroundSnap.HeightAt(triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2], point) is { } z && z <= fromZ
                && (best is null || z > best))
                best = z;
        }
        return best;
    }

    // The feet moved sideways until the body touches no wall (it slides along them).
    public Vector3 PushOut(Vector3 feet)
    {
        if (_area is not { } area)
            return feet;
        var triangles = area.Triangles;
        for (int pass = 0; pass < 3; pass++)
        {
            bool pushed = false;
            var (x0, y0) = CellOf(new Vector2(feet.X - BodyRadius, feet.Y - BodyRadius));
            var (x1, y1) = CellOf(new Vector2(feet.X + BodyRadius, feet.Y + BodyRadius));
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (!area.Cells.TryGetValue((x, y), out var list))
                        continue;
                    foreach (int t in list)
                    {
                        if (!area.Walls[t])
                            continue;
                        var (a, b, c) = (triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2]);
                        foreach (float height in BodyHeights)
                        {
                            var centre = feet with { Z = feet.Z + height };
                            var away = centre - ClosestPoint(centre, a, b, c);
                            if (away.LengthSquared() >= BodyRadius * BodyRadius)
                                continue;
                            var side = new Vector2(away.X, away.Y);
                            float length = side.Length();
                            if (length < 1e-4f)
                            {
                                // Centre on the wall itself: out along its normal.
                                var normal = Vector3.Cross(b - a, c - a);
                                side = Vector2.Normalize(new Vector2(normal.X, normal.Y));
                                length = 0f;
                            }
                            else
                                side /= length;
                            var moved = side * (BodyRadius - length + 0.001f);
                            feet += new Vector3(moved.X, moved.Y, 0f);
                            pushed = true;
                        }
                    }
                }
            }
            if (!pushed)
                break;
        }
        return feet;
    }

    // A ledge ahead to climb onto: a wall within reach at the height of her knees (from), and on its top a surface
    // between minRise and maxRise above the feet, with nothing over the wall's edge and room to stand up on top.
    // Returns the top's height and how far ahead the wall is; null when there is none.
    public (float Top, float Distance)? Ledge(Vector3 feet, Vector2 ahead, float from, float minRise, float maxRise)
    {
        const float Reach = 1f; // From the body's edge.
        const float Beyond = 0.3f; // Where on the top she lands, past the wall's face.
        const float Headroom = 1.6f;
        ahead = Vector2.Normalize(ahead);
        var knees = feet with { Z = feet.Z + from };
        float length = BodyRadius + Reach;
        if (Hit(knees, knees + new Vector3(ahead * length, 0f)) is not { } share)
            return null;
        float distance = share * length;
        var on = new Vector2(feet.X, feet.Y) + ahead * (distance + Beyond);
        if (Ground(on, feet.Z + maxRise) is not { } top || top - feet.Z < minRise)
            return null;
        // Over the edge: open, else the surface found is a floor behind a higher wall.
        var over = feet with { Z = top + 0.3f };
        if (Hit(over, new Vector3(on.X, on.Y, top + 0.3f)) is not null)
            return null;
        if (Hit(new Vector3(on.X, on.Y, top + 0.1f), new Vector3(on.X, on.Y, top + Headroom)) is not null)
            return null;
        return (top, distance);
    }

    // Where a line from a point to another first meets a surface, as a share of its length (0 to 1); null when it
    // meets none.
    public float? Hit(Vector3 from, Vector3 to)
    {
        if (_area is not { } area)
            return null;
        var triangles = area.Triangles;
        var direction = to - from;
        var (x0, y0) = CellOf(new Vector2(MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y)));
        var (x1, y1) = CellOf(new Vector2(MathF.Max(from.X, to.X), MathF.Max(from.Y, to.Y)));
        float? nearest = null;
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                if (!area.Cells.TryGetValue((x, y), out var list))
                    continue;
                foreach (int t in list)
                {
                    if (Crossing(from, direction, triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2]) is { } share
                        && (nearest is null || share < nearest))
                        nearest = share;
                }
            }
        }
        return nearest;
    }

    // Share of the segment (from, from + direction) where it crosses a triangle, either face.
    private static float? Crossing(Vector3 from, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return null;
        float inverse = 1f / det;
        var s = from - a;
        float u = Vector3.Dot(s, p) * inverse;
        if (u < 0f || u > 1f)
            return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
            return null;
        float share = Vector3.Dot(e2, q) * inverse;
        return share is >= 0f and <= 1f ? share : null;
    }

    // The point of a triangle closest to a point.
    private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
            return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
            return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
            return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denominator = 1f / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    // Collision given as it is (tests): the area around a point.
    internal void Use(IEnumerable<PlacedCollision> collisions, Vector3 position) => _area = Build(this, 0, position, collisions);

    private static (Vector3 Min, Vector3 Max) AreaAround(Vector3 position) =>
        (new Vector3(position.X - Radius, position.Y - Radius, -1000f), new Vector3(position.X + Radius, position.Y + Radius, position.Z + 300f));

    private static Area Build(object data, int revision, Vector3 position, IEnumerable<PlacedCollision> collisions)
    {
        var (min, max) = AreaAround(position);
        var triangles = new List<Vector3>();
        var walls = new List<bool>();
        var cells = new Dictionary<(int, int), List<int>>();
        foreach (var (mesh, transform) in collisions)
        {
            bool identity = transform.IsIdentity;
            var vertices = mesh.Vertices;
            var indices = mesh.Indices;
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                if (!identity)
                {
                    a = Vector3.Transform(a, transform);
                    b = Vector3.Transform(b, transform);
                    c = Vector3.Transform(c, transform);
                }
                var low = Vector3.Min(a, Vector3.Min(b, c));
                var high = Vector3.Max(a, Vector3.Max(b, c));
                if (high.X < min.X || low.X > max.X || high.Y < min.Y || low.Y > max.Y)
                    continue;
                var (x0, y0) = CellOf(new Vector2(Math.Max(low.X, min.X), Math.Max(low.Y, min.Y)));
                var (x1, y1) = CellOf(new Vector2(Math.Min(high.X, max.X), Math.Min(high.Y, max.Y)));
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerTriangle)
                    continue;
                int index = triangles.Count / 3;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                var normal = Vector3.Cross(b - a, c - a);
                float area = normal.Length();
                walls.Add(area > 1e-8f && MathF.Abs(normal.Z / area) < FlatNormal);
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!cells.TryGetValue((x, y), out var list))
                            cells[(x, y)] = list = [];
                        list.Add(index);
                    }
                }
            }
        }
        return new Area(data, revision, Flat(position), triangles.ToArray(), walls.ToArray(), cells);
    }

    private static (int, int) CellOf(Vector2 point) => ((int)MathF.Floor(point.X / CellSize), (int)MathF.Floor(point.Y / CellSize));

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);
}
