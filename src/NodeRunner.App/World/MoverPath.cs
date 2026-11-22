using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

// The way a clip carries its ped (its mover track), sampled: the angle turned since the clip's first image (counted
// on, past half a turn) and the position, in the frame of that first image (+Y ahead, as the models face).
internal sealed class MoverPath
{
    private const float Rate = 60f; // Samples per second.
    private const float TurnShare = 0.03f; // Of the whole turn: where it starts and ends.

    private readonly float[] _yaw;
    private readonly Vector2[] _position;
    private readonly float[] _height;

    public AnimationClip Clip { get; }

    public float Duration { get; }

    // Angle turned from the first image to the last (radians, positive to the left).
    public float TotalYaw => _yaw[^1];

    // Whether the clip turns its ped round at all.
    public bool Turns => MathF.Abs(TotalYaw) > 0.3f;

    // Times the turn starts and ends (0 for a clip that does not turn).
    public float TurnStart { get; }
    public float TurnEnd { get; }

    public MoverPath(AnimationClip clip)
    {
        Clip = clip;
        Duration = Math.Max(clip.Duration, 1f / Rate);
        int count = Math.Max(2, (int)MathF.Ceiling(Duration * Rate) + 1);
        _yaw = new float[count];
        _position = new Vector2[count];
        _height = new float[count];
        var (firstPosition, firstRotation) = Root(clip, 0f);
        float first = Yaw(firstRotation), previous = first, turned = 0f;
        for (int i = 0; i < count; i++)
        {
            var (position, rotation) = Root(clip, Math.Min(i / Rate, Duration - 0.001f));
            float yaw = Yaw(rotation);
            turned += Wrap(yaw - previous);
            previous = yaw;
            _yaw[i] = turned;
            var moved = position - firstPosition;
            _position[i] = Turn(new Vector2(moved.X, moved.Y), -first);
            _height[i] = moved.Z;
        }
        if (Turns)
        {
            TurnStart = TimeWhen(TotalYaw * TurnShare);
            TurnEnd = TimeWhen(TotalYaw * (1f - TurnShare));
        }
    }

    public float YawAt(float time) => Sample(_yaw, time, (a, b, t) => a + (b - a) * t);

    public Vector2 PositionAt(float time) => Sample(_position, time, Vector2.Lerp);

    // How far up the clip has carried its ped (climbing).
    public float HeightAt(float time) => Sample(_height, time, (a, b, t) => a + (b - a) * t);

    public float TotalHeight => _height[^1];

    // How far ahead it has carried it by its end.
    public float TotalReach => _position[^1].Y;

    // Speed the clip carries its ped at, at a time (metres per second).
    public float SpeedAt(float time)
    {
        const float Half = 0.1f;
        float from = Math.Max(time - Half, 0f), to = Math.Min(time + Half, Duration);
        return to > from ? Vector2.Distance(PositionAt(from), PositionAt(to)) / (to - from) : 0f;
    }

    // A vector turned by an angle (positive to the left).
    public static Vector2 Turn(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    public static float Wrap(float angle)
    {
        angle %= MathF.Tau;
        return angle > MathF.PI ? angle - MathF.Tau : angle < -MathF.PI ? angle + MathF.Tau : angle;
    }

    // First time the turned angle reaches a share of the whole turn.
    private float TimeWhen(float yaw)
    {
        for (int i = 0; i < _yaw.Length; i++)
        {
            if (MathF.Abs(_yaw[i]) >= MathF.Abs(yaw))
                return i / Rate;
        }
        return Duration;
    }

    private T Sample<T>(T[] values, float time, Func<T, T, float, T> lerp)
    {
        float at = Math.Clamp(time, 0f, Duration) * Rate;
        int i = Math.Min((int)at, values.Length - 1);
        int next = Math.Min(i + 1, values.Length - 1);
        return lerp(values[i], values[next], at - i);
    }

    private static (Vector3 Position, Quaternion Rotation) Root(AnimationClip clip, float time)
    {
        var (position, rotation) = clip.Root(time, AnimationTrackKind.MoverTranslation, AnimationTrackKind.MoverRotation);
        return (position ?? Vector3.Zero, rotation ?? Quaternion.Identity);
    }

    // Turn about the vertical axis (positive to the left).
    private static float Yaw(Quaternion q) => MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
}
