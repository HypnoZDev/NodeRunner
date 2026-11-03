using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class GroundSnapTests
{
    private static readonly Archetype Crate = new()
    {
        NameHash = 1,
        BoundsMin = new Vector3(-1, -1, -0.5f),
        BoundsMax = new Vector3(1, 1, 1.5f),
    };

    private static MapEntity At(Vector3 position) => new() { ArchetypeHash = 1, Position = position };

    /// <summary>A flat square from (-50, -50) to (50, 50) at height <paramref name="z"/>.</summary>
    private static PlacedCollision Floor(float z, Matrix4x4? transform = null)
    {
        var builder = new CollisionMeshBuilder();
        builder.AddTriangle(new(-50, -50, z), new(50, -50, z), new(50, 50, z), 0);
        builder.AddTriangle(new(-50, -50, z), new(50, 50, z), new(-50, 50, z), 0);
        return new PlacedCollision(builder.Build()!, transform ?? Matrix4x4.Identity);
    }

    [Fact]
    public void Floating_object_comes_down_until_its_box_touches_the_ground()
    {
        var position = GroundSnap.Snap(At(new(3, 4, 20)), Crate, [Floor(10)]);
        // The bottom of the box is 0.5 m under the origin.
        Assert.Equal(new Vector3(3, 4, 10.5f), position);
    }

    [Fact]
    public void Half_buried_object_goes_up()
    {
        var position = GroundSnap.Snap(At(new(0, 0, 9.8f)), Crate, [Floor(10)]);
        Assert.Equal(10.5f, position!.Value.Z, 4);
    }

    [Fact]
    public void Building_is_not_lifted_onto_its_own_roof()
    {
        // A 20 m high building standing on the ground, whose collision includes its roof.
        var building = new Archetype { NameHash = 2, BoundsMin = new(-10, -10, 0), BoundsMax = new(10, 10, 20) };
        var entity = new MapEntity { ArchetypeHash = 2, Position = new(0, 0, 10) };
        var position = GroundSnap.Snap(entity, building, [Floor(10), Floor(30)]);
        Assert.Equal(10f, position!.Value.Z, 4);
    }

    [Fact]
    public void Surface_above_the_object_is_ignored()
    {
        // A roof 5 m above the top of the object, the ground below.
        var position = GroundSnap.Snap(At(new(0, 0, 20)), Crate, [Floor(10), Floor(30)]);
        Assert.Equal(10.5f, position!.Value.Z, 4);
    }

    [Fact]
    public void Highest_support_under_the_object_wins()
    {
        // A step: higher floor under only part of the object (one corner side).
        var builder = new CollisionMeshBuilder();
        builder.AddBox(new(0.5f, -5, 0), new(5, 5, 11), Matrix4x4.Identity, 0);
        var step = new PlacedCollision(builder.Build()!, Matrix4x4.Identity);
        var position = GroundSnap.Snap(At(new(0, 0, 20)), Crate, [Floor(10), step]);
        Assert.Equal(11.5f, position!.Value.Z, 4);
    }

    [Fact]
    public void No_collision_below_gives_nothing()
    {
        Assert.Null(GroundSnap.Snap(At(new(0, 0, 5)), Crate, [Floor(10)])); // buried deeper than it can rise
        Assert.Null(GroundSnap.Snap(At(new(500, 0, 50)), Crate, [Floor(10)]));
    }

    [Fact]
    public void Interior_collision_is_placed_by_its_transform()
    {
        // Floor at height 0 in the interior, the interior placed 100 m up.
        var floor = Floor(0, Matrix4x4.CreateTranslation(0, 0, 100));
        var position = GroundSnap.Snap(At(new(0, 0, 120)), Crate, [floor]);
        Assert.Equal(100.5f, position!.Value.Z, 4);
    }

    [Fact]
    public void Interior_box_comes_from_its_rooms()
    {
        // As in the game's files: no box for the interior type itself, only for its rooms.
        var mlo = new Archetype
        {
            NameHash = 3,
            Kind = ArchetypeKind.Interior,
            InteriorRooms = [new(Vector3.Zero, Vector3.Zero), new(new(-5, -5, 0), new(5, 5, 3)), new(new(5, -2, 0), new(9, 2, 6))],
        };
        var instance = new MapEntity { ArchetypeHash = 3, Position = new(100, 0, 20), IsInterior = true };
        var box = GroundSnap.WorldBox(instance, mlo);
        Assert.Equal((new Vector3(95, -5, 20), new Vector3(109, 5, 26)), box);
    }

    [Fact]
    public void Height_on_a_slope_is_interpolated()
    {
        var z = GroundSnap.HeightAt(new(0, 0, 0), new(10, 0, 10), new(0, 10, 0), new Vector2(5, 2));
        Assert.Equal(5f, z!.Value, 4);
        Assert.Null(GroundSnap.HeightAt(new(0, 0, 0), new(10, 0, 10), new(0, 10, 0), new Vector2(8, 8)));
    }

    [Fact]
    public void Wall_is_never_ground()
    {
        Assert.Null(GroundSnap.HeightAt(new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new Vector2(5, 0)));
    }
}
