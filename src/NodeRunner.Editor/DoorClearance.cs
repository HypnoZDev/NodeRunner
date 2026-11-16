using System.Numerics;
using NodeRunner.Data.Collisions;

namespace NodeRunner.Editor;

// How far a swinging door really opens: its leaf is turned step by step and tested against the collision around it
// (walls, furniture of the map files and interiors). The leaf is slightly shrunk so that the floor, the frame and the
// wall it closes against are not taken for obstacles.
public static class DoorClearance
{
    private const float Step = 3f;
    private const float FirstAngle = 12f;
    private const float BottomGap = 0.2f;
    private const float TopGap = 0.15f;
    private const float EdgeGap = 0.03f;
    private const float Search = 0.3f;
    private const float ClosedSlab = 0.2f;

    // The opening angles reached on each side (degrees, same signs as minAngle / maxAngle): equal to them when nothing
    // is in the way.
    public static (float Min, float Max) Reach(DoorShape shape, float minAngle, float maxAngle, Matrix4x4 transform,
        IEnumerable<PlacedCollision> collisions)
    {
        if (shape.Kind != DoorKind.Hinged || shape.Width < 0.2f || !Matrix4x4.Invert(transform, out var toDoor))
            return (minAngle, maxAngle);
        var triangles = Near(shape, toDoor, collisions);
        if (triangles.Count == 0)
            return (minAngle, maxAngle);
        return (Side(shape, triangles, minAngle), Side(shape, triangles, maxAngle));
    }

    // Opening that lets someone walk through. A door stopped a little before its limit is only resting against the wall
    // next to it (measured on the game's doors: 4 % open less than this on both sides, mostly scripted doors).
    public const float Passage = 70f;

    public static bool IsBlocked(float wanted, float reached) => MathF.Abs(reached) < MathF.Abs(wanted) - 0.5f;

    // No side opens wide enough to walk through (or as wide as the tuning allows, when that is less).
    public static bool NoPassage(float wantedMin, float wantedMax, float reachedMin, float reachedMax)
    {
        float wanted = MathF.Max(MathF.Abs(wantedMin), MathF.Abs(wantedMax));
        return MathF.Max(MathF.Abs(reachedMin), MathF.Abs(reachedMax)) < MathF.Min(Passage, wanted - 0.5f);
    }

    private static float Side(DoorShape shape, List<(Vector3, Vector3, Vector3)> triangles, float limit)
    {
        float wanted = MathF.Abs(limit);
        if (wanted < FirstAngle)
            return limit;
        float sign = MathF.Sign(limit), reached = 0f;
        for (float a = FirstAngle; ; a += Step)
        {
            float angle = MathF.Min(a, wanted);
            if (Hits(shape, triangles, sign * angle))
                return sign * reached;
            reached = angle;
            if (angle >= wanted)
                return limit;
        }
    }

    private const float MinFurniture = 0.4f;
    public const float MaxFurniture = 4f;
    private const float FurnitureReach = 1.2f;

