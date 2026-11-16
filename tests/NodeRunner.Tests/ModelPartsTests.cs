using System.Numerics;
using NodeRunner.Editor;
using Xunit;

namespace NodeRunner.Tests;

public class ModelPartsTests
{
    // A wall (two triangles) and, in front of it, a logo made of two triangles sharing their corners with each other only;
    // the logo's corners are separate vertices with the same positions (as at a texture seam).
    private static readonly Vector3[] Positions =
    [
        new(0, 0, 0), new(4, 0, 0), new(4, 0, 3), new(0, 0, 3),
        new(1, -0.1f, 1), new(2, -0.1f, 1), new(2, -0.1f, 2), new(2, -0.1f, 2), new(1, -0.1f, 2), new(1, -0.1f, 1),
    ];

    private static readonly ushort[] Indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 7, 8, 9];

    [Fact]
    public void ClickPicksTheLogoAndItsJoinedTriangles()
    {
        var meshes = new List<(Vector3[], ushort[])> { (Positions, Indices) };
        var hit = ModelParts.Hit(meshes, new Vector3(1.5f, -5, 1.4f), Vector3.UnitY);
        Assert.NotNull(hit);
        Assert.Equal(2, hit.Value.Triangle.Triangle);
        Assert.Equal([2, 3], ModelParts.Connected(Positions, Indices, 2).Order());
        Assert.Equal([0, 1], ModelParts.Connected(Positions, Indices, 0).Order());

        var (min, max) = ModelParts.Box(Positions, Indices, [2, 3]);
        Assert.Equal([2, 3], ModelParts.Inside(Positions, Indices, min - new Vector3(0.01f), max + new Vector3(0.01f)));

        // An emptied triangle is not hit any more.
        var emptied = (ushort[])Indices.Clone();
        emptied[7] = emptied[8] = emptied[6];
        emptied[10] = emptied[11] = emptied[9];
        var behind = ModelParts.Hit([(Positions, emptied)], new Vector3(1.5f, -5, 1.4f), Vector3.UnitY);
        Assert.Equal(0, behind!.Value.Triangle.Triangle / 2);
    }
}
