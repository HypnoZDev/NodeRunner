using System.Numerics;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class GizmoMathTests
{
    [Fact]
    public void Finds_the_point_of_an_axis_closest_to_a_ray()
    {
        // Ray going down the Z axis through x = 3: closest point of the X axis is x = 3.
        float? t = GizmoMath.ClosestAxisParameter(Vector3.Zero, Vector3.UnitX, new Vector3(3f, 0f, 10f), -Vector3.UnitZ);
        Assert.NotNull(t);
        Assert.Equal(3f, t.Value, 0.001f);
    }

    [Fact]
    public void Ignores_a_ray_parallel_to_the_axis()
    {
        Assert.Null(GizmoMath.ClosestAxisParameter(Vector3.Zero, Vector3.UnitX, new Vector3(0f, 0f, 5f), Vector3.UnitX));
    }

    [Fact]
    public void Intersects_a_plane_in_front_only()
    {
        var hit = GizmoMath.IntersectPlane(new Vector3(1f, 2f, 10f), -Vector3.UnitZ, Vector3.Zero, Vector3.UnitZ);
        Assert.Equal(new Vector3(1f, 2f, 0f), hit);
        Assert.Null(GizmoMath.IntersectPlane(new Vector3(1f, 2f, 10f), Vector3.UnitZ, Vector3.Zero, Vector3.UnitZ));
    }

    [Fact]
    public void Measures_angles_counter_clockwise_around_an_axis()
    {
        float a = GizmoMath.AngleAround(Vector3.UnitX, Vector3.UnitZ);
        float b = GizmoMath.AngleAround(Vector3.UnitY, Vector3.UnitZ);
        Assert.Equal(MathF.PI / 2f, GizmoMath.WrapAngle(b - a), 0.001f);
    }

    [Fact]
    public void Wraps_and_snaps()
    {
        Assert.Equal(-MathF.PI / 2f, GizmoMath.WrapAngle(1.5f * MathF.PI), 0.001f);
        Assert.Equal(1.5f, GizmoMath.Snap(1.37f, 0.5f), 0.001f);
        Assert.Equal(1.37f, GizmoMath.Snap(1.37f, 0f), 0.001f);
    }

    [Fact]
    public void Screen_helpers()
    {
        Assert.Equal(2f, GizmoMath.DistanceToSegment(new Vector2(5f, 2f), Vector2.Zero, new Vector2(10f, 0f)), 0.001f);
        Assert.Equal(5f, GizmoMath.DistanceToSegment(new Vector2(13f, 4f), Vector2.Zero, new Vector2(10f, 0f)), 0.001f);
        var (a, b, c, d) = (Vector2.Zero, new Vector2(10f, 0f), new Vector2(10f, 10f), new Vector2(0f, 10f));
        Assert.True(GizmoMath.InsideQuad(new Vector2(5f, 5f), a, b, c, d));
        Assert.True(GizmoMath.InsideQuad(new Vector2(5f, 5f), d, c, b, a));
        Assert.False(GizmoMath.InsideQuad(new Vector2(15f, 5f), a, b, c, d));
    }
}
