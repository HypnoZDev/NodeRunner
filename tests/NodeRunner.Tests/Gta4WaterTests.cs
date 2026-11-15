using NodeRunner.Data.Native.Gta4;

namespace NodeRunner.Tests;

public class Gta4WaterTests
{
    private static string Line(float x1, float y1, float x2, float y2, float z) =>
        FormattableString.Invariant($"{x1} {y1} {z} 0 0 1 1   {x2} {y1} {z} 0 0 1 1   {x1} {y2} {z} 0 0 1 1   {x2} {y2} {z} 0 0 1 1 5 1.0");

    [Fact]
    public void Rectangles_are_read_whatever_the_corner_order()
    {
        var quads = Gta4Water.Parse(Line(-966, 664, -976, 674, 0.0001f));
        var q = Assert.Single(quads);
        Assert.Equal((-976f, -966f, 664f, 674f), (q.MinX, q.MaxX, q.MinY, q.MaxY));
    }

    [Fact]
    public void Flat_lines_are_skipped()
    {
        Assert.Empty(Gta4Water.Parse(Line(-504, -8, -504, -8, 0f)));
    }

    [Fact]
    public void Overlaps_at_the_same_height_are_cut_out()
    {
        // A 10 x 10 square, then a 20 x 20 one around it: the second keeps only the ring around the first.
        var quads = Gta4Water.Parse(Line(0, 0, 10, 10, 0f) + "\n" + Line(-5, -5, 15, 15, 0f));
        float area = quads.Sum(q => (q.MaxX - q.MinX) * (q.MaxY - q.MinY));
        Assert.Equal(400f, area, 0.01f);
        Assert.Equal(5, quads.Count);
    }

    [Fact]
    public void Overlaps_at_another_height_stay()
    {
        var quads = Gta4Water.Parse(Line(0, 0, 10, 10, 0f) + "\n" + Line(0, 0, 10, 10, 12f));
        Assert.Equal(2, quads.Count);
    }
}
