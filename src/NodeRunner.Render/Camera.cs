using System.Numerics;

namespace NodeRunner.Render;

// A free-flying perspective camera in GTA V world space: X = east, Y = north, Z = up (metres).
public sealed class Camera
{
    private const float MaxPitch = 1.55f; // Just under 90°, so the view never flips over.

    public Vector3 Position { get; set; }

    public float Yaw { get; set; }

    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }
    private float _pitch;

    public float FieldOfView { get; set; } = MathF.PI / 3f;

    public float NearPlane { get; set; } = 0.5f;

    public Vector3 Forward => new(
        MathF.Cos(_pitch) * MathF.Cos(Yaw),
        MathF.Cos(_pitch) * MathF.Sin(Yaw),
        MathF.Sin(_pitch));

    public Vector3 Right => new(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0f);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);

    // Right-handed perspective projection with an infinite far plane and reversed depth (near plane -> depth 1,
    // infinity -> depth 0), to match GraphicsDevice.
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

    public void LookAt(Vector3 target)
    {
        var dir = Vector3.Normalize(target - Position);
        Yaw = MathF.Atan2(dir.Y, dir.X);
        Pitch = MathF.Asin(Math.Clamp(dir.Z, -1f, 1f));
    }
}
