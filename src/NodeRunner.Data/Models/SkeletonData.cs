using System.Numerics;

namespace NodeRunner.Data.Models;

// The bones of a model in their default pose: each one relative to its parent (rotation, position, scale), and where
// that puts it in the model.
public sealed class SkeletonData
{
    // Number given to each bone by the game (animations name bones by it), e.g. 0 for the root, 31086 for the head.
    public required ushort[] Tags { get; init; }

    // Index of each bone's parent, -1 for the root.
    public required int[] Parents { get; init; }

    public required Quaternion[] Rotations { get; init; }
    public required Vector3[] Translations { get; init; }
    public required Vector3[] Scales { get; init; }

    // From bone to model, in the default pose.
    public required Matrix4x4[] BindPose { get; init; }

    public int Count => Tags.Length;

    private Matrix4x4[]? _inverseBindPose;
    private Dictionary<ushort, int>? _byTag;
    private int[]? _order;

    // From model to bone, in the default pose (a vertex placed in the default pose, brought back to its bone).
    public Matrix4x4[] InverseBindPose => _inverseBindPose ??= BindPose.Select(m => Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();

    public int IndexOf(ushort tag)
    {
        _byTag ??= BuildTags();
        return _byTag.TryGetValue(tag, out int index) ? index : -1;
    }

    // Bones ordered so that each one comes after its parent (they are not always stored that way).
    public int[] ParentsFirst => _order ??= BuildOrder();

    private Dictionary<ushort, int> BuildTags()
    {
        var byTag = new Dictionary<ushort, int>();
        for (int i = 0; i < Tags.Length; i++)
            byTag.TryAdd(Tags[i], i);
        return byTag;
    }

    private int[] BuildOrder()
    {
        var order = new List<int>(Count);
        var state = new byte[Count]; // 0 not placed, 1 being placed, 2 placed
        for (int i = 0; i < Count; i++)
            Place(i);
        return order.ToArray();

        void Place(int bone)
        {
            // Climb to the first placed ancestor, then place the chain downwards (a loop is cut where it closes).
            var chain = new List<int>();
            for (int b = bone; b >= 0 && b < Count && state[b] == 0; b = Parents[b])
            {
                state[b] = 1;
                chain.Add(b);
            }
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                state[chain[k]] = 2;
                order.Add(chain[k]);
            }
        }
    }
}

// Bends a skeleton with a clip: the matrices that take each vertex from the default pose to the animated one.
public static class SkeletonAnimator
{
    // Roll bones of the thighs, which the game turns with their thigh by an expression.
    private static readonly (ushort Roll, ushort Thigh)[] FollowingBones = [(23639, 58271), (6442, 51826)];

    // skin: one matrix per bone (vertex in the default pose -> animated, in the model). clip null: default pose.
    // Movement of the whole character (root motion) is left out: it stays on its spot.
    public static void Pose(SkeletonData skeleton, AnimationClip? clip, float time, Span<Matrix4x4> skin)
    {
        int count = skeleton.Count;
        var rotations = skeleton.Rotations.ToArray();
        var translations = skeleton.Translations.ToArray();
        var scales = skeleton.Scales.ToArray();
        if (clip is not null)
        {
            foreach (var part in clip.Parts)
            {
                var animation = part.Animation;
                float frame = animation.FrameAt(part.TimeAt(time));
                foreach (var track in animation.Tracks)
                {
                    int bone = skeleton.IndexOf(track.BoneTag);
                    if (bone < 0)
                        continue;
                    switch ((AnimationTrackKind)track.Kind)
                    {
                        case AnimationTrackKind.BoneTranslation:
                            var t = track.Sample(frame);
                            translations[bone] = new Vector3(t.X, t.Y, t.Z);
                            break;
                        case AnimationTrackKind.BoneRotation:
                            rotations[bone] = track.SampleRotation(frame);
                            break;
                        case AnimationTrackKind.BoneScale:
                            var s = track.Sample(frame);
                            scales[bone] = new Vector3(s.X, s.Y, s.Z);
                            break;
                    }
                }
            }
            foreach (var (roll, thigh) in FollowingBones)
            {
                int r = skeleton.IndexOf(roll), t = skeleton.IndexOf(thigh);
                if (r >= 0 && t >= 0 && skeleton.Parents[r] != t)
                    rotations[r] = rotations[t];
            }
        }

        var world = new Matrix4x4[count];
        var inverse = skeleton.InverseBindPose;
        foreach (int bone in skeleton.ParentsFirst)
        {
            var local = Matrix4x4.CreateScale(scales[bone]) * Matrix4x4.CreateFromQuaternion(rotations[bone])
                * Matrix4x4.CreateTranslation(translations[bone]);
            int parent = skeleton.Parents[bone];
            world[bone] = parent >= 0 && parent < count ? local * world[parent] : local;
        }
        for (int bone = 0; bone < count && bone < skin.Length; bone++)
            skin[bone] = inverse[bone] * world[bone];
    }
}
