using System.Numerics;
using NodeRunner.Editor;
using Xunit;

namespace NodeRunner.Tests;

public class DoorsTests
{
    [Theory]
    [InlineData("v_ilev_fh_frntdoor", true)]
    [InlineData("prop_ld_garaged_01", true)]
    [InlineData("PROP_COM_LS_DOOR_01", true)]
    [InlineData("prop_outdoor_fan_01", false)]
    [InlineData("prop_bench_01a", false)]
    [InlineData(null, false)]
    public void DoorsAreFoundByName(string? name, bool door) => Assert.Equal(door, Doors.IsDoor(name));

    [Fact]
    public void HideScriptUsesNameOrHash()
    {
        var models = new[]
        {
            new HiddenModel(1, "v_ilev_fh_frntdoor", new Vector3(1.234f, -2f, 30.5f)),
            new HiddenModel(0x3073333B, null, new Vector3(0, 0, 0)),
        };
        Assert.Equal("-- client.lua\nCreateModelHide(1.23, -2.00, 30.50, 0.5, `v_ilev_fh_frntdoor`, true)\n"
            + "CreateModelHide(0.00, 0.00, 0.00, 0.5, 0x3073333B, true)", ModelHideScript.Hide(models, CopyFormat.Lua));
        Assert.Contains("API.CreateModelHide(1.23f, -2.00f, 30.50f, 0.5f, (uint)API.GetHashKey(\"v_ilev_fh_frntdoor\"), true);",
            ModelHideScript.Hide(models, CopyFormat.CSharp));
    }
}
