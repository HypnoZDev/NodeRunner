using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class BuildingOpeningTests
{
    // A swinging door 1 m wide, its leaf going along +X from the hinge, 2.2 m high, 6 cm thick.
    private static readonly Archetype Door = new()
    {
        NameHash = 0xD00,
        BoundsMin = new Vector3(0, -0.03f, 0),
        BoundsMax = new Vector3(1, 0.03f, 2.2f),
        Flags = BuildingOpening.OpeningDoorTypeFlags,
        LodDistance = 60,
    };

    [Fact]
    public void Interior_goes_behind_the_wall_with_its_door_on_the_opening()
    {
        // A wall facing -X (the street is at -X), the opening's bottom at (10, 20, 5).
        var opening = new Opening(new Vector3(10, 20, 5), -Vector3.UnitX, 1f, 2.2f);
        var plan = BuildingOpening.Make(opening, BuildingOpening.DefaultRoomSize, 0xD00, Door, "v_ilev_test_door");

        var transform = Matrix4x4.CreateFromQuaternion(plan.Orientation) * Matrix4x4.CreateTranslation(plan.Position);
        // The room is inside the building (+X), the portal on the wall, facing the street.
        var roomCenter = Vector3.Transform((plan.Room.BoundsMin + plan.Room.BoundsMax) / 2, transform);
        Assert.True(roomCenter.X > 10.5f);
        var corners = plan.Portal.Corners.Select(c => Vector3.Transform(c, transform)).ToList();
        Assert.All(corners, c => Assert.Equal(10f, c.X, 3));
        Assert.Equal(5f, corners.Min(c => c.Z), 3);
        Assert.Equal(7.2f, corners.Max(c => c.Z), 3);
        Assert.Equal((1, 0, BuildingOpening.DoorPortalFlags), (plan.Portal.RoomFrom, plan.Portal.RoomTo, plan.Portal.Flags));
        Assert.True(InteriorCreation.FacesAsTheGame(plan.Portal, [plan.Room, plan.Room]));

        // The door fills the opening: its leaf from one side to the other, bottom on the floor, attached to the portal.
        var door = plan.Door!;
        Assert.Equal((-1, 0), (door.InteriorRoom, door.InteriorPortal));
        var hinge = Vector3.Transform(door.Position, transform);
        var tip = Vector3.Transform(door.Position + new Vector3(1, 0, 0), transform);
        Assert.Equal(10f, hinge.X, 3);
        Assert.Equal(5f, hinge.Z, 3);
        Assert.Equal(1f, MathF.Abs(hinge.Y - tip.Y), 3);
        Assert.Equal(20f, (hinge.Y + tip.Y) / 2, 3);
    }

    [Fact]
    public void Opening_follows_the_building_into_its_model()
    {
        var placement = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(100, 0, 3);
        Matrix4x4.Invert(placement, out var toModel);
        var world = new Opening(new Vector3(100, 5, 3), Vector3.UnitY, 1.2f, 2.3f);

        var local = BuildingOpening.ToModel(world, toModel);

        Assert.Equal(new Vector3(5, 0, 0), local.Bottom, new Vec3Comparer(1e-4f));
        Assert.Equal(new Vector3(1, 0, 0), local.Normal, new Vec3Comparer(1e-4f));
    }

    [Fact]
    public void Chooses_a_game_door_of_the_right_width()
    {
        Archetype Type(float width, uint flags = BuildingOpening.OpeningDoorTypeFlags) =>
            new() { NameHash = 1, BoundsMin = Vector3.Zero, BoundsMax = new Vector3(width, 0.05f, 2.2f), Flags = flags };
        var types = new (uint, string?, Archetype)[]
        {
            (1, "v_ilev_door_a", Type(0.9f)),
            (2, "v_ilev_door_b", Type(1.2f)),
            (3, "v_ilev_door_c", Type(1.15f, 0)),   // does not open
            (4, "prop_bench", Type(1.2f)),         // not a door
        };

        Assert.Equal((2u, "v_ilev_door_b"), BuildingOpening.ChooseDoor(types, 1.18f));
        Assert.Equal((1u, "v_ilev_door_a"), BuildingOpening.ChooseDoor(types, 0.8f));
        Assert.Null(BuildingOpening.ChooseDoor([], 1f));
    }

    private sealed class Vec3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
