using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

public enum OccluderIssueKind
{
    // Overlaps an interior (MLO): hides its rooms from each other and from the doors.
    InsideInterior,
    // Sticks out of the buildings: a hole in the scenery behind it.
    OutsideBuildings,
    // A road or a pavement goes through it.
    AcrossPath,
    // Too small to hide anything.
    TooSmall,
    // Mostly inside another box.
    Duplicate,
}

// Box: index in the checked list. Other: the interior's archetype hash (InsideInterior) or the other box (Duplicate,
// index in the checked list, or -1 - index in the other boxes).
public sealed record OccluderIssue(OccluderIssueKind Kind, int Box, long Other = 0);

// An object's box in world space: its local bounds and where it is placed.
public sealed class PlacedVolume
{
    private readonly Matrix4x4 _toLocal;

    public PlacedVolume(Matrix4x4 transform, Vector3 min, Vector3 max, uint hash = 0)
    {
        Matrix4x4.Invert(transform, out _toLocal);
        Min = min;
        Max = max;
        Hash = hash;
        var center = Vector3.Transform((min + max) / 2, transform);
        Center = new Vector2(center.X, center.Y);
        // Upper bound of the horizontal reach, scale included.
        Reach = Vector3.Distance(min, max) / 2 * MathF.Max(new Vector2(transform.M11, transform.M12).Length(), new Vector2(transform.M21, transform.M22).Length());
    }

    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public uint Hash { get; }
    public Vector2 Center { get; }
    public float Reach { get; }

    // Inside, with margin metres more on every side (less when negative).
    public bool Contains(Vector3 point, float margin)
    {
        var p = Vector3.Transform(point, _toLocal);
        return p.X >= Min.X - margin && p.X <= Max.X + margin
            && p.Y >= Min.Y - margin && p.Y <= Max.Y + margin
            && p.Z >= Min.Z - margin && p.Z <= Max.Z + margin;
    }

    // Inside or below: under a building is underground.
    public bool Covers(Vector3 point, float margin)
    {
        var p = Vector3.Transform(point, _toLocal);
        return p.X >= Min.X - margin && p.X <= Max.X + margin
            && p.Y >= Min.Y - margin && p.Y <= Max.Y + margin
            && p.Z <= Max.Z + margin;
    }
}

// Common mistakes in the occluder boxes made or changed by hand, found from sample points spread inside each box.
// Interiors: one volume per room.
public static class OccluderCheck
{
    private const int Samples = 6;
    private const float BuildingMargin = 1f;
    private const float InteriorInset = 0.5f;
    // Thinner boxes are walls: the rooms' boxes reach into the walls (and past them for L-shaped rooms).
    private const float WallThickness = 0.6f;
    private const float MaxOutside = 0.2f;
    private const float DuplicateShare = 0.8f;
    private const float MinFaceArea = 4f;

    public static List<OccluderIssue> Run(IReadOnlyList<OccluderBox> boxes, IReadOnlyList<OccluderBox> otherBoxes,
        IReadOnlyList<PlacedVolume> buildings, IReadOnlyList<PlacedVolume> interiors, IReadOnlyList<Vector3> pathPoints)
    {
        var issues = new List<OccluderIssue>();
        for (int b = 0; b < boxes.Count; b++)
        {
            var box = boxes[b];
            var points = SamplePoints(box);
            float reach = new Vector2(box.Size.X, box.Size.Y).Length() / 2;
            var center = new Vector2(box.Center.X, box.Center.Y);

            bool Near(PlacedVolume v) => Vector2.Distance(v.Center, center) <= v.Reach + reach;

            foreach (var interior in MathF.Min(box.Size.X, box.Size.Y) <= WallThickness ? [] : interiors)
            {
                if (Near(interior) && points.Any(p => interior.Contains(p, -InteriorInset)))
                {
                    issues.Add(new OccluderIssue(OccluderIssueKind.InsideInterior, b, interior.Hash));
                    break;
                }
            }

            var near = buildings.Where(Near).ToList();
            int outside = points.Count(p => !near.Any(v => v.Covers(p, BuildingMargin)));
            if (outside > MaxOutside * points.Length)
                issues.Add(new OccluderIssue(OccluderIssueKind.OutsideBuildings, b));

            float bottom = box.Center.Z - box.Size.Z / 2 - 2f;
            float top = box.Center.Z + box.Size.Z / 2;
            if (pathPoints.Any(p => p.Z >= bottom && p.Z <= top && Inside(box, p with { Z = box.Center.Z })))
                issues.Add(new OccluderIssue(OccluderIssueKind.AcrossPath, b));

            float face = MathF.Max(box.Size.X * box.Size.Y, MathF.Max(box.Size.X * box.Size.Z, box.Size.Y * box.Size.Z));
            if (face < MinFaceArea)
                issues.Add(new OccluderIssue(OccluderIssueKind.TooSmall, b));

            for (int o = -otherBoxes.Count; o < boxes.Count; o++)
            {
                if (o == b)
                    continue;
                var other = o >= 0 ? boxes[o] : otherBoxes[-1 - o];
                if (Vector2.Distance(new Vector2(other.Center.X, other.Center.Y), center) > reach + new Vector2(other.Size.X, other.Size.Y).Length() / 2)
                    continue;
                if (points.Count(p => Inside(other, p)) >= DuplicateShare * points.Length)
                {
                    issues.Add(new OccluderIssue(OccluderIssueKind.Duplicate, b, o));
                    break;
                }
            }
        }
        return issues;
    }

    // The rooms of an interior placed in the world (not "limbo", the outside); its whole box when none has bounds.
    public static IEnumerable<PlacedVolume> Rooms(MapEntity interior, Archetype archetype, Vector3 min, Vector3 max)
    {
        var rooms = archetype.InteriorRooms
            .Where(r => r.BoundsMin != r.BoundsMax && !r.Name.Equals("limbo", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rooms.Count == 0)
            return [new PlacedVolume(interior.Transform, min, max, interior.ArchetypeHash)];
        return rooms.Select(r => new PlacedVolume(interior.Transform, r.BoundsMin, r.BoundsMax, interior.ArchetypeHash));
    }

    public static bool Inside(OccluderBox box, Vector3 point)
    {
        var d = point - box.Center;
        float cos = MathF.Cos(box.Angle), sin = MathF.Sin(box.Angle);
        float x = d.X * cos + d.Y * sin;
        float y = -d.X * sin + d.Y * cos;
        return MathF.Abs(x) <= box.Size.X / 2 + 1e-3f && MathF.Abs(y) <= box.Size.Y / 2 + 1e-3f && MathF.Abs(d.Z) <= box.Size.Z / 2 + 1e-3f;
    }

    // The centres of a grid of cells filling the box.
    private static Vector3[] SamplePoints(OccluderBox box)
    {
        var x = new Vector3(MathF.Cos(box.Angle), MathF.Sin(box.Angle), 0);
        var y = new Vector3(-x.Y, x.X, 0);
        var points = new Vector3[Samples * Samples * Samples];
        int n = 0;
        for (int i = 0; i < Samples; i++)
        for (int j = 0; j < Samples; j++)
        for (int k = 0; k < Samples; k++)
        {
            points[n++] = box.Center
                          + x * (((i + 0.5f) / Samples - 0.5f) * box.Size.X)
                          + y * (((j + 0.5f) / Samples - 0.5f) * box.Size.Y)
                          + Vector3.UnitZ * (((k + 0.5f) / Samples - 0.5f) * box.Size.Z);
        }
        return points;
    }
}
