using System.Numerics;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class SurfacePlacementTests
{
    private static readonly Vector3 ChairMin = new(-0.3f, -0.3f, 0f), ChairMax = new(0.3f, 0.3f, 1f);
    private static readonly Vector3 PaintingMin = new(-0.6f, -0.03f, -0.4f), PaintingMax = new(0.6f, 0.03f, 0.4f);

    [Fact]
    public void On_a_floor_the_object_stands_on_the_point_with_the_turn_added()
    {
        var pose = SurfacePlacement.Pose(new Vector3(10, 20, 5), Vector3.UnitZ, ChairMin with { Z = -0.2f }, ChairMax, 0f, 90f);
        Assert.False(pose.OnWall);
        Assert.False(pose.NeedsGround);
        Assert.Equal(5.2f, pose.Position.Z, 3);
        var front = Vector3.Transform(Vector3.UnitY, pose.Orientation);
        Assert.Equal(-1f, front.X, 3);
    }

    [Fact]
    public void A_flat_object_hangs_on_a_wall_facing_out()
    {
        // Wall facing +X, point 1.5 m high.
        var point = new Vector3(0, 0, 1.5f);
        var pose = SurfacePlacement.Pose(point, Vector3.UnitX, PaintingMin, PaintingMax, 0f, 0f);
        Assert.True(pose.OnWall);
        var front = Vector3.Transform(Vector3.UnitY, pose.Orientation);
        Assert.Equal(1f, front.X, 3);
        // Back of the painting on the wall, centred on the point.
        Assert.Equal(0.03f, pose.Position.X, 3);
        Assert.Equal(1.5f, pose.Position.Z, 3);
        var back = pose.Position + Vector3.Transform(new Vector3(0, PaintingMin.Y, 0), pose.Orientation);
        Assert.Equal(0f, back.X, 3);
    }

    [Fact]
    public void A_flat_object_turned_half_way_keeps_its_face_on_the_wall()
    {
        var pose = SurfacePlacement.Pose(Vector3.Zero, Vector3.UnitX, PaintingMin, PaintingMax, 0f, 180f);
        var touching = pose.Position + Vector3.Transform(new Vector3(0, PaintingMax.Y, 0), pose.Orientation);
        Assert.Equal(0f, touching.X, 3);
        Assert.True(pose.Position.X > 0f);
    }

    [Fact]
    public void A_bulky_object_pointed_at_a_wall_goes_on_the_ground_in_front_of_it()
    {
        var pose = SurfacePlacement.Pose(new Vector3(0, 0, 1.2f), -Vector3.UnitY, ChairMin, ChairMax, 0f, 0f);
        Assert.False(pose.OnWall);
        Assert.True(pose.NeedsGround);
        Assert.True(pose.Position.Y < -0.3f);
    }
}
