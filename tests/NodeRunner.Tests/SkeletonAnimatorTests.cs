using System.Numerics;
using NodeRunner.Data.Models;
using Xunit;

namespace NodeRunner.Tests;

public class SkeletonAnimatorTests
{
    // Root at the origin, child 1 m up the root's Z.
    private static SkeletonData TwoBones()
    {
        var bind = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, 0f, 1f) };
        return new SkeletonData
        {
            Tags = [0, 100],
            Parents = [-1, 0],
            Rotations = [Quaternion.Identity, Quaternion.Identity],
            Translations = [Vector3.Zero, new Vector3(0f, 0f, 1f)],
            Scales = [Vector3.One, Vector3.One],
            BindPose = bind,
        };
    }

    private static AnimationClip TurnChild(params Quaternion[] frames) => new()
    {
        NameHash = 1,
        Parts =
        [
            new ClipPart(new AnimationData
            {
                FrameCount = frames.Length,
                Duration = frames.Length - 1,
                Tracks =
                [
                    new AnimationTrack
                    {
                        BoneTag = 100,
                        Kind = (byte)AnimationTrackKind.BoneRotation,
                        Frames = frames.Select(q => new Vector4(q.X, q.Y, q.Z, q.W)).ToArray(),
                    },
                ],
            }, 0f, frames.Length - 1, 1f),
        ],
    };

    [Fact]
    public void DefaultPoseLeavesVerticesWhereTheyAre()
    {
        var skin = new Matrix4x4[2];
        SkeletonAnimator.Pose(TwoBones(), null, 0f, skin);
        var vertex = new Vector3(0.3f, 0f, 1.5f);
        Assert.True(Vector3.Distance(vertex, Vector3.Transform(vertex, skin[1])) < 1e-5f);
    }

    [Fact]
    public void RotatedBoneTurnsItsVerticesAroundItself()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
        var skin = new Matrix4x4[2];
        SkeletonAnimator.Pose(TwoBones(), TurnChild(quarter, quarter), 0.5f, skin);
        // 0.5 m above the child bone, turned a quarter around X: 0.5 m along -Y of the bone, still 1 m up.
        var moved = Vector3.Transform(new Vector3(0f, 0f, 1.5f), skin[1]);
        Assert.True(Vector3.Distance(new Vector3(0f, -0.5f, 1f), moved) < 1e-5f, moved.ToString());
    }

    [Fact]
    public void RotationsAreBlendedBetweenFrames()
    {
        var half = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var track = TurnChild(Quaternion.Identity, half).Parts[0].Animation.Tracks[0];
        var q = track.SampleRotation(0.5f);
        Assert.Equal(MathF.PI / 4f, 2f * MathF.Acos(q.W), 3);
    }

    [Fact]
    public void ClipPartLoops()
    {
        var part = new ClipPart(new AnimationData { FrameCount = 2, Duration = 4f, Tracks = [] }, 1f, 3f, 1f);
        Assert.Equal(2f, part.Length);
        Assert.Equal(1.5f, part.TimeAt(0.5f), 4);
        Assert.Equal(1.5f, part.TimeAt(2.5f), 4);
    }
}
