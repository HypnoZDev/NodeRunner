using System.Numerics;

namespace NodeRunner.Data.Models;

// What a track of an animation moves.
public enum AnimationTrackKind : byte
{
    BoneTranslation = 0,
    BoneRotation = 1,
    BoneScale = 2,
    // Movement of the whole character (root motion), added to its position / heading.
    MoverTranslation = 5,
    MoverRotation = 6,
    CameraTranslation = 7,
    CameraRotation = 8,
}

// One value per frame for one bone (by tag) and one kind of track. Rotations are quaternions (x, y, z, w).
public sealed class AnimationTrack
{
    public required ushort BoneTag { get; init; }

    // AnimationTrackKind for the known ones; other numbers are facial and camera tracks.
    public required byte Kind { get; init; }

    public required Vector4[] Frames { get; init; }

    public bool IsRotation => Kind is (byte)AnimationTrackKind.BoneRotation or (byte)AnimationTrackKind.MoverRotation or (byte)AnimationTrackKind.CameraRotation;

    public Vector4 Sample(float frame)
    {
        int last = Frames.Length - 1;
        if (last <= 0)
            return last == 0 ? Frames[0] : Vector4.Zero;
        frame = Math.Clamp(frame, 0f, last);
        int f0 = Math.Min((int)frame, last - 1);
        float t = frame - f0;
        var a = Frames[f0];
        var b = Frames[f0 + 1];
        if (!IsRotation)
            return Vector4.Lerp(a, b, t);
        if (Vector4.Dot(a, b) < 0f)
            b = -b;
        var q = Vector4.Lerp(a, b, t);
        float length = q.Length();
        return length > 1e-6f ? q / length : new Vector4(0f, 0f, 0f, 1f);
    }

    public Quaternion SampleRotation(float frame)
    {
        var v = Sample(frame);
        return new Quaternion(v.X, v.Y, v.Z, v.W);
    }
}

// An animation of a clip dictionary, decoded: frames evenly spread over its duration.
public sealed class AnimationData
{
    public required int FrameCount { get; init; }

    public required float Duration { get; init; }

    public required IReadOnlyList<AnimationTrack> Tracks { get; init; }

    // Frame number (with fraction) at a time in seconds of the animation.
    public float FrameAt(float time) =>
        Duration > 0f && FrameCount > 1 ? Math.Clamp(time / Duration, 0f, 1f) * (FrameCount - 1) : 0f;
}

// A piece of an animation, played from Start to End (seconds of the animation) at Rate.
public sealed record ClipPart(AnimationData Animation, float Start, float End, float Rate)
{
    public float Length => Rate > 0f ? Math.Max(End - Start, 0f) / Rate : 0f;

    // Time in the animation at a time in seconds of the clip, looping.
    public float TimeAt(float clipTime)
    {
        float span = End - Start;
        if (span <= 0f)
            return Start;
        float t = clipTime * Rate % span;
        return Start + (t < 0f ? t + span : t);
    }
}

// A named clip (.ycd entry, e.g. "base"): one animation, or several played together (body and face).
public sealed class AnimationClip
{
    public required uint NameHash { get; init; }

    // Full name in the file ("pack:/amb@world_human_smoking@male@male_a@base/base.clip"), null when absent.
    public string? Name { get; init; }

    public required IReadOnlyList<ClipPart> Parts { get; init; }

    public float Duration => Parts.Count == 0 ? 0f : Parts.Max(p => p.Length);

    // Where the clip moves the whole object at a time: its tracks of these kinds on the root (5 / 6: a ped or a prop,
    // 7 / 8: a cutscene camera), null for a value it has no track for.
    public (Vector3? Position, Quaternion? Rotation) Root(float time, AnimationTrackKind positionKind, AnimationTrackKind rotationKind)
    {
        Vector3? position = null;
        Quaternion? rotation = null;
        foreach (var part in Parts)
        {
            float frame = part.Animation.FrameAt(part.TimeAt(time));
            foreach (var track in part.Animation.Tracks)
            {
                if (track.BoneTag != 0)
                    continue;
                if (track.Kind == (byte)positionKind)
                {
                    var v = track.Sample(frame);
                    position = new Vector3(v.X, v.Y, v.Z);
                }
                else if (track.Kind == (byte)rotationKind)
                    rotation = Quaternion.Normalize(track.SampleRotation(frame));
            }
        }
        return (position, rotation);
    }
}

// Reads the clips of a .ycd file, by name hash.
public interface IAnimationReader
{
    IReadOnlyDictionary<uint, AnimationClip> ReadClips(string path);
}
