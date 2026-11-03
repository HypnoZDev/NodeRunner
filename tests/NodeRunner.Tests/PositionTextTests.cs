using System.Numerics;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class PositionTextTests
{
    private static readonly Vector3 Bench = new(215.756f, -810.12f, 30.7f);

    [Theory]
    [InlineData(CopyFormat.Lua, "vector3(215.76, -810.12, 30.70)")]
    [InlineData(CopyFormat.Json, "{\"x\": 215.76, \"y\": -810.12, \"z\": 30.70}")]
    [InlineData(CopyFormat.CSharp, "new Vector3(215.76f, -810.12f, 30.70f)")]
    public void Writes_a_position(CopyFormat format, string expected) =>
        Assert.Equal(expected, PositionText.Position(Bench, null, format));

    [Theory]
    [InlineData(CopyFormat.Lua, "vector4(215.76, -810.12, 30.70, 90.00)")]
    [InlineData(CopyFormat.Json, "{\"x\": 215.76, \"y\": -810.12, \"z\": 30.70, \"heading\": 90.00}")]
    [InlineData(CopyFormat.CSharp, "new Vector4(215.76f, -810.12f, 30.70f, 90.00f)")]
    public void Writes_a_position_and_heading(CopyFormat format, string expected) =>
        Assert.Equal(expected, PositionText.Position(Bench, 90f, format));

    [Fact]
    public void Writes_a_lua_table()
    {
        var text = PositionText.List([new("prop_bench_01a", Bench, 90f), new("prop_bin_05a", Vector3.Zero, 0f)], CopyFormat.Lua);

        Assert.Equal("{\n    { model = 'prop_bench_01a', coords = vector4(215.76, -810.12, 30.70, 90.00) },\n"
            + "    { model = 'prop_bin_05a', coords = vector4(0.00, 0.00, 0.00, 0.00) },\n}", text);
    }

    [Fact]
    public void Writes_a_json_array_that_parses()
    {
        var text = PositionText.List([new("prop_bench_01a", Bench, 90f), new("prop_bin_05a", Vector3.Zero, 0f)], CopyFormat.Json);

        using var json = System.Text.Json.JsonDocument.Parse(text);
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal("prop_bin_05a", json.RootElement[1].GetProperty("model").GetString());
        Assert.Equal(-810.12, json.RootElement[0].GetProperty("y").GetDouble());
    }

    [Fact]
    public void Writes_a_csharp_array()
    {
        var text = PositionText.List([new("prop_bench_01a", Bench, 90f)], CopyFormat.CSharp);

        Assert.Equal("new[]\n{\n    new Vector4(215.76f, -810.12f, 30.70f, 90.00f), // prop_bench_01a\n}", text);
    }
}
