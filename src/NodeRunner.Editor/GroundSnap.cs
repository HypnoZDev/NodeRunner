using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Collision placed in the world: a map file (identity) or the collision of an interior instance.
public readonly record struct PlacedCollision(CollisionMesh Mesh, Matrix4x4 Transform);

// "Put on the ground": moves an object up or down so that the bottom of its box rests on the collision below it.
public static class GroundSnap
{
    public const float MaxRise = 2f;

    // Corner rays are moved this far towards the centre, so a wall touching the object is not taken for the ground.
    private const float CornerInset = 0.9f;

    // World box of an object (8 corners of its type's box, placed), or null without a type box.
    public static (Vector3 Min, Vector3 Max)? WorldBox(MapEntity entity, Archetype? archetype)
    {
        if (archetype is null)
            return null;
        var (localMin, localMax) = (archetype.BoundsMin, archetype.BoundsMax);
        if (localMin == localMax)
        {
            var rooms = archetype.InteriorRooms.Where(r => r.BoundsMin != r.BoundsMax).ToList();
            if (rooms.Count == 0)
                return null;
            localMin = rooms.Select(r => r.BoundsMin).Aggregate(Vector3.Min);
            localMax = rooms.Select(r => r.BoundsMax).Aggregate(Vector3.Max);
        }
        var transform = entity.Transform;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var corner in Corners(localMin, localMax))
        {
            var p = Vector3.Transform(corner, transform);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    // New position that puts the object on the ground, or null if no collision lies below it.
    public static Vector3? Snap(MapEntity entity, Archetype? archetype, IReadOnlyList<PlacedCollision> collisions)
    {
        var box = WorldBox(entity, archetype) ?? (entity.Position, entity.Position);
        float startZ = StartHeight(box.Min.Z, box.Max.Z);

        // Centre of the object and the bottom corners of its box (as placed), slightly moved inwards.
        var center = new Vector2(entity.Position.X, entity.Position.Y);
        var points = new List<Vector2> { center };
        if (archetype is not null && archetype.BoundsMin != archetype.BoundsMax)
        {
            var transform = entity.Transform;
            var bottom = archetype.BoundsMin.Z;
            foreach (var corner in Corners(archetype.BoundsMin, archetype.BoundsMax).Where(c => c.Z == bottom))
            {
                var p = Vector3.Transform(corner, transform);
                points.Add(center + (new Vector2(p.X, p.Y) - center) * CornerInset);
            }
        }

        float? ground = null;
        foreach (var point in points)
        {
            if (HighestBelow(collisions, point, startZ) is { } z && (ground is null || z > ground))
                ground = z;
        }
        return ground is { } g ? entity.Position with { Z = entity.Position.Z + (g - box.Min.Z) } : null;
    }

    public static float StartHeight(float bottom, float top) => bottom + MathF.Min((top - bottom) * 0.5f, MaxRise);

    public static float? HighestBelow(IReadOnlyList<PlacedCollision> collisions, Vector2 point, float fromZ)
    {
        float? best = null;
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
                if (HeightAt(a, b, c, point) is { } z && z <= fromZ && (best is null || z > best))
                    best = z;
            }
        }
        return best;
    }

    // Height of a triangle above (x, y), or null if the vertical line misses it (or the triangle is vertical).
    public static float? HeightAt(Vector3 a, Vector3 b, Vector3 c, Vector2 p)
    {
        // Quick rejection on the triangle's 2D box.
        if (p.X < MathF.Min(a.X, MathF.Min(b.X, c.X)) || p.X > MathF.Max(a.X, MathF.Max(b.X, c.X))
            || p.Y < MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) || p.Y > MathF.Max(a.Y, MathF.Max(b.Y, c.Y)))
            return null;

        float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(d) < 1e-9f)
            return null; // vertical (wall): never the ground
        float u = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / d;
        float v = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / d;
        float w = 1f - u - v;
        const float Tolerance = -1e-5f;
        if (u < Tolerance || v < Tolerance || w < Tolerance)
            return null;
        return u * a.Z + v * b.Z + w * c.Z;
    }

    private static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (int i = 0; i < 8; i++)
            yield return new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
    }
}
