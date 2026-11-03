using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

/// <summary>Where an object is, how it is turned (world orientation) and scaled.</summary>
public readonly record struct Placement(Vector3 Position, Quaternion Orientation, Vector3 Scale)
{
    public static Placement Of(MapEntity entity) => new(entity.Position, entity.Orientation, entity.Scale);

    public void ApplyTo(MapEntity entity) => entity.SetPlacement(Position, Orientation, Scale);
}

/// <summary>An object moved, turned or scaled.</summary>
public sealed class PlacementEdit(MapEntity entity, Placement before, Placement after, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => entity;
    public Placement Before { get; } = before;
    public Placement After { get; } = after;

    public void Undo() => Before.ApplyTo(entity);
    public void Redo() => After.ApplyTo(entity);
}

/// <summary>Several objects moved, turned or scaled together (one undo step); the last one is the main one.</summary>
public sealed class GroupPlacementEdit(IReadOnlyList<MapEntity> entities, IReadOnlyList<Placement> before, IReadOnlyList<Placement> after, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => entities[^1];
    public IEnumerable<MapEntity> Entities => entities;
    public IReadOnlyList<MapEntity> Selection => entities;

    public void Undo() => Apply(before);
    public void Redo() => Apply(after);

    private void Apply(IReadOnlyList<Placement> placements)
    {
        for (int i = 0; i < entities.Count; i++)
            placements[i].ApplyTo(entities[i]);
    }
}

/// <summary>
/// Moving, turning and scaling a group of objects around its centre (the middle of the box around their positions):
/// each object keeps its place in the group.
/// </summary>
public static class GroupTransform
{
    public static Vector3 Pivot(IEnumerable<Vector3> positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return min.X <= max.X ? (min + max) * 0.5f : Vector3.Zero;
    }

    public static Placement Move(Placement start, Vector3 delta) => start with { Position = start.Position + delta };

    /// <summary>Turns the object by <paramref name="turn"/> (world axes) around <paramref name="pivot"/>.</summary>
    public static Placement Rotate(Placement start, Vector3 pivot, Quaternion turn) => new(
        pivot + Vector3.Transform(start.Position - pivot, turn),
        Quaternion.Normalize(Quaternion.Concatenate(start.Orientation, turn)),
        start.Scale);

    /// <summary>
    /// Scales the group around <paramref name="pivot"/>: distances along X and Y by <paramref name="horizontal"/>, along Z
    /// by <paramref name="vertical"/>; each object's own scale likewise (the game stores one horizontal scale, one vertical).
    /// </summary>
    public static Placement Scale(Placement start, Vector3 pivot, float horizontal, float vertical)
    {
        var factors = new Vector3(horizontal, horizontal, vertical);
        return new Placement(pivot + (start.Position - pivot) * factors, start.Orientation, start.Scale * factors);
    }
}

/// <summary>Rotation angles as shown in the interface: around X, then Y, then Z (world axes), in degrees.</summary>
public static class EulerAngles
{
    private const float ToDegrees = 180f / MathF.PI;

    public static Vector3 FromQuaternion(Quaternion q)
    {
        float x = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float y = MathF.Asin(Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f));
        float z = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
        return new Vector3(x, y, z) * ToDegrees + Vector3.Zero; // + 0 turns -0 into 0 (shown "-0.0" otherwise).
    }

    public static Quaternion ToQuaternion(Vector3 degrees)
    {
        var r = degrees / ToDegrees;
        return Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X));
    }
}