    // The box of a separately placed object as a collision mesh (in the object's space), when it is furniture the door
    // can run into: neither tiny (pushed away) nor big (a building piece), standing low enough (not a hanging lamp), and
    // not around the hinge (a door frame).
    public static CollisionMesh? Furniture(Vector3 boundsMin, Vector3 boundsMax, Matrix4x4 transform, Vector3 hinge, float floor)
    {
        var size = boundsMax - boundsMin;
        float largest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (largest < MinFurniture || largest > MaxFurniture || !Matrix4x4.Invert(transform, out var toObject))
            return null;
        var local = Vector3.Transform(hinge, toObject);
        if (Vector3.Clamp(local, boundsMin - new Vector3(0.05f), boundsMax + new Vector3(0.05f)) == local)
            return null;
        float bottom = float.MaxValue;
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3((i & 1) == 0 ? boundsMin.X : boundsMax.X, (i & 2) == 0 ? boundsMin.Y : boundsMax.Y,
                (i & 4) == 0 ? boundsMin.Z : boundsMax.Z);
            bottom = MathF.Min(bottom, Vector3.Transform(corners[i], transform).Z);
        }
        if (bottom > floor + FurnitureReach)
            return null;
        return new CollisionMesh
        {
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
            Vertices = corners,
            // Sides only: tops and bottoms are ignored anyway.
            Indices = [0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5],
            Materials = new byte[8],
        };
    }

    // The collision triangles in the door's space, around the circle its leaf sweeps.
    private static List<(Vector3, Vector3, Vector3)> Near(DoorShape shape, Matrix4x4 toDoor, IEnumerable<PlacedCollision> collisions)
    {
        float reach = shape.Width + Search;
        var min = new Vector3(-reach, -reach, shape.Bottom);
        var max = new Vector3(reach, reach, shape.Bottom + shape.Height);
        var found = new List<(Vector3, Vector3, Vector3)>();
        foreach (var placed in collisions)
        {
            var mesh = placed.Mesh;
            var toLocal = placed.Transform * toDoor;
            var vertices = mesh.Vertices;
            var indices = mesh.Indices;
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                var a = Vector3.Transform(vertices[indices[i]], toLocal);
                var b = Vector3.Transform(vertices[indices[i + 1]], toLocal);
                var c = Vector3.Transform(vertices[indices[i + 2]], toLocal);
                var lo = Vector3.Min(a, Vector3.Min(b, c));
                var hi = Vector3.Max(a, Vector3.Max(b, c));
                if (hi.X < min.X || lo.X > max.X || hi.Y < min.Y || lo.Y > max.Y || hi.Z < min.Z || lo.Z > max.Z)
                    continue;
                // Floors, ceilings and lintels do not stop a leaf turning around a vertical hinge; a surface in the
                // closed door's own place is the door (or a blocker the game removes with it).
                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                if (float.IsNaN(normal.X) || MathF.Abs(normal.Z) > 0.7f
                    || MathF.Abs(normal.Y) > 0.9f && MathF.Max(MathF.Abs(lo.Y), MathF.Abs(hi.Y)) < ClosedSlab)
                    continue;
                found.Add((a, b, c));
            }
        }
        return found;
    }

    private static bool Hits(DoorShape shape, List<(Vector3 A, Vector3 B, Vector3 C)> triangles, float angle)
    {
        float radians = angle * MathF.PI / 180f;
        var along = new Vector3(shape.Side * MathF.Cos(radians), shape.Side * MathF.Sin(radians), 0f);
        float near = MathF.Max(0.08f, shape.Width * 0.1f), far = shape.Width - EdgeGap;
        float bottom = shape.Bottom + MathF.Min(BottomGap, shape.Height * 0.2f);
        float top = shape.Bottom + shape.Height - MathF.Min(TopGap, shape.Height * 0.2f);
        var p0 = along * near + new Vector3(0, 0, bottom);
        var p1 = along * far + new Vector3(0, 0, bottom);
        var p2 = along * far + new Vector3(0, 0, top);
        var p3 = along * near + new Vector3(0, 0, top);
        foreach (var (a, b, c) in triangles)
        {
            if (TrianglesCross(p0, p1, p2, a, b, c) || TrianglesCross(p0, p2, p3, a, b, c))
                return true;
        }
        return false;
    }

    // Two triangles cross when an edge of one goes through the other.
    private static bool TrianglesCross(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2) =>
        SegmentHits(a0, a1, b0, b1, b2) || SegmentHits(a1, a2, b0, b1, b2) || SegmentHits(a2, a0, b0, b1, b2)
        || SegmentHits(b0, b1, a0, a1, a2) || SegmentHits(b1, b2, a0, a1, a2) || SegmentHits(b2, b0, a0, a1, a2);

    private static bool SegmentHits(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Vector3 c)
    {
        var direction = to - from;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        float inv = 1f / det;
        var s = from - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f)
            return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0f || u + v > 1f)
            return false;
        float t = Vector3.Dot(e2, q) * inv;
        return t >= 0f && t <= 1f;
    }
}
