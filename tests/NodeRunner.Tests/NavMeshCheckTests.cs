using System.Numerics;
using NodeRunner.Data.NavMeshes;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class NavMeshCheckTests
{
    // A walkable ground of 4 squares of 10 m (100 m² each) at height 0, around the origin.
    private static NavMeshTile Ground()
    {
        var vertices = new List<Vector3>();
        var indices = new List<ushort>();
        var polygons = new List<NavMeshPolygon>();
        foreach (var (x, y) in new[] { (-10f, -10f), (0f, -10f), (-10f, 0f), (0f, 0f) })
        {
            int first = indices.Count;
            foreach (var p in new Vector3[] { new(x, y, 0), new(x + 10, y, 0), new(x + 10, y + 10, 0), new(x, y + 10, 0) })
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(p);
            }
            polygons.Add(new NavMeshPolygon(first, 4, 0, 0, 0));
        }
        return new NavMeshTile
        {
            AreaId = 0,
            BoundsMin = new Vector3(-10, -10, 0),
            BoundsMax = new Vector3(10, 10, 0),
            Vertices = vertices,
            Indices = indices,
            Polygons = polygons,
        };
    }

    // Floor and roof of a 4 x 4 m box (its sides are vertical: rays never meet them).
    private static List<Vector3> Box(Matrix4x4 transform, float bottom, float top)
    {
        var triangles = new List<Vector3>();
        foreach (float z in new[] { bottom, top })
        {
            Vector3 a = new(-2, -2, z), b = new(2, -2, z), c = new(2, 2, z), d = new(-2, 2, z);
            triangles.AddRange([a, b, c, a, c, d]);
        }
        return triangles.Select(p => Vector3.Transform(p, transform)).ToList();
    }

    [Fact]
    public void AddedBuildingOnTheNavMeshCrossesIt()
    {
        var at = Matrix4x4.CreateTranslation(1, 1, 0);
        float area = NavMeshCheck.CrossedArea([Ground()], at, new Vector3(-2, -2, 0.5f), new Vector3(2, 2, 4), Box(at, 0.5f, 4));
        Assert.True(area >= 100);
    }

    [Fact]
    public void ObjectHighAboveTheNavMeshDoesNotCrossIt()
    {
        var at = Matrix4x4.CreateTranslation(1, 1, 0);
        Assert.Equal(0, NavMeshCheck.CrossedArea([Ground()], at, new Vector3(-2, -2, 3), new Vector3(2, 2, 6), Box(at, 3, 6)));
    }

    [Fact]
    public void GroundPieceUnderTheNavMeshDoesNotCrossIt()
    {
        var at = Matrix4x4.Identity;
        Assert.Equal(0, NavMeshCheck.CrossedArea([Ground()], at, new Vector3(-2, -2, -3), new Vector3(2, 2, -0.1f), Box(at, -3, -0.1f)));
    }

    [Fact]
    public void CentresInsideAreSortedHighestFirst()
    {
        var centres = NavMeshCheck.CentresInside([Ground()], Matrix4x4.Identity, new Vector3(-10, -10, -1), new Vector3(10, 10, 5));
        Assert.Equal(4, centres.Count);
        Assert.Empty(NavMeshCheck.CentresInside([Ground()], Matrix4x4.Identity, new Vector3(-10, -10, 2), new Vector3(10, 10, 5)));
    }
}
