using System.Numerics;
using NodeRunner.Editor;
using Xunit;

namespace NodeRunner.Tests;

public class WallOpeningTests
{
    // A wall 4 m wide and 3 m high in the plane y = 0, facing -Y (corners anticlockwise seen from -Y).
    private static readonly Vector3[] Wall = [new(-2, 0, 0), new(2, 0, 0), new(2, 0, 3), new(-2, 0, 3)];
    private static readonly ushort[] WallIndices = [0, 1, 2, 0, 2, 3];
    private static readonly Opening Door = new(new Vector3(0, 0, 0), -Vector3.UnitY, 1.2f, 2.3f);

    private static List<(Vector3 A, Vector3 B, Vector3 C)> Added(Vector3[] positions, ushort[] indices, MeshCut cut)
    {
        Vector3 At(int i)
        {
            var (t, w) = cut.Vertices[i];
            return positions[indices[t * 3]] * w.X + positions[indices[t * 3 + 1]] * w.Y + positions[indices[t * 3 + 2]] * w.Z;
        }
        var list = new List<(Vector3, Vector3, Vector3)>();
        for (int i = 0; i + 2 < cut.Indices.Count; i += 3)
            list.Add((At(cut.Indices[i]), At(cut.Indices[i + 1]), At(cut.Indices[i + 2])));
        return list;
    }

    private static float Area((Vector3 A, Vector3 B, Vector3 C) t) => Vector3.Cross(t.B - t.A, t.C - t.A).Length() * 0.5f;

    [Fact]
    public void Wall_keeps_everything_around_the_hole()
    {
        var cut = WallOpening.Cut(Wall, WallIndices, Door);

        Assert.Equal([0, 1], cut.Removed);
        var added = Added(Wall, WallIndices, cut);
        Assert.Equal(4 * 3 - 1.2f * 2.3f, added.Sum(Area), 3);
        foreach (var t in added)
        {
            var center = (t.A + t.B + t.C) / 3;
            Assert.False(MathF.Abs(center.X) < 0.6f - 1e-3f && center.Z < 2.3f - 1e-3f, $"triangle left in the hole at {center}");
            // Same side as the wall: normal towards -Y.
            Assert.True(Vector3.Cross(t.B - t.A, t.C - t.A).Y < 0);
        }
        Assert.All(cut.Vertices, v => Assert.Equal(1f, v.Weights.X + v.Weights.Y + v.Weights.Z, 4));
    }

    [Fact]
    public void Triangle_inside_the_hole_goes_without_replacement()
    {
        Vector3[] positions = [new(-0.3f, 0, 0.5f), new(0.3f, 0, 0.5f), new(0, 0, 1.5f)];
        var cut = WallOpening.Cut(positions, [0, 1, 2], Door);

        Assert.Equal([0], cut.Removed);
        Assert.Empty(cut.Indices);
    }

    [Fact]
    public void Floor_and_far_walls_are_left_alone()
    {
        // The ground in front of the door, and a wall 3 m behind it.
        Vector3[] positions = [new(-3, -3, 0), new(3, -3, 0), new(3, 3, 0), new(-1, 3, 0), new(1, 3, 0), new(0, 3, 2)];
        var cut = WallOpening.Cut(positions, [0, 1, 2, 3, 4, 5], Door);

        Assert.True(cut.IsEmpty);
    }

    [Fact]
    public void Wall_beside_the_hole_is_untouched()
    {
        Vector3[] positions = [new(1, 0, 0), new(3, 0, 0), new(3, 0, 3)];
        var cut = WallOpening.Cut(positions, [0, 1, 2], Door);

        Assert.True(cut.IsEmpty);
    }

    [Fact]
    public void Placed_on_the_floor_facing_out()
    {
        var opening = Opening.At(new Vector3(5, 2, 1.4f), new Vector3(0, -0.98f, 0.2f), floor: 0.1f);

        Assert.NotNull(opening);
        Assert.Equal(new Vector3(5, 2, 0.1f), opening.Value.Bottom);
        Assert.Equal(-1f, opening.Value.Normal.Y, 4);
        Assert.Null(Opening.At(Vector3.Zero, Vector3.UnitZ));
    }
}
