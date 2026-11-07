using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class MloOccludersTests
{
    // A 10 x 10 x 4 m room (bounds 0.2 m into its 0.3 m thick walls), a door to the outside in its south wall; the
    // interior is turned a quarter turn and placed away from the origin.
    private static readonly InteriorRoom Limbo = new(Vector3.Zero, Vector3.Zero) { Name = "limbo" };
    private static readonly InteriorRoom Room = new(new Vector3(-5, -5, 0), new Vector3(5, 5, 4)) { Name = "room" };
    private static readonly InteriorPortal Door = new(1, 0, 0)
    {
        Corners = [new(-1, -4.8f, 0), new(1, -4.8f, 0), new(1, -4.8f, 2.5f), new(-1, -4.8f, 2.5f)],
    };

    private static readonly MapEntity Interior = new()
    {
        ArchetypeHash = 1,
        IsInterior = true,
        Position = new Vector3(100, 200, 30),
        StoredRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
    };

    // Inner and outer faces of the four walls.
    private static CollisionMesh Walls()
    {
        var points = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => points.AddRange([a, b, c, a, c, d]);
        foreach (float x in new[] { 4.8f, 5.1f })
        {
            Quad(new(x, -6, 0), new(x, 6, 0), new(x, 6, 4), new(x, -6, 4));
            Quad(new(-x, -6, 0), new(-x, 6, 0), new(-x, 6, 4), new(-x, -6, 4));
            Quad(new(-6, x, 0), new(6, x, 0), new(6, x, 4), new(-6, x, 4));
            Quad(new(-6, -x, 0), new(6, -x, 0), new(6, -x, 4), new(-6, -x, 4));
        }
        return new CollisionMesh
        {
            BoundsMin = new Vector3(-6, -6, 0),
            BoundsMax = new Vector3(6, 6, 4),
            Vertices = points,
            Indices = Enumerable.Range(0, points.Count).ToArray(),
            Materials = new byte[points.Count / 3],
        };
    }

    private static List<(Vector3 Center, OccluderBox Box)> Fit(params InteriorRoom[] rooms)
    {
        var archetype = new Archetype { NameHash = 1, Kind = ArchetypeKind.Interior, InteriorRooms = [Limbo, .. rooms], InteriorPortals = [Door] };
        Matrix4x4.Invert(Interior.Transform, out var toLocal);
        return MloOccluders.Fit(Interior, archetype, Walls()).Select(b => (Vector3.Transform(b.Center, toLocal), b)).ToList();
    }

    [Fact]
    public void Boxes_sit_in_the_outer_walls_and_leave_the_door_free()
    {
        var boxes = Fit(Room);
        Assert.NotEmpty(boxes);
        foreach (var (center, box) in boxes)
        {
            // Inside a wall, 4.8 to 5.1 m from the middle.
            float distance = MathF.Max(MathF.Abs(center.X), MathF.Abs(center.Y));
            Assert.InRange(distance, 4.8f, 5.1f);
            Assert.InRange(box.Size.Y, 0.1f, 0.3f);
            // Nothing over the door.
            bool southWall = center.Y < -4.5f;
            Assert.False(southWall && MathF.Abs(center.X) < 1f + box.Size.X / 2 && center.Z - box.Size.Z / 2 < 2.5f);
        }
        // Each of the four walls has some.
        Assert.Contains(boxes, b => b.Center.X > 4.5f);
        Assert.Contains(boxes, b => b.Center.X < -4.5f);
        Assert.Contains(boxes, b => b.Center.Y > 4.5f);
        Assert.Contains(boxes, b => b.Center.Y < -4.5f);
    }

    [Fact]
    public void Wall_between_two_rooms_gets_no_box()
    {
        var next = new InteriorRoom(new Vector3(5, -5, 0), new Vector3(15, 5, 4)) { Name = "next" };
        Assert.DoesNotContain(Fit(Room, next), b => b.Center.X > 4.5f && b.Center.X < 5.5f && MathF.Abs(b.Center.Y) < 4.5f);
    }
}
