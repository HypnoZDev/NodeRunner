using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Editor;
using Xunit;

namespace NodeRunner.Tests;

public sealed class CollisionPickerTests
{
    // Camera at the origin looking along +Y; a wall (polygon 0) 10 m ahead, a larger wall (polygon 1) 20 m ahead.
    private static readonly Matrix4x4 ViewProjection =
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ) * Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1f, 0.1f, 1000f);

    private static CollisionMesh Walls() => new()
    {
        BoundsMin = new Vector3(-20, 10, -20),
        BoundsMax = new Vector3(20, 20, 20),
        Vertices =
        [
            new(-2, 10, -2), new(2, 10, -2), new(2, 10, 2), new(-2, 10, 2),
            new(-20, 20, -20), new(20, 20, -20), new(20, 20, 20), new(-20, 20, 20),
        ],
        Indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7],
        Materials = [0, 0, 0, 0],
        Polygons = [0, 0, 1, 1],
    };

    [Fact]
    public void Click_TakesTheNearestPolygon()
    {
        var picked = CollisionPicker.Pick([("a.ybn", Walls())], ViewProjection, new Vector2(100), new Vector2(49.5f), new Vector2(50.5f));
        Assert.Equal([new CollisionPolygon("a.ybn", 0)], picked);
    }

    [Fact]
    public void Rectangle_TakesOnlyWhatIsSeen()
    {
        // Whole view: the far wall shows around the near one.
        var all = CollisionPicker.Pick([("a.ybn", Walls())], ViewProjection, new Vector2(100), Vector2.Zero, new Vector2(100));
        Assert.Equal(2, all.Count);

        // Centre only: the far wall is hidden behind the near one.
        var centre = CollisionPicker.Pick([("a.ybn", Walls())], ViewProjection, new Vector2(100), new Vector2(45), new Vector2(55));
        Assert.Equal([new CollisionPolygon("a.ybn", 0)], centre);
    }
}
