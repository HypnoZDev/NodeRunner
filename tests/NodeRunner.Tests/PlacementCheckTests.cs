using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class PlacementCheckTests
{
    // Horizontal squares (two triangles each) at the given heights, 20 m wide around the origin.
    private static List<PlacedCollision> Floors(params float[] heights)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (float z in heights)
        {
            int b = vertices.Count;
            vertices.AddRange([new(-10, -10, z), new(10, -10, z), new(10, 10, z), new(-10, 10, z)]);
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        var mesh = new CollisionMesh
        {
            BoundsMin = new Vector3(-10, -10, heights.Min()),
            BoundsMax = new Vector3(10, 10, heights.Max()),
            Vertices = vertices,
            Indices = indices,
            Materials = new byte[indices.Count / 3],
        };
        return [new PlacedCollision(mesh, Matrix4x4.Identity)];
    }

    [Fact]
    public void PointOnTheGroundIsFine()
    {
        Assert.Null(PlacementCheck.Classify(Floors(0f), new Vector3(1, 1, 0.02f)));
        Assert.Null(PlacementCheck.Classify(Floors(0f), new Vector3(1, 1, -0.3f)));
        Assert.Null(PlacementCheck.Classify(Floors(0f), new Vector3(1, 1, 0.9f)));
    }

    [Fact]
    public void PointHighAboveTheGroundFloats()
    {
        Assert.Equal(PlacementCheck.Kind.Floating, PlacementCheck.Classify(Floors(0f), new Vector3(1, 1, 2.5f)));
        Assert.Null(PlacementCheck.Classify(Floors(0f), new Vector3(1, 1, 1.3f), PlacementCheck.MaxSeatGap));
        // Nothing under it at all (a bridge deleted): floats too.
        Assert.Equal(PlacementCheck.Kind.Floating, PlacementCheck.Classify(Floors(-50f), new Vector3(1, 1, 0f)));
    }

    [Fact]
    public void PointUnderASurfaceIsBuried()
    {
        Assert.Equal(PlacementCheck.Kind.Buried, PlacementCheck.Classify(Floors(0f), new Vector3(1, 1, -1.5f)));
        // A floor two storeys up with ground under the point: fine.
        Assert.Null(PlacementCheck.Classify(Floors(0f, 2f), new Vector3(1, 1, 0.05f)));
    }

    [Fact]
    public void NoCollisionGivesNoVerdict() =>
        Assert.Null(PlacementCheck.Classify([], new Vector3(1, 1, 50f)));
}
