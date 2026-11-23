using System.Numerics;

namespace NodeRunner.Editor;

// Where an object being placed goes, from the surface under the mouse: standing on a floor, hung on a wall (flat
// objects: paintings, signs...), or on the ground in front of a wall or under a ceiling.
public readonly record struct PlacementPose(Vector3 Position, Quaternion Orientation, bool OnWall, bool NeedsGround);

public enum PlacementProblem
{
    None,
    Floating,
    Sinking,
    Overlapping,
}

public static class SurfacePlacement
{
    // Surfaces facing up more than this (cosine) are floors.
    public const float FloorSlope = 0.7f;

    public const float Step = 45f;
    public const float FineStep = 15f;

    // Metres between the bottom of an object and the ground below a corner before it counts as floating or sinking.
    public const float GroundTolerance = 0.25f;

    private const float CornerInset = 0.8f;

    // Thin objects (a painting, a sign, a shelf board): depth at most 35 cm and well under their width or height.
    public static bool IsFlat(Vector3 min, Vector3 max)
    {
        var size = max - min;
        return size.Y <= 0.35f && size.Y <= 0.3f * MathF.Max(size.X, size.Z);
    }

    // Turn around Z so that the front (+Y) of an object looks along the horizontal direction of normal.
    public static float FacingDegrees(Vector3 normal) => MathF.Atan2(-normal.X, normal.Y) * 180f / MathF.PI;

    public static Quaternion Turn(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    // point and normal: the surface under the mouse (normal towards the viewer); min / max: the object's box;
    // floorYaw: its turn when standing on a floor; turn: the user's rotation added to it (or to the wall's direction).
    public static PlacementPose Pose(Vector3 point, Vector3 normal, Vector3 min, Vector3 max, float floorYaw, float turn)
    {
        if (normal.Z >= FloorSlope)
            return new PlacementPose(point with { Z = point.Z - min.Z }, Turn(floorYaw + turn), false, false);

        var flat = new Vector2(normal.X, normal.Y);
        bool wall = flat.Length() > 0.1f && normal.Z > -FloorSlope;
        var outward = wall ? Vector2.Normalize(flat) : Vector2.Zero;
        if (wall && IsFlat(min, max))
        {
            // Back of the box against the wall, centred on the point. Only half turns: the back stays on the wall.
            bool flipped = MathF.Abs(MathF.IEEERemainder(turn, 360f)) >= 90f;
            var orientation = Turn(FacingDegrees(new Vector3(outward, 0f)) + (flipped ? 180f : 0f));
            var forward = Vector3.Transform(Vector3.UnitY, orientation);
            var position = point - forward * (flipped ? max.Y : min.Y);
            return new PlacementPose(position with { Z = point.Z - (min.Z + max.Z) * 0.5f }, orientation, true, false);
        }

        // Out of the wall by the object's reach, then down on the ground (the caller finds its height).
        float reach = MathF.Max(MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)), MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y)));
        var away = point + new Vector3(outward * (reach + 0.02f), 0f);
        float baseYaw = wall ? FacingDegrees(new Vector3(outward, 0f)) : floorYaw;
        return new PlacementPose(away, Turn(baseYaw + turn), false, true);
    }

    // Bottom corners of the placed box, moved a little towards its centre (so a wall touching it is not taken for the
    // ground).
    public static IEnumerable<Vector3> BottomCorners(Vector3 position, Quaternion orientation, Vector3 min, Vector3 max)
    {
        var center = new Vector2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f);
        foreach (var (x, y) in new[] { (min.X, min.Y), (max.X, min.Y), (min.X, max.Y), (max.X, max.Y) })
        {
            var local = center + (new Vector2(x, y) - center) * CornerInset;
            yield return position + Vector3.Transform(new Vector3(local, min.Z), orientation);
        }
    }

    // Whether an object standing at position rests on the ground: groundBelow(x, y, fromZ) gives the height of the
    // ground under a point, starting at fromZ, or null when nothing is known there.
    public static PlacementProblem CheckGround(Vector3 position, Quaternion orientation, Vector3 min, Vector3 max,
        Func<Vector2, float, float?> groundBelow)
    {
        float height = max.Z - min.Z;
        float from = GroundSnap.StartHeight(0f, height);
        bool floating = false, known = false;
        foreach (var corner in BottomCorners(position, orientation, min, max))
        {
            if (groundBelow(new Vector2(corner.X, corner.Y), corner.Z + from) is not { } ground)
            {
                floating = true;
                continue;
            }
            known = true;
            if (ground > corner.Z + GroundTolerance)
                return PlacementProblem.Sinking;
            if (ground < corner.Z - GroundTolerance)
                floating = true;
        }
        return known && floating ? PlacementProblem.Floating : PlacementProblem.None;
    }

    // Whether two world boxes overlap by more than margin on every axis.
    public static bool Overlap((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b, float margin) =>
        a.Min.X + margin < b.Max.X && b.Min.X + margin < a.Max.X
        && a.Min.Y + margin < b.Max.Y && b.Min.Y + margin < a.Max.Y
        && a.Min.Z + margin < b.Max.Z && b.Min.Z + margin < a.Max.Z;

    // World box of a box turned and placed.
    public static (Vector3 Min, Vector3 Max) WorldBox(Vector3 position, Quaternion orientation, Vector3 min, Vector3 max)
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var p = position + Vector3.Transform(corner, orientation);
            low = Vector3.Min(low, p);
            high = Vector3.Max(high, p);
        }
        return (low, high);
    }
}
