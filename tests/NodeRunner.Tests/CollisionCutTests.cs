using System.Numerics;
using NodeRunner.Data.Collisions;

namespace NodeRunner.Tests;

public class CollisionCutTests
{
    // A 10 x 10 x 8 m building standing at (100, 200, 30), turned by 90°.
    private static readonly CutVolume Building = new(
        Matrix4x4.CreateTranslation(-100, -200, -30) * Matrix4x4.CreateRotationZ(-MathF.PI / 2),
        new Vector3(-5, -5, 0), new Vector3(5, 5, 8));

    private static Vector3 World(float x, float y, float z) =>
        new Vector3(100, 200, 30) + Vector3.Transform(new Vector3(x, y, z), Matrix4x4.CreateRotationZ(MathF.PI / 2));

    [Fact]
    public void Wall_inside_the_box_is_removed()
    {
        Assert.True(CollisionCut.IsInside(Building, World(4, -4, 0), World(4, 4, 0), World(4, 4, 7)));
    }

    [Fact]
    public void Ground_under_the_building_is_kept()
    {
        Assert.False(CollisionCut.IsInside(Building, World(-4, -4, 0.2f), World(4, -4, 0.2f), World(4, 4, 0.3f)));
    }

    [Fact]
    public void Roof_is_removed()
    {
        Assert.True(CollisionCut.IsInside(Building, World(-4, -4, 8), World(4, -4, 8), World(4, 4, 8)));
    }

    [Fact]
    public void Triangle_crossing_the_box_is_kept()
    {
        Assert.False(CollisionCut.IsInside(Building, World(4, 0, 2), World(12, 0, 2), World(4, 0, 6)));
    }

    private static FlatSurfaces Surfaces(params Vector3[] triangles)
    {
        var mesh = new CollisionMesh
        {
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.Zero,
            Vertices = triangles,
            Indices = Enumerable.Range(0, triangles.Length).ToArray(),
            Materials = new byte[triangles.Length / 3],
        };
        var surfaces = new FlatSurfaces();
        surfaces.Add(mesh, Matrix4x4.Identity, new Vector2(-1000), new Vector2(1000));
        return surfaces;
    }

    [Fact]
    public void With_surfaces_the_ground_is_what_has_nothing_under_it()
    {
        // Ground 3 m above the bottom of the box (the model goes underground), and the roof 6 m higher.
        Vector3[] ground = [World(-6, -6, 3), World(6, -6, 3), World(6, 6, 3)];
        Vector3[] roof = [World(-4, -4, 7.9f), World(4, -4, 7.9f), World(4, 4, 7.9f)];
        var surfaces = Surfaces([.. ground, .. roof]);
        Assert.False(CollisionCut.IsInside(Building, World(-4, -4, 3), World(4, -4, 3), World(4, 4, 3), surfaces));
        Assert.True(CollisionCut.IsInside(Building, roof[0], roof[1], roof[2], surfaces));
    }

    [Fact]
    public void Floor_overhanging_the_edge_of_a_lower_area_is_kept()
    {
        // A lower area under one corner of the floor only: the rest of it is the ground.
        Vector3[] lower = [World(2, 2, 0.5f), World(5, 2, 0.5f), World(5, 5, 0.5f)];
        var surfaces = Surfaces(lower);
        Assert.False(CollisionCut.IsInside(Building, World(-4, -4, 4), World(4.5f, -4, 4), World(4.5f, 4.5f, 4), surfaces));
    }

    [Fact]
    public void Polygon_goes_only_when_all_its_triangles_go()
    {
        // Polygon 0: two wall triangles inside; polygon 1: one inside, one outside; polygon -1: lone primitive.
        Vector3[] vertices =
        [
            World(4, -4, 1), World(4, 4, 1), World(4, 4, 7),
            World(4, -4, 1), World(4, 4, 7), World(4, -4, 7),
            World(-4, -4, 1), World(-4, 4, 1), World(-4, 4, 7),
            World(-4, -4, 1), World(-20, 4, 7), World(-4, -4, 7),
            World(0, 0, 2), World(1, 0, 2), World(0, 0, 4),
        ];
        var mesh = new CollisionMesh
        {
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.Zero,
            Vertices = vertices,
            Indices = Enumerable.Range(0, vertices.Length).ToArray(),
            Materials = new byte[5],
            Polygons = [0, 0, 1, 1, -1],
        };
        Assert.Equal([0], CollisionCut.Select(mesh, Matrix4x4.Identity, [Building]));
    }
}
