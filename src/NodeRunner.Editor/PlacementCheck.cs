using System.Numerics;

namespace NodeRunner.Editor;

// Whether a scenario point or road node stands on the collision: the game's own are within a few centimetres of it
// (measured: median 0, 95 % within 10 cm), so one far above it floats and one under a surface is buried.
public static class PlacementCheck
{
    public enum Kind
    {
        Floating,

        Buried,
    }

    // Highest gap allowed between a point and the ground under it (a seat point stands a bit higher).
    public const float MaxGap = 1f;
    public const float MaxSeatGap = 1.5f;

    // A surface this far above the point, with no ground close under it, buries it.
    public const float BuriedDepth = 3f;

    // Collision looked up around the point: this much above and below.
    public const float SearchHeight = 6f;

    // A point this high above the map's ground, inside a prop's box, stands on the prop (props carry their own
    // collision): measured on the game's points, those at the foot of a tree or a bush stay under it.
    public const float OnObjectHeight = 0.4f;

    // Height of the point above the ground under it (slightly into the ground counts), null without ground there.
    public static float? GroundGap(IReadOnlyList<PlacedCollision> collisions, Vector3 point) =>
        GroundSnap.HighestBelow(collisions, new Vector2(point.X, point.Y), point.Z + 0.5f) is { } ground ? point.Z - ground : null;

    // Null when the point stands on the ground, or when no collision is there to judge (not loaded, open sea).
    public static Kind? Classify(IReadOnlyList<PlacedCollision> collisions, Vector3 point, float maxGap = MaxGap)
    {
        if (collisions.Count == 0)
            return null;
        if (GroundGap(collisions, point) is { } gap && gap <= maxGap)
            return null;
        if (GroundSnap.HighestBelow(collisions, new Vector2(point.X, point.Y), point.Z + BuriedDepth) is { } above && above > point.Z + 0.5f)
            return Kind.Buried;
        return Kind.Floating;
    }
}
