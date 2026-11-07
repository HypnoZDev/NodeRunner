using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class OcclusionBufferTests
{
    // Same as the app's camera: right-handed, infinite far plane, reversed depth.
    private static Matrix4x4 ViewProjection(Vector3 position, Vector3 forward, float aspect)
    {
        float yScale = 1f / MathF.Tan(MathF.PI / 6f);
        var projection = new Matrix4x4(yScale / aspect, 0, 0, 0, 0, yScale, 0, 0, 0, 0, 0, -1, 0, 0, 0.5f, 0);
        return Matrix4x4.CreateLookAt(position, position + forward, Vector3.UnitZ) * projection;
    }

    private static OcclusionBuffer View(Vector3 camera, Vector3 forward, float aspect, OccluderBox box)
    {
        var buffer = new OcclusionBuffer();
        buffer.Begin(ViewProjection(camera, forward, aspect), camera, aspect);
        var (vertices, indices) = OcclusionBuffer.BoxMesh(box);
        buffer.AddMesh(vertices, indices, twoSided: false);
        buffer.End();
        return buffer;
    }

    // Camera at the origin looking north (+Y); a 20 x 2 x 20 m wall 50 m ahead.
    private static OcclusionBuffer ViewWithWall() =>
        View(Vector3.Zero, Vector3.UnitY, 16f / 9f, new OccluderBox(0, new Vector3(0, 50, 0), new Vector3(20, 2, 20), 0f));

    [Fact]
    public void Object_behind_the_wall_is_hidden()
    {
        Assert.True(ViewWithWall().IsHidden(new Vector3(0, 120, 0), 3f));
    }

    [Fact]
    public void Object_in_front_beside_or_larger_than_the_wall_is_seen()
    {
        var buffer = ViewWithWall();
        Assert.False(buffer.IsHidden(new Vector3(0, 30, 0), 3f)); // In front.
        Assert.False(buffer.IsHidden(new Vector3(40, 120, 0), 3f)); // Beside.
        Assert.False(buffer.IsHidden(new Vector3(0, 120, 0), 40f)); // Sticks out around it.
        Assert.False(buffer.IsHidden(new Vector3(0, 50, 0), 4f)); // Around the wall itself.
    }

    [Fact]
    public void Wall_behind_the_camera_hides_nothing()
    {
        var buffer = View(new Vector3(0, 100, 0), Vector3.UnitY, 1f, new OccluderBox(0, new Vector3(0, 50, 0), new Vector3(20, 2, 20), 0f));
        Assert.False(buffer.IsHidden(new Vector3(0, 150, 0), 3f));
    }

    [Fact]
    public void Camera_inside_a_box_sees_out()
    {
        var buffer = View(Vector3.Zero, Vector3.UnitY, 1f, new OccluderBox(0, Vector3.Zero, new Vector3(10, 10, 10), 0.3f));
        Assert.False(buffer.IsHidden(new Vector3(0, 60, 0), 3f));
    }

    [Fact]
    public void Two_sided_wall_hides_from_both_sides()
    {
        Vector3[] wall = [new(-10, 50, -10), new(10, 50, -10), new(10, 50, 10), new(-10, 50, 10)];
        int[] indices = [0, 1, 2, 0, 2, 3];
        foreach (var (camera, behind) in new[] { (Vector3.Zero, new Vector3(0, 100, 0)), (new Vector3(0, 100, 0), Vector3.Zero) })
        {
            var buffer = new OcclusionBuffer();
            buffer.Begin(ViewProjection(camera, Vector3.Normalize(behind - camera), 1f), camera, 1f);
            buffer.AddMesh(wall, indices, twoSided: true);
            buffer.End();
            Assert.True(buffer.IsHidden(behind, 3f));
        }
    }
}
