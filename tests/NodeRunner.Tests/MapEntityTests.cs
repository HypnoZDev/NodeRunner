using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Tests;

public class MapEntityTests
{
    private static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

    [Fact]
    public void Ordinary_entities_store_the_inverse_rotation()
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero, StoredRotation = Quaternion.Inverse(Quarter) };

        var turned = Vector3.Transform(Vector3.UnitX, entity.Orientation);

        Assert.Equal(0f, turned.X, 4);
        Assert.Equal(1f, turned.Y, 4);
    }

    [Fact]
    public void Interior_instances_store_the_rotation_directly()
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero, StoredRotation = Quarter, IsInterior = true };

        Assert.Equal(Quarter, entity.Orientation);
    }

    [Theory]
    [InlineData(0x180000u, true)]    // Casts shadows: an ordinary object.
    [InlineData(0x1180000u, true)]   // Not drawn in reflections, but drawn normally.
    [InlineData(0x2180000u, false)]  // Only drawn in reflections (metro tunnel stand-ins).
    [InlineData(0x800000u, false)]   // Only drawn in shadows.
    public void Shadow_and_reflection_stand_ins_are_not_drawn(uint flags, bool drawn)
    {
        var entity = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero, Flags = flags };

        Assert.Equal(drawn, entity.IsDrawnInMainView);
    }

    [Fact]
    public void Shadow_proxy_archetypes_are_recognised()
    {
        Assert.True(new Archetype { NameHash = 1, Flags = 0x20000800 }.IsShadowOrReflectionProxy);
        Assert.False(new Archetype { NameHash = 1, Flags = 0x20C00000 }.IsShadowOrReflectionProxy);
    }

    [Fact]
    public void Transform_applies_scale_rotation_then_position()
    {
        var entity = new MapEntity
        {
            ArchetypeHash = 1,
            Position = new Vector3(100f, 200f, 30f),
            StoredRotation = Quaternion.Inverse(Quarter),
            Scale = new Vector3(2f, 2f, 1f),
        };

        var corner = Vector3.Transform(new Vector3(1f, 0f, 1f), entity.Transform);

        Assert.Equal(100f, corner.X, 3);
        Assert.Equal(202f, corner.Y, 3);
        Assert.Equal(31f, corner.Z, 3);
    }

    [Fact]
    public void Moving_an_object_of_an_interior_moves_it_in_every_instance()
    {
        var definition = new MapEntity { ArchetypeHash = 1, Position = new Vector3(1, 0, 0) };
        var first = new MapEntity { ArchetypeHash = 2, Position = new Vector3(100, 0, 0), StoredRotation = Quarter, IsInterior = true };
        var second = new MapEntity { ArchetypeHash = 2, Position = new Vector3(0, 500, 10), IsInterior = true };
        var inFirst = definition.PlaceInside(first);
        var inSecond = definition.PlaceInside(second);
        Assert.Equal(100f, inFirst.Position.X, 4);
        Assert.Equal(1f, inFirst.Position.Y, 4);

        // Two metres further along the first instance's X (world Y), turned a quarter, scaled.
        inFirst.SetPlacement(new Vector3(100, 3, 0), Quarter * inFirst.Orientation, new Vector3(2, 2, 1));

        Assert.Equal(3f, definition.Position.X, 4);
        Assert.Equal(0f, definition.Position.Y, 4);
        Assert.True(Vector3.Distance(new Vector3(3, 500, 10), inSecond.Position) < 1e-4f);
        Assert.Equal(new Vector3(2, 2, 1), inSecond.Scale);
        Assert.True(MathF.Abs(Quaternion.Dot(Quarter, inSecond.Orientation)) > 0.9999f);
        Assert.True(inSecond.CanDelete);
    }

    [Fact]
    public void Placed_in_an_interior_relative_to_its_instance()
    {
        var instance = new MapEntity { ArchetypeHash = 2, Position = new Vector3(100, 0, 0), StoredRotation = Quarter, IsInterior = true };
        var placed = new MapEntity { ArchetypeHash = 1, Position = new Vector3(100, 2, 1), StoredRotation = Quaternion.Inverse(Quarter) };

        var definition = NodeRunner.Editor.Adding.InteriorDefinition(placed, instance, 3);
        var copy = definition.PlaceInside(instance);

        Assert.True(Vector3.Distance(new Vector3(2, 0, 1), definition.Position) < 1e-4f);
        Assert.True(Vector3.Distance(placed.Position, copy.Position) < 1e-4f);
        Assert.True(MathF.Abs(Quaternion.Dot(placed.Orientation, copy.Orientation)) > 0.9999f);
        Assert.Equal(3, definition.InteriorRoom);
    }
}
