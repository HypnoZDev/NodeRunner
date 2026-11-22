using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

// Where the feet are, and how they move, in the model (relative to the ped's mover, so the same for every clip).
internal readonly record struct Feet(Vector3 Left, Vector3 Right, Vector3 LeftSpeed, Vector3 RightSpeed)
{
    // Speeds count less than places: they mainly tell a foot coming forward from one going back.
    private const float SpeedWeight = 0.04f;

    public float Distance(Feet other) =>
        Vector3.DistanceSquared(Left, other.Left) + Vector3.DistanceSquared(Right, other.Right)
        + SpeedWeight * (Vector3.DistanceSquared(LeftSpeed, other.LeftSpeed) + Vector3.DistanceSquared(RightSpeed, other.RightSpeed));
}

// The feet of a skeleton along each clip, sampled once, to go from one clip into another on the same step (the time
// of the next clip whose feet are closest to the feet now).
internal sealed class FootPoses
{
    private const float Rate = 30f; // Samples per second.
    private const float SpeedStep = 1f / Rate;
    // Bone tags of the feet (SKEL_L_Foot, SKEL_R_Foot).
    private const ushort LeftFoot = 14201;
    private const ushort RightFoot = 52301;

    private readonly SkeletonData _skeleton;
    private readonly int _left, _right;
    private readonly Dictionary<AnimationClip, Feet[]> _clips = new(ReferenceEqualityComparer.Instance);

    private FootPoses(SkeletonData skeleton, int left, int right)
    {
        (_skeleton, _left, _right) = (skeleton, left, right);
    }

    // Null for a skeleton without the feet bones.
    public static FootPoses? Build(SkeletonData skeleton, IEnumerable<AnimationClip> clips)
    {
        int left = skeleton.IndexOf(LeftFoot), right = skeleton.IndexOf(RightFoot);
        if (left < 0 || right < 0)
            return null;
        var poses = new FootPoses(skeleton, left, right);
        var pose = new Matrix4x4[skeleton.Count];
        foreach (var clip in clips.Distinct(ReferenceEqualityComparer.Instance).Cast<AnimationClip>())
        {
            int count = Math.Max(1, (int)MathF.Ceiling(clip.Duration * Rate));
            var samples = new Feet[count];
            for (int i = 0; i < count; i++)
            {
                float time = i / Rate;
                var (l, r) = poses.Place(clip, time, pose);
                var (nextL, nextR) = poses.Place(clip, Math.Min(time + SpeedStep, clip.Duration - 0.001f), pose);
                samples[i] = new Feet(l, r, (nextL - l) / SpeedStep, (nextR - r) / SpeedStep);
            }
            poses._clips[clip] = samples;
        }
        return poses;
    }

    // The feet in a pose drawn (one matrix per bone, as SkeletonAnimator gives).
    public (Vector3 Left, Vector3 Right)? Of(Matrix4x4[]? pose)
    {
        if (pose is null || pose.Length != _skeleton.Count)
            return null;
        return ((_skeleton.BindPose[_left] * pose[_left]).Translation, (_skeleton.BindPose[_right] * pose[_right]).Translation);
    }

    // The time of a clip between two times whose feet are closest to these; from when it cannot tell.
    public float Closest(AnimationClip clip, Feet now, float from, float to, out float distance)
    {
        distance = float.MaxValue;
        if (!_clips.TryGetValue(clip, out var samples))
            return from;
        float best = from;
        int first = Math.Clamp((int)MathF.Floor(from * Rate), 0, samples.Length - 1);
        int last = Math.Clamp((int)MathF.Ceiling(to * Rate), first, samples.Length - 1);
        for (int i = first; i <= last; i++)
        {
            float d = samples[i].Distance(now);
            if (d < distance)
                (distance, best) = (d, i / Rate);
        }
        return best;
    }

    private (Vector3, Vector3) Place(AnimationClip clip, float time, Matrix4x4[] pose)
    {
        SkeletonAnimator.Pose(_skeleton, clip, time, pose);
        return ((_skeleton.BindPose[_left] * pose[_left]).Translation, (_skeleton.BindPose[_right] * pose[_right]).Translation);
    }
}
