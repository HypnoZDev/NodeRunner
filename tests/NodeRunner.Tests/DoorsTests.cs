using System.Numerics;
using NodeRunner.Data.Maps;
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
    public void CloseDoorsFormADoubleDoor()
    {
        var doors = new[]
        {
            new DoorItem(1, "door_l", new Vector3(10, 0, 30), 90),
            new DoorItem(2, "far", new Vector3(50, 0, 30), 0),
            new DoorItem(3, "door_r", new Vector3(11.5f, 0, 30.1f), 270),
        };
        var groups = DoorLockScript.Group(doors);
        Assert.Equal(2, groups.Count);
        Assert.Equal(["door_l", "door_r"], groups[0].Select(d => d.Name));

        string qb = DoorLockScript.Write(doors, DoorLockFormat.QbDoorlock, false);
        Assert.Contains("doorType = 'double'", qb);
        Assert.Contains("{ objName = 'door_l', objYaw = 90.0, objCoords = vec3(10.000, 0.000, 30.000) },", qb);
        Assert.Contains("    objName = 'far',\n    objYaw = 0.0,\n    objCoords = vec3(50.000, 0.000, 30.000),", qb);

        string natives = DoorLockScript.Write(doors, DoorLockFormat.Natives, false);
        Assert.Contains("AddDoorToSystem(`nrs_door_50_0_30`, `far`, 50.000, 0.000, 30.000, false, false, false)", natives);
        Assert.Contains("DoorSystemSetDoorState(`nrs_door_50_0_30`, 1, false, false)", natives);
    }

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

    private static MapEntity Entity(uint hash, Vector3 position, uint guid = 0) =>
        new() { ArchetypeHash = hash, Position = position, StoredRotation = Quaternion.Identity, Guid = guid, LodLevel = LodLevel.Hd };

    private static string? Name(uint hash) => hash switch { 1 => "v_ilev_door_a", 2 => "v_ilev_door_b", _ => "prop_bench_01a" };

    [Fact]
    public void MovedAndRemovedGameDoorsAreFound()
    {
        var original = new[] { Entity(1, new Vector3(0, 0, 0), 10), Entity(2, new Vector3(5, 0, 0), 11), Entity(3, new Vector3(9, 0, 0), 12) };
        var current = new[] { Entity(1, new Vector3(0.5f, 0, 0), 10), Entity(3, new Vector3(20, 0, 0), 12) };
        var (removed, moved) = DoorCheck.Changed(original, current, Name);
        Assert.Equal(2u, Assert.Single(removed).ArchetypeHash);
        Assert.Same(current[0], Assert.Single(moved).Current);
    }

    [Fact]
    public void DoorsAtTheSamePlaceAreReportedOnce()
    {
        var a = Entity(1, new Vector3(0, 0, 0));
        var b = Entity(2, new Vector3(0.1f, 0, 0));
        var far = Entity(2, new Vector3(3, 0, 0));
        var all = new[] { a, b, far };
        var pairs = DoorCheck.Duplicates([a, b, far], (_, _) => all, Name);
        Assert.Single(pairs);
    }

    private static Archetype Type(Vector3 min, Vector3 max, uint flags = DoorCheck.DynamicFlag) =>
        new() { NameHash = 1, BoundsMin = min, BoundsMax = max, Flags = flags };

    [Fact]
    public void DoorShapeComesFromTheModelBox()
    {
        var hinged = DoorShape.Of(Type(new Vector3(-1.1f, -0.05f, 0), new Vector3(0.02f, 0.05f, 2.2f)), "v_ilev_door");
        Assert.Equal(DoorKind.Hinged, hinged.Kind);
        Assert.Equal(-1f, hinged.Side);
        Assert.Equal(1.1f, hinged.Width, 3);
        Assert.Contains(hinged.Lines(), l => Vector3.Distance(l.To, new Vector3(0, 1.1f, 0.05f)) < 1e-3f);

        Assert.Equal(DoorKind.Garage, DoorShape.Of(Type(new Vector3(-2.5f, 0, 0), new Vector3(2.5f, 0.1f, 3)), "prop_garagedoor_01").Kind);
        Assert.Equal(DoorKind.Sliding, DoorShape.Of(Type(new Vector3(-1, 0, 0), new Vector3(1, 0.1f, 2.5f)), "prop_door_elev").Kind);
        Assert.Equal(DoorKind.Fixed, DoorShape.Of(Type(new Vector3(-1, 0, 0), new Vector3(0, 0.1f, 2), flags: 0), "v_ilev_door").Kind);

        string qb = DoorLockScript.Write([new DoorItem(1, "prop_garagedoor_01", Vector3.Zero, 0, DoorKind.Garage)], DoorLockFormat.QbDoorlock, false);
        Assert.Contains("doorType = 'garage'", qb);
    }
}
