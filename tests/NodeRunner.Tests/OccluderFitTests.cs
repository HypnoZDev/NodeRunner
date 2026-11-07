using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class OccluderFitTests
{
    // A model whose box holds a car park around the building: 50 x 50 x 13 m.
    private static readonly Archetype Lot = new()
    {
        NameHash = 1,
        BoundsMin = new Vector3(-25, -25, 0),
        BoundsMax = new Vector3(25, 25, 13),
    };

    private static void Wall(CollisionMeshBuilder builder, Vector2 from, Vector2 to, float z0, float z1)
    {
        builder.AddTriangle(new(from, z0), new(to, z0), new(to, z1), 0);
        builder.AddTriangle(new(from, z0), new(to, z1), new(from, z1), 0);
    }

    private static void Roof(CollisionMeshBuilder b, Vector2 min, Vector2 max, float z)
    {
        b.AddTriangle(new(min, z), new(max.X, min.Y, z), new(max, z), 0);
        b.AddTriangle(new(min, z), new(max, z), new(min.X, max.Y, z), 0);
    }

    // The four walls and the roof of a building (world positions), with an optional opening of the given width in the
    // first wall.
    private static void Building(CollisionMeshBuilder b, Vector2 center, float x, float y, float h, float door = 0)
    {
        Roof(b, center - new Vector2(x / 2, y / 2), center + new Vector2(x / 2, y / 2), h);
        var c0 = center + new Vector2(-x / 2, -y / 2);
        var c1 = center + new Vector2(x / 2, -y / 2);
        var c2 = center + new Vector2(x / 2, y / 2);
        var c3 = center + new Vector2(-x / 2, y / 2);
        if (door > 0)
        {
            Wall(b, c0, new(center.X - door / 2, c0.Y), 0, h);
            Wall(b, new(center.X + door / 2, c0.Y), c1, 0, h);
        }
        else
            Wall(b, c0, c1, 0, h);
        Wall(b, c1, c2, 0, h);
        Wall(b, c2, c3, 0, h);
        Wall(b, c3, c0, 0, h);
    }

    private static PlacedCollision Ground(CollisionMeshBuilder b, Matrix4x4? transform = null)
    {
        b.AddTriangle(new(-25, -25, 0), new(25, -25, 0), new(25, 25, 0), 0);
        b.AddTriangle(new(-25, -25, 0), new(25, 25, 0), new(-25, 25, 0), 0);
        return new PlacedCollision(b.Build()!, transform ?? Matrix4x4.Identity);
    }

    private static readonly MapEntity AtOrigin = new() { ArchetypeHash = 1, Position = Vector3.Zero };

    [Fact]
    public void Without_collision_the_box_follows_the_model()
    {
        var fit = OccluderFit.Fit(new MapEntity { ArchetypeHash = 1, Position = new(100, 200, 30) }, Lot);
        Assert.Equal(OccluderFitSource.Model, fit!.Source);
        var box = Assert.Single(fit.Boxes);
        Assert.Equal(50 - 2 * OccluderFit.Inset, box.Size.X, 0.13f);
        Assert.Equal(new Vector3(100, 200, 36.5f), box.Center);
    }

    [Fact]
    public void Box_stays_inside_the_walls_not_on_the_car_park()
    {
        var b = new CollisionMeshBuilder();
        Building(b, new(4, -2), 20, 10, 12);
        // A sign post and a low wall around the car park.
        Wall(b, new(22, 22), new(22.3f, 22), 0, 6);
        Wall(b, new(-24, -24), new(24, -24), 0, 1);
        var fit = OccluderFit.Fit(AtOrigin, Lot, [Ground(b)]);
        Assert.Equal(OccluderFitSource.Walls, fit!.Source);
        var box = Assert.Single(fit.Boxes);
        // Inside the walls, less than a cell away from them.
        Assert.InRange(box.Size.X, 18.5f, 20f);
        Assert.InRange(box.Size.Y, 8.5f, 10f);
        Assert.Equal(12 - 2 * OccluderFit.Inset, box.Size.Z, 0.13f);
        Assert.Equal(4f, box.Center.X, 0.5f);
        Assert.Equal(-2f, box.Center.Y, 0.5f);
        foreach (var corner in box.Corners())
        {
            Assert.InRange(corner.X, -6f, 14f);
            Assert.InRange(corner.Y, -7f, 3f);
        }
    }

    [Fact]
    public void Each_closed_building_gets_its_box_and_doors_do_not_open_it()
    {
        var b = new CollisionMeshBuilder();
        Building(b, new(-12, 0), 16, 16, 10, door: 1.5f);
        Building(b, new(12, 5), 10, 20, 6);
        var fit = OccluderFit.Fit(AtOrigin, Lot, [Ground(b)]);
        Assert.Equal(2, fit!.Boxes.Count);
        var big = fit.Boxes.MaxBy(x => x.Size.X * x.Size.Y)!;
        Assert.Equal(-12f, big.Center.X, 0.5f);
        Assert.InRange(big.Size.X, 14.5f, 16f);
        var small = fit.Boxes.MinBy(x => x.Size.X * x.Size.Y)!;
        Assert.Equal(12f, small.Center.X, 0.5f);
        Assert.Equal(6 - 2 * OccluderFit.Inset, small.Size.Z, 0.13f);
    }

    [Fact]
    public void Tower_on_a_lower_part_gets_its_own_box_on_top()
    {
        var b = new CollisionMeshBuilder();
        Building(b, Vector2.Zero, 40, 30, 8);
        // A 12 x 12 m tower up to 60 m in the middle (its walls start on the lower roof).
        Wall(b, new(-6, -6), new(6, -6), 8, 60);
        Wall(b, new(6, -6), new(6, 6), 8, 60);
        Wall(b, new(6, 6), new(-6, 6), 8, 60);
        Wall(b, new(-6, 6), new(-6, -6), 8, 60);
        Roof(b, new(-6, -6), new(6, 6), 60);
        var tall = new Archetype { NameHash = 1, BoundsMin = Lot.BoundsMin, BoundsMax = new Vector3(25, 25, 61) };
        var fit = OccluderFit.Fit(AtOrigin, tall, [Ground(b)]);
        Assert.Equal(2, fit!.Boxes.Count);
        var lower = fit.Boxes[0];
        Assert.InRange(lower.Size.X, 38.5f, 40f);
        Assert.Equal(8 - 2 * OccluderFit.Inset, lower.Size.Z, 0.13f);
        var tower = fit.Boxes[1];
        Assert.InRange(tower.Size.X, 10.5f, 12f);
        Assert.InRange(tower.Size.Y, 10.5f, 12f);
        // From the top of the lower box up to under the tower's roof.
        Assert.Equal(lower.Center.Z + lower.Size.Z / 2, tower.Center.Z - tower.Size.Z / 2, 0.26f);
        Assert.Equal(60 - OccluderFit.Inset, tower.Center.Z + tower.Size.Z / 2, 0.26f);
    }

    [Fact]
    public void Interior_placed_in_the_building_stays_free()
    {
        var b = new CollisionMeshBuilder();
        Building(b, Vector2.Zero, 40, 20, 10);
        // An interior filling the west half.
        var fit = OccluderFit.Fit(AtOrigin, Lot, [Ground(b)], keepOut: [(new Vector3(-20, -10, 0), new Vector3(0, 10, 10))]);
        var box = Assert.Single(fit!.Boxes);
        foreach (var corner in box.Corners())
            Assert.True(corner.X >= 0, $"corner at x = {corner.X}");
        Assert.InRange(box.Size.X, 17f, 20f);
    }

    // The triangles of a collision mesh as a model (world positions, three points each).
    private static List<Vector3> AsModel(CollisionMeshBuilder b)
    {
        var mesh = b.Build()!;
        return mesh.Indices.Select(i => mesh.Vertices[i]).ToList();
    }

    [Fact]
    public void Small_object_next_to_a_building_does_not_take_its_walls()
    {
        // A building's collision (shared by the whole area), and a 4 x 4 m kiosk model standing outside it, whose box
        // reaches into the building.
        var area = new CollisionMeshBuilder();
        Building(area, new(10, 0), 20, 20, 12);
        var kioskModel = new CollisionMeshBuilder();
        Wall(kioskModel, new(-2, -2), new(2, -2), 0, 3);
        Wall(kioskModel, new(2, -2), new(2, 2), 0, 3);
        Wall(kioskModel, new(2, 2), new(-2, 2), 0, 3);
        Wall(kioskModel, new(-2, 2), new(-2, -2), 0, 3);
        Roof(kioskModel, new(-2, -2), new(2, 2), 3);
        var kiosk = new Archetype { NameHash = 1, BoundsMin = new(-8, -8, 0), BoundsMax = new(8, 8, 4) };
        var fit = OccluderFit.Fit(AtOrigin, kiosk, [Ground(area)], AsModel(kioskModel));
        foreach (var box in fit!.Boxes)
        {
            foreach (var corner in box.Corners())
                Assert.InRange(corner.X, -3f, 3f);
        }
    }

    [Fact]
    public void Without_collision_the_model_own_walls_are_used()
    {
        var model = new CollisionMeshBuilder();
        Building(model, new(4, -2), 20, 10, 12);
        var fit = OccluderFit.Fit(AtOrigin, Lot, [], AsModel(model));
        Assert.Equal(OccluderFitSource.Walls, fit!.Source);
        var box = Assert.Single(fit.Boxes);
        Assert.InRange(box.Size.X, 18.5f, 20f);
        Assert.Equal(12 - 2 * OccluderFit.Inset, box.Size.Z, 0.13f);
    }

    [Fact]
    public void Plant_model_gets_no_box()
    {
        // Two crossed vertical cards, as a weed or a bush.
        var model = new CollisionMeshBuilder();
        Wall(model, new(-3, 0), new(3, 0), 0, 4);
        Wall(model, new(0, -3), new(0, 3), 0, 4);
        var plant = new Archetype { NameHash = 1, BoundsMin = new(-3, -3, 0), BoundsMax = new(3, 3, 4) };
        var fit = OccluderFit.Fit(AtOrigin, plant, [], AsModel(model));
        Assert.Equal(OccluderFitSource.NoClosedSpace, fit!.Source);
    }

    [Fact]
    public void Open_structure_gets_no_box()
    {
        // Two parallel walls (a car wash tunnel open at both ends).
        var b = new CollisionMeshBuilder();
        Wall(b, new(-10, -4), new(10, -4), 0, 6);
        Wall(b, new(-10, 4), new(10, 4), 0, 6);
        var fit = OccluderFit.Fit(AtOrigin, Lot, [Ground(b)]);
        Assert.Equal(OccluderFitSource.NoClosedSpace, fit!.Source);
        Assert.Empty(fit.Boxes);
    }

    [Fact]
    public void Turned_object_gets_a_turned_box_inside_its_walls()
    {
        var entity = new MapEntity
        {
            ArchetypeHash = 1,
            Position = Vector3.Zero,
            StoredRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6),
        };
        var xAxis = Vector3.TransformNormal(Vector3.UnitX, entity.Transform);
        float angle = MathF.Atan2(xAxis.Y, xAxis.X);
        Assert.True(MathF.Abs(angle) > 0.4f);
        var b = new CollisionMeshBuilder();
        Building(b, Vector2.Zero, 20, 10, 12);
        var fit = OccluderFit.Fit(entity, Lot, [Ground(b, Matrix4x4.CreateRotationZ(angle))]);
        var box = Assert.Single(fit!.Boxes);
        Assert.Equal(angle, box.Angle, 0.01f);
        Assert.InRange(box.Size.X, 18.5f, 20f);
        Assert.InRange(box.Size.Y, 8.5f, 10f);
    }

    [Fact]
    public void Ground_only_collision_falls_back_to_the_model()
    {
        var fit = OccluderFit.Fit(AtOrigin, Lot, [Ground(new CollisionMeshBuilder())]);
        Assert.Equal(OccluderFitSource.Model, fit!.Source);
    }
}
