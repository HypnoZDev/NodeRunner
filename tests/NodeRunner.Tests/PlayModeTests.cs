using System.Numerics;
using NodeRunner.App.World;
using NodeRunner.Data.Collisions;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class PlayModeTests
{
    // A floor at z = 0, a 0.3 m step from x = 2, and a 3 m high wall across x = 5.
    private static PlayCollision Room()
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = vertices.Count;
            vertices.AddRange([a, b, c, d]);
            indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
        }
        Quad(new(-10, -10, 0), new(2, -10, 0), new(2, 10, 0), new(-10, 10, 0));
        Quad(new(2, -10, 0.3f), new(10, -10, 0.3f), new(10, 10, 0.3f), new(2, 10, 0.3f));
        Quad(new(2, -10, 0), new(2, 10, 0), new(2, 10, 0.3f), new(2, -10, 0.3f));
        Quad(new(5, -10, 0), new(5, 10, 0), new(5, 10, 3), new(5, -10, 3));
        // Past that wall, a 1.5 m block on the raised floor, from x = 7 to 9.
        Quad(new(7, -10, 0.3f), new(7, 10, 0.3f), new(7, 10, 1.8f), new(7, -10, 1.8f));
        Quad(new(7, -10, 1.8f), new(9, -10, 1.8f), new(9, 10, 1.8f), new(7, 10, 1.8f));
        Quad(new(9, -10, 0.3f), new(9, 10, 0.3f), new(9, 10, 1.8f), new(9, -10, 1.8f));
        var mesh = new CollisionMesh
        {
            BoundsMin = new Vector3(-10, -10, 0),
            BoundsMax = new Vector3(10, 10, 3),
            Vertices = vertices,
            Indices = indices,
            Materials = new byte[indices.Count / 3],
        };
        var collision = new PlayCollision();
        collision.Use([new PlacedCollision(mesh, Matrix4x4.Identity)], Vector3.Zero);
        return collision;
    }

    [Fact]
    public void Ground_is_the_highest_surface_not_above_the_reach()
    {
        var room = Room();
        Assert.Equal(0f, room.Ground(new Vector2(0, 0), 0.45f));
        // The step is climbed (0.3 m), the wall's top is never ground.
        Assert.Equal(0.3f, room.Ground(new Vector2(3, 0), 0.45f));
        Assert.Null(room.Ground(new Vector2(30, 0), 10f));
    }

    [Fact]
    public void A_wall_stops_the_body_and_a_step_does_not()
    {
        var room = Room();
        // Against the step (0.3 m high): not pushed back.
        Assert.Equal(new Vector3(1.9f, 0, 0), room.PushOut(new Vector3(1.9f, 0, 0)));
        // Into the wall: put back at the body's radius in front of it, still at the same y.
        var pushed = room.PushOut(new Vector3(4.9f, 1f, 0.3f));
        Assert.Equal(5f - PlayCollision.BodyRadius, pushed.X, 2);
        Assert.Equal(1f, pushed.Y, 4);
        Assert.Equal(0.3f, pushed.Z);
    }

    [Fact]
    public void Walking_into_a_wall_never_crosses_it()
    {
        var room = Room();
        var feet = new Vector3(3, 0, 0.3f);
        // Sprinting at 6 m/s for 2 s, in steps of a tenth of the body's radius.
        for (int i = 0; i < 400; i++)
            feet = room.PushOut(feet + new Vector3(0.03f, 0.01f, 0));
        Assert.InRange(feet.X, 4.6f, 5f - PlayCollision.BodyRadius + 0.01f);
        Assert.True(feet.Y > 3f); // Slid along it.
    }

    [Fact]
    public void A_ledge_is_found_on_top_of_a_block_not_on_a_bare_wall()
    {
        var room = Room();
        var ledge = room.Ledge(new Vector3(6, 0, 0.3f), Vector2.UnitX, 0.5f, 0.6f, 3.2f);
        Assert.NotNull(ledge);
        Assert.Equal(1.8f, ledge!.Value.Top, 3);
        Assert.Equal(1f, ledge.Value.Distance, 2);
        // The 3 m wall has no top to stand on: past it is the floor.
        Assert.Null(room.Ledge(new Vector3(4.5f, 0, 0.3f), Vector2.UnitX, 0.5f, 0.6f, 3.2f));
        // Too high for the clips.
        Assert.Null(room.Ledge(new Vector3(6, 0, 0.3f), Vector2.UnitX, 0.5f, 0.6f, 1.2f));
        // Facing away from the block: nothing.
        Assert.Null(room.Ledge(new Vector3(6, 0, 0.3f), -Vector2.UnitY, 0.5f, 0.6f, 3.2f));
    }

    [Fact]
    public void The_camera_stops_in_front_of_a_wall()
    {
        var room = Room();
        var share = room.Hit(new Vector3(4, 0, 1.5f), new Vector3(8, 0, 1.5f));
        Assert.NotNull(share);
        Assert.Equal(0.25f, share!.Value, 3);
        Assert.Null(room.Hit(new Vector3(0, 0, 1.5f), new Vector3(4, 0, 1.5f)));
    }
}
