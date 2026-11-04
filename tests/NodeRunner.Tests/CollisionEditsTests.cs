using System.Numerics;
using NodeRunner.Data.Collisions;
using Xunit;

namespace NodeRunner.Tests;

public sealed class CollisionEditsTests
{
    // Three triangles: polygons 0 and 1, then a lone primitive (-1).
    private static CollisionMesh Mesh() => new()
    {
        BoundsMin = Vector3.Zero,
        BoundsMax = Vector3.One,
        Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.One],
        Indices = [0, 1, 2, 1, 2, 3, 0, 2, 3],
        Materials = [1, 1, 5],
        Polygons = [0, 1, -1],
    };

    [Fact]
    public void Apply_RemovesPolygonsAndChangesMaterials()
    {
        var edit = new CollisionFileEdit();
        edit.Removed.Add(0);
        edit.Materials[1] = 9;

        var mesh = edit.Apply(Mesh());

        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal([1, -1], mesh.Polygons);
        Assert.Equal([9, 5], mesh.Materials);
        Assert.Equal([1, 2, 3, 0, 2, 3], mesh.Indices);
    }

    [Fact]
    public void Edits_RoundTripThroughJsonAndForgetEmptyFiles()
    {
        var edits = new CollisionEdits();
        edits.SetRemoved("a.ybn", [3, 4], removed: true);
        edits.SetMaterial("a.ybn", 7, 12);
        edits.SetMaterial("b.ybn", 1, 2);
        edits.SetMaterial("b.ybn", 1, null);
        int revision = edits.Revision;

        var back = CollisionEdits.FromJson(edits.ToJson());

        Assert.Equal(4, revision);
        Assert.Null(back.For("b.ybn"));
        Assert.True(back.IsRemoved("A.YBN", 4));
        Assert.Equal((byte)12, back.MaterialOf("a.ybn", 7));
    }
}
