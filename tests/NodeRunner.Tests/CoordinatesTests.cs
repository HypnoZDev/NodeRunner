using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class CoordinatesTests
{
    [Theory]
    [InlineData("vector3(-1037.52, -2737.89, 20.17)", -1037.52f, -2737.89f, 20.17f)]
    [InlineData("vec3(-1037.52,-2737.89,20.17)", -1037.52f, -2737.89f, 20.17f)]
    [InlineData("  -1037.52, -2737.89, 20.17 ", -1037.52f, -2737.89f, 20.17f)]
    [InlineData("-1037.52 -2737.89 20.17", -1037.52f, -2737.89f, 20.17f)]
    [InlineData("{x = -1037.52, y = -2737.89, z = 20.17}", -1037.52f, -2737.89f, 20.17f)]
    [InlineData("X: -1037.52 Y: -2737.89 Z: 20.17", -1037.52f, -2737.89f, 20.17f)]
    [InlineData("vector3(-1037.52f, -2737.89f, 20.17f)", -1037.52f, -2737.89f, 20.17f)]
    public void Reads_a_position(string text, float x, float y, float z)
    {
        var c = Coordinates.TryParse(text);
        Assert.NotNull(c);
        Assert.Equal(x, c.Value.X, 0.001f);
        Assert.Equal(y, c.Value.Y, 0.001f);
        Assert.Equal(z, c.Value.Z!.Value, 0.001f);
        Assert.Null(c.Value.Heading);
    }

    [Fact]
    public void Reads_the_heading_of_a_vector4()
    {
        var c = Coordinates.TryParse("vector4(215.76, -810.12, 30.73, 157.5)");
        Assert.Equal(157.5f, c!.Value.Heading!.Value, 0.001f);
    }

    [Fact]
    public void Height_is_optional()
    {
        var c = Coordinates.TryParse("215.76, -810.12");
        Assert.NotNull(c);
        Assert.Null(c.Value.Z);
        Assert.Equal(42f, c.Value.ToVector3(42f).Z);
    }

    [Fact]
    public void Heading_zero_points_north_and_90_west()
    {
        var north = new Coordinates(0, 0, 0, 0).HeadingDirection!.Value;
        var west = new Coordinates(0, 0, 0, 90).HeadingDirection!.Value;
        Assert.Equal(1f, north.Y, 0.001f);
        Assert.Equal(-1f, west.X, 0.001f);
    }

    [Theory]
    [InlineData("prop_bench_01a")]
    [InlineData("dt1 02")]
    [InlineData("12")]
    [InlineData("1, 2, 3, 4, 5")]
    [InlineData("")]
    public void Ignores_other_text(string text) => Assert.Null(Coordinates.TryParse(text));
}
