using System.Numerics;

namespace NodeRunner.Data.Collisions;

/// <summary>
/// Builds a <see cref="CollisionMesh"/>: triangles are added as they are, the game's collision primitives
/// (boxes, spheres, capsules, cylinders) are turned into a few triangles each.
/// </summary>
public sealed class CollisionMeshBuilder
{
    // Round primitives are only approximated: enough to see them and to put objects on them.
    private const int RoundSegments = 12;

    private readonly List<Vector3> _vertices = new();
    private readonly List<int> _indices = new();
    private readonly List<byte> _materials = new();
    private readonly List<int> _polygons = new();

    public int TriangleCount => _materials.Count;

    /// <summary>Number of the file's polygon the next triangles come from (-1: not a polygon, e.g. a lone box).</summary>
    public int Polygon { get; set; } = -1;

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, byte material)
    {
        int first = _vertices.Count;
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _indices.Add(first);
        _indices.Add(first + 1);
        _indices.Add(first + 2);
        _materials.Add(material);
        _polygons.Add(Polygon);
    }

    /// <summary>
    /// A box stored as the game does: four of its corners, each pair on the diagonal of a face (every other
    /// corner). The other four corners are the opposites through the centre.
    /// </summary>
    public void AddBoxFromCorners(Vector3 v1, Vector3 v2, Vector3 v3, Vector3 v4, byte material)
    {
        var center = (v1 + v2 + v3 + v4) * 0.25f;
        // The middle of two such corners is the centre of a face: centre + half an edge.
        var axisX = (v1 + v2) * 0.5f - center;
        var axisY = (v1 + v3) * 0.5f - center;
        var axisZ = (v1 + v4) * 0.5f - center;
        AddOrientedBox(center, axisX, axisY, axisZ, material);
    }

    /// <summary>An axis-aligned box, optionally transformed.</summary>
    public void AddBox(Vector3 min, Vector3 max, Matrix4x4 transform, byte material)
    {
        var center = Vector3.Transform((min + max) * 0.5f, transform);
        var half = (max - min) * 0.5f;
        AddOrientedBox(center,
            Vector3.TransformNormal(new Vector3(half.X, 0, 0), transform),
            Vector3.TransformNormal(new Vector3(0, half.Y, 0), transform),
            Vector3.TransformNormal(new Vector3(0, 0, half.Z), transform),
            material);
    }

    /// <summary>A box from its centre and three half edges.</summary>
    public void AddOrientedBox(Vector3 center, Vector3 halfX, Vector3 halfY, Vector3 halfZ, byte material)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            c[i] = center
                + ((i & 1) != 0 ? halfX : -halfX)
                + ((i & 2) != 0 ? halfY : -halfY)
                + ((i & 4) != 0 ? halfZ : -halfZ);
        }
        AddQuad(c[0], c[1], c[3], c[2], material); // bottom (-Z)
        AddQuad(c[4], c[6], c[7], c[5], material); // top (+Z)
        AddQuad(c[0], c[4], c[5], c[1], material); // -Y
        AddQuad(c[2], c[3], c[7], c[6], material); // +Y
        AddQuad(c[0], c[2], c[6], c[4], material); // -X
        AddQuad(c[1], c[5], c[7], c[3], material); // +X
    }

    public void AddSphere(Vector3 center, float radius, byte material) =>
        AddCapsule(center, center, radius, material);

    /// <summary>A cylinder between two points, closed at both ends.</summary>
    public void AddCylinder(Vector3 a, Vector3 b, float radius, byte material)
    {
        var (u, v) = Perpendiculars(b - a);
        for (int i = 0; i < RoundSegments; i++)
        {
            var d0 = Ring(u, v, radius, i);
            var d1 = Ring(u, v, radius, i + 1);
            AddQuad(a + d0, a + d1, b + d1, b + d0, material);
            AddTriangle(a, a + d1, a + d0, material);
            AddTriangle(b, b + d0, b + d1, material);
        }
    }

    /// <summary>A capsule (cylinder with round ends) between two points; a sphere when both points are the same.</summary>
    public void AddCapsule(Vector3 a, Vector3 b, float radius, byte material)
    {
        var axis = b - a;
        var (u, v) = Perpendiculars(axis);
        var w = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        const int Rings = RoundSegments / 2; // latitude steps from pole to pole

        // Latitude bands: the lower half sphere around a, the upper one around b.
        for (int r = 0; r < Rings; r++)
        {
            float lat0 = MathF.PI * (r / (float)Rings - 0.5f);
            float lat1 = MathF.PI * ((r + 1) / (float)Rings - 0.5f);
            var center = r < Rings / 2 ? a : b;
            for (int i = 0; i < RoundSegments; i++)
            {
                AddQuad(
                    center + Point(u, v, w, radius, lat0, i),
                    center + Point(u, v, w, radius, lat0, i + 1),
                    center + Point(u, v, w, radius, lat1, i + 1),
                    center + Point(u, v, w, radius, lat1, i),
                    material);
            }
        }

        // The straight part between the two half spheres.
        if (axis.LengthSquared() > 1e-8f)
        {
            for (int i = 0; i < RoundSegments; i++)
            {
                var d0 = Ring(u, v, radius, i);
                var d1 = Ring(u, v, radius, i + 1);
                AddQuad(a + d0, a + d1, b + d1, b + d0, material);
            }
        }
    }

    public CollisionMesh? Build()
    {
        if (_vertices.Count == 0)
            return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in _vertices)
        {
            min = Vector3.Min(min, vertex);
            max = Vector3.Max(max, vertex);
        }
        return new CollisionMesh
        {
            BoundsMin = min,
            BoundsMax = max,
            Vertices = _vertices.ToArray(),
            Indices = _indices.ToArray(),
            Materials = _materials.ToArray(),
            Polygons = _polygons.ToArray(),
        };
    }

    private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, byte material)
    {
        AddTriangle(a, b, c, material);
        AddTriangle(a, c, d, material);
    }

    private static Vector3 Ring(Vector3 u, Vector3 v, float radius, int step)
    {
        float angle = MathF.Tau * step / RoundSegments;
        return (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
    }

    private static Vector3 Point(Vector3 u, Vector3 v, Vector3 w, float radius, float latitude, int step) =>
        Ring(u, v, radius * MathF.Cos(latitude), step) + w * (radius * MathF.Sin(latitude));

    /// <summary>Two unit vectors perpendicular to <paramref name="axis"/> and to each other.</summary>
    private static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var w = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        var other = MathF.Abs(w.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(w, other));
        return (u, Vector3.Cross(w, u));
    }
}
