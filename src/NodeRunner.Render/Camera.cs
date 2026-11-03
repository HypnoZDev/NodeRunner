using System.Numerics;

namespace NodeRunner.Render;

/// <summary>
/// A free-flying perspective camera in GTA V world space: X = east, Y = north, Z = up (metres).
/// </summary>
public sealed class Camera
{
    private const float MaxPitch = 1.55f; // Just under 90°, so the view never flips over.

    public Vector3 Position { get; set; }

    /// <summary>Horizontal angle in radians. 0 = looking towards +X (east), increases counter-clockwise.</summary>
    public float Yaw { get; set; }

    /// <summary>Vertical angle in radians. Positive = looking up.</summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }
    private float _pitch;

    /// <summary>Vertical field of view in radians.</summary>
    public float FieldOfView { get; set; } = MathF.PI / 3f;

    /// <summary>Distance of the near clipping plane. There is no far plane (infinite reversed-Z projection).</summary>
    public float NearPlane { get; set; } = 0.5f;

    public Vector3 Forward => new(
        MathF.Cos(_pitch) * MathF.Cos(Yaw),
        MathF.Cos(_pitch) * MathF.Sin(Yaw),
        MathF.Sin(_pitch));

    /// <summary>Horizontal right direction (always level, so strafing never changes altitude).</summary>
    public Vector3 Right => new(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0f);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);

    /// <summary>
    /// Right-handed perspective projection with an infinite far plane and reversed depth
    /// (near plane -> depth 1, infinity -> depth 0), to match <see cref="GraphicsDevice"/>.
    /// </summary>
    public Matrix4x4 Projection(float aspectRatio)
    {
        float yScale = 1f / MathF.Tan(FieldOfView * 0.5f);
        float xScale = yScale / aspectRatio;
        return new Matrix4x4(
            xScale, 0f, 0f, 0f,
            0f, yScale, 0f, 0f,
            0f, 0f, 0f, -1f,
            0f, 0f, NearPlane, 0f);
    }

    public Matrix4x4 ViewProjection(float aspectRatio) => View * Projection(aspectRatio);

    /// <summary>Points the camera at <paramref name="target"/> from its current position.</summary>
    public void LookAt(Vector3 target)
    {
        var dir = Vector3.Normalize(target - Position);
        Yaw = MathF.Atan2(dir.Y, dir.X);
        Pitch = MathF.Asin(Math.Clamp(dir.Z, -1f, 1f));
    }
}
