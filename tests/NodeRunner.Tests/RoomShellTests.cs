using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class RoomShellTests
{
    private static readonly Vector3 Room = new(6, 5, 3);

    [Fact]
    public void Every_face_turns_towards_the_inside()
    {
        var faces = RoomShell.Faces(Room, 1.2f, 2.3f);
        var middle = new Vector3(0, Room.Y / 2, Room.Z / 2);
        foreach (var face in faces)
        {
            var center = face.Origin + (face.U + face.V) * 0.5f;
            bool doorway = MathF.Abs(center.X) <= 0.61f && center.Y < RoomShell.Thickness + 0.01f && center.Z < 2.31f;
            // The sides of the doorway face its middle; the rest faces the middle of the room.
            var towards = doorway ? new Vector3(0, center.Y, 1.15f) - center : middle - center;
            Assert.True(Vector3.Dot(face.Normal, towards) > 0, $"face at {center} turned away ({face.Normal})");
        }
        // Floor, ceiling, 3 walls, the front wall in 3 pieces and the doorway's 3 sides.
        Assert.Equal(11, faces.Count);
        Assert.Equal(Room.X * Room.Y, faces.Where(f => f.Surface == RoomShell.Surface.Floor).Sum(f => Vector3.Cross(f.U, f.V).Length()), 3);
    }

    [Fact]
    public void Collision_is_a_closed_room_with_the_door_free()
    {
        var slabs = RoomShell.Slabs(Room, 1.2f, 2.3f);
        var (header, content) = ResourceFile.Read(YbnXml.Convert(RoomShell.CollisionXml(slabs, material: 1)));
        var mesh = YbnReader.Read(content)!;

        Assert.Equal(slabs.Count, mesh.Polygons.Distinct().Count());
        Assert.Equal(43, header.Version);
        // Nothing in the doorway; the floor reaches under it.
        foreach (var (min, max, _) in slabs)
            Assert.False(min.X < 0.6f && max.X > -0.6f && min.Z < 2.3f && max.Z > 0 && min.Y < 0.2f && max.Y > 0, $"slab {min}..{max} in the doorway");
        Assert.Contains(slabs, s => s.Floor && s.Min.Y <= 0 && s.Max.Z == 0);
        Assert.Equal(new Vector3(-3.2f, -0.2f, -0.2f), mesh.BoundsMin, new Vec3Comparer(0.01f));
    }

    private sealed class Vec3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
