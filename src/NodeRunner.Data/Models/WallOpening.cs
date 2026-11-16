using System.Numerics;

namespace NodeRunner.Data.Models;

// A door-sized hole through a wall: a vertical rectangle (its bottom middle, the side it faces), crossing the wall over
// Depth on both sides of the rectangle. Model or world space, the same as the triangles it cuts.
public readonly record struct Opening(Vector3 Bottom, Vector3 Normal, float Width, float Height, float Depth = Opening.DefaultDepth)
{
    public const float DefaultWidth = 1.2f;
    public const float DefaultHeight = 2.3f;
    public const float DefaultDepth = 1f;

    // Horizontal, to the right when facing the wall from the side the normal points to.
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, Normal));

    public Vector3 Center => Bottom + new Vector3(0, 0, Height * 0.5f);

    // From a point clicked on a wall and the wall's normal: the normal made horizontal, the bottom on floor when given
    // (a height under the point), otherwise the rectangle centred on the point. Null on a floor or ceiling.
    public static Opening? At(Vector3 point, Vector3 surfaceNormal, float width = DefaultWidth, float height = DefaultHeight, float? floor = null)
    {
        var flat = new Vector3(surfaceNormal.X, surfaceNormal.Y, 0);
        if (flat.LengthSquared() < 0.25f * surfaceNormal.LengthSquared())
            return null;
        float bottom = floor is { } f && f <= point.Z ? f : point.Z - height * 0.5f;
        return new Opening(new Vector3(point.X, point.Y, bottom), Vector3.Normalize(flat), width, height);
    }

    // The point in the opening's axes: across (right), up from the bottom, out of the wall.
    public Vector3 Local(Vector3 p)
    {
        var d = p - Bottom;
        return new Vector3(Vector3.Dot(d, Right), d.Z, Vector3.Dot(d, Normal));
    }

    public Vector3 World(float right, float up, float outwards) => Bottom + Right * right + Vector3.UnitZ * up + Normal * outwards;

    // Outline to draw: the rectangle on the wall, and fainter ones where the cut stops on both sides.
    public IEnumerable<(Vector3 From, Vector3 To)> Edges()
    {
        float w = Width * 0.5f;
        foreach (float o in new[] { 0f, Depth, -Depth })
        {
            var a = World(-w, 0, o);
            var b = World(w, 0, o);
            var c = World(w, Height, o);
            var d = World(-w, Height, o);
            yield return (a, b);
            yield return (b, c);
            yield return (c, d);
            yield return (d, a);
        }
        yield return (World(-w, 0, -Depth), World(-w, 0, Depth));
        yield return (World(w, 0, -Depth), World(w, 0, Depth));
        yield return (World(w, Height, -Depth), World(w, Height, Depth));
        yield return (World(-w, Height, -Depth), World(-w, Height, Depth));
    }
}

// A corner of a new triangle: a mix of the three corners of a triangle of the mesh (weights adding up to 1), so every
// value of the vertex (texture, colour, normal) follows the original surface.
public readonly record struct CutVertex(int Triangle, Vector3 Weights);

// What a cut does to one mesh: triangles to remove, and the triangles to add instead (corners in Vertices).
public sealed record MeshCut(List<int> Removed, List<CutVertex> Vertices, List<int> Indices)
{
    public bool IsEmpty => Removed.Count == 0;
}

// Cuts an opening through the triangles of a mesh. Triangles facing the opening (the wall) lose the part inside the
// rectangle: they are replaced by the pieces around it. Triangles turned sideways (floor, sides of a recess) go only
// when they are wholly inside.
public static class WallOpening
{
    // Below this, a triangle is sideways to the opening (|cos| of the angle between the normals).
    private const float Facing = 0.25f;
    private const float Epsilon = 1e-4f;
    private const float SidewaysInset = 0.01f;
    private const float MinArea = 1e-6f;

    public static MeshCut Cut(IReadOnlyList<Vector3> positions, IReadOnlyList<ushort> indices, Opening opening) =>
        Cut(positions, indices.Count / 3, i => indices[i], opening);

    public static MeshCut Cut(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Opening opening) =>
        Cut(positions, indices.Count / 3, i => indices[i], opening);

