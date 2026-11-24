using System.Numerics;

namespace NodeRunner.Data.Recordings;

// One sample of a vehicle recording. Time in seconds; Steering in radians; Gas from -1 (reverse) to 1; Brake 0 to 1.
public readonly record struct VehicleRecordingFrame(
    float Time, Vector3 Position, Vector3 Velocity, Vector3 Right, Vector3 Forward,
    float Steering, float Gas, float Brake, bool Handbrake);

// Where a recorded vehicle is at a given time, and how it is driven.
public readonly record struct VehicleRecordingPose(
    Vector3 Position, Quaternion Rotation, Vector3 Velocity, float Steering, float Gas, float Brake, bool Handbrake)
{
    public float Speed => Velocity.Length();
}

// A vehicle recording (.yvr, "carrec"): the path a mission script replays for a vehicle. The file does not say which
// vehicle drives it. Group and Number come from the file name ("fam1001" = group "fam1", number 1), the way scripts
// ask for a recording.
public sealed class VehicleRecording
{
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required int Number { get; init; }
    public required IReadOnlyList<VehicleRecordingFrame> Frames { get; init; }

    public float Duration => Frames.Count == 0 ? 0f : Frames[^1].Time;

    // Splits a recording file name into its group and number (the last three digits).
    public static (string Group, int Number) SplitName(string name)
    {
        int digits = 0;
        while (digits < 3 && digits < name.Length - 1 && char.IsAsciiDigit(name[name.Length - 1 - digits]))
            digits++;
        if (digits < 3)
            return (name, 0);
        return (name[..^3], int.Parse(name.AsSpan(name.Length - 3)));
    }

    // The pose at a time (clamped to the recording), between the two nearest samples: position along a curve that
    // follows the recorded velocities, orientation blended.
    public VehicleRecordingPose Sample(float time)
    {
        if (Frames.Count == 0)
            return new VehicleRecordingPose(Vector3.Zero, Quaternion.Identity, Vector3.Zero, 0f, 0f, 0f, false);
        int next = FirstAfter(time);
        if (next == 0 || next >= Frames.Count)
        {
            var only = Frames[next == 0 ? 0 : Frames.Count - 1];
            return new VehicleRecordingPose(only.Position, Orientation(only), only.Velocity, only.Steering, only.Gas,
                only.Brake, only.Handbrake);
        }
        var a = Frames[next - 1];
        var b = Frames[next];
        float span = b.Time - a.Time;
        float t = span > 1e-4f ? Math.Clamp((time - a.Time) / span, 0f, 1f) : 0f;
        var position = Hermite(a.Position, a.Velocity * span, b.Position, b.Velocity * span, t);
        // A curve far from the straight line means the velocities do not match the positions (a teleport): go straight.
        if (Vector3.DistanceSquared(position, Vector3.Lerp(a.Position, b.Position, t)) > 4f + Vector3.DistanceSquared(a.Position, b.Position))
            position = Vector3.Lerp(a.Position, b.Position, t);
        return new VehicleRecordingPose(
            position,
            Quaternion.Slerp(Orientation(a), Orientation(b), t),
            Vector3.Lerp(a.Velocity, b.Velocity, t),
            float.Lerp(a.Steering, b.Steering, t),
            float.Lerp(a.Gas, b.Gas, t),
            float.Lerp(a.Brake, b.Brake, t),
            t < 0.5f ? a.Handbrake : b.Handbrake);
    }

    // The index of the first sample after the time.
    private int FirstAfter(float time)
    {
        int low = 0, high = Frames.Count;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (Frames[mid].Time <= time)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    // The vehicle's rotation: X = right, Y = forward, Z = up (as the game's vehicle models).
    public static Quaternion Orientation(in VehicleRecordingFrame frame)
    {
        var forward = SafeNormalize(frame.Forward, Vector3.UnitY);
        var right = SafeNormalize(frame.Right - forward * Vector3.Dot(frame.Right, forward), Vector3.UnitX);
        var up = Vector3.Cross(right, forward);
        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float length = v.Length();
        return length > 1e-4f && float.IsFinite(length) ? v / length : fallback;
    }

    private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
    }
}