    private static MeshCut Cut(IReadOnlyList<Vector3> positions, int triangles, Func<int, int> index, Opening opening)
    {
        var cut = new MeshCut([], [], []);
        float hw = opening.Width * 0.5f;
        float height = opening.Height;
        for (int t = 0; t < triangles; t++)
        {
            int i0 = index(t * 3), i1 = index(t * 3 + 1), i2 = index(t * 3 + 2);
            if (i0 == i1 || i1 == i2 || i0 == i2 || Math.Max(i0, Math.Max(i1, i2)) >= positions.Count || Math.Min(i0, Math.Min(i1, i2)) < 0)
                continue;
            var a = opening.Local(positions[i0]);
            var b = opening.Local(positions[i1]);
            var c = opening.Local(positions[i2]);
            if (Max(a.X, b.X, c.X) <= -hw || Min(a.X, b.X, c.X) >= hw || Max(a.Y, b.Y, c.Y) <= 0 || Min(a.Y, b.Y, c.Y) >= height
                || Min(a.Z, b.Z, c.Z) > opening.Depth || Max(a.Z, b.Z, c.Z) < -opening.Depth)
                continue;
            var normal = Vector3.Cross(b - a, c - a);
            float length = normal.Length();
            if (length < MinArea)
                continue;
            if (MathF.Abs(normal.Z) / length < Facing)
            {
                // Sideways: removed only when wholly inside (a step under the door stays).
                if (Inside(a, hw, height, opening.Depth) && Inside(b, hw, height, opening.Depth) && Inside(c, hw, height, opening.Depth))
                    cut.Removed.Add(t);
                continue;
            }
            // Only the part of the wall within the depth: a triangle reaching past it is left whole.
            if (MathF.Abs((a.Z + b.Z + c.Z) / 3f) > opening.Depth)
                continue;
            CutTriangle(t, a, b, c, hw, height, cut);
        }
        return cut;
    }

    private static bool Inside(Vector3 p, float hw, float height, float depth) =>
        MathF.Abs(p.X) < hw - SidewaysInset && p.Y > SidewaysInset && p.Y < height - SidewaysInset && MathF.Abs(p.Z) <= depth;

    private readonly record struct Point(Vector2 At, Vector3 Weights);

    // Removes the part of the triangle inside the rectangle (in the wall's plane): the polygon is split by each side of the
    // rectangle in turn, the part outside each side is kept.
    private static void CutTriangle(int t, Vector3 a, Vector3 b, Vector3 c, float hw, float height, MeshCut cut)
    {
        var rest = new List<Point>
        {
            new(new Vector2(a.X, a.Y), Vector3.UnitX),
            new(new Vector2(b.X, b.Y), Vector3.UnitY),
            new(new Vector2(c.X, c.Y), Vector3.UnitZ),
        };
        var kept = new List<List<Point>>();
        // Each side: inside when dot(axis, p) <= limit.
        (Vector2 Axis, float Limit)[] sides = [(new(-1, 0), hw), (new(1, 0), hw), (new(0, -1), 0f), (new(0, 1), height)];
        foreach (var (axis, limit) in sides)
        {
            var (inside, outside) = Split(rest, axis, limit);
            if (Area(outside) > MinArea)
                kept.Add(outside);
            rest = inside;
            if (Area(rest) <= MinArea)
                break;
        }
        // Nothing of the triangle in the rectangle: untouched.
        if (Area(rest) <= MinArea)
            return;
        cut.Removed.Add(t);
        foreach (var polygon in kept)
        {
            int first = cut.Vertices.Count;
            foreach (var p in polygon)
                cut.Vertices.Add(new CutVertex(t, p.Weights));
            // A convex polygon in the triangle's own order: a fan keeps the side it faces.
            for (int k = 1; k + 1 < polygon.Count; k++)
            {
                if (Area([polygon[0], polygon[k], polygon[k + 1]]) <= MinArea)
                    continue;
                cut.Indices.Add(first);
                cut.Indices.Add(first + k);
                cut.Indices.Add(first + k + 1);
            }
        }
    }

    private static (List<Point> Inside, List<Point> Outside) Split(List<Point> polygon, Vector2 axis, float limit)
    {
        var inside = new List<Point>();
        var outside = new List<Point>();
        for (int i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            float dp = Vector2.Dot(axis, p.At) - limit;
            float dq = Vector2.Dot(axis, q.At) - limit;
            if (dp <= Epsilon)
                inside.Add(p);
            if (dp >= -Epsilon)
                outside.Add(p);
            if ((dp < -Epsilon && dq > Epsilon) || (dp > Epsilon && dq < -Epsilon))
            {
                float s = dp / (dp - dq);
                var cross = new Point(Vector2.Lerp(p.At, q.At, s), Vector3.Lerp(p.Weights, q.Weights, s));
                inside.Add(cross);
                outside.Add(cross);
            }
        }
        return (inside, outside);
    }

    // Unsigned area of a polygon in the wall's plane.
    private static float Area(List<Point> polygon)
    {
        if (polygon.Count < 3)
            return 0;
        float sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i].At;
            var q = polygon[(i + 1) % polygon.Count].At;
            sum += p.X * q.Y - q.X * p.Y;
        }
        return MathF.Abs(sum) * 0.5f;
    }

    private static float Min(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));
    private static float Max(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));
}
