using NodeRunner.Data.Hashing;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Tests;

public class ScenarioTests
{
    [Theory]
    [InlineData("adder", 0xB779A091u)]   // Well-known vehicle model hash.
    [InlineData("ADDER", 0xB779A091u)]   // The game ignores case.
    [InlineData("", 0u)]
    public void Joaat_matches_game_hashes(string text, uint expected)
    {
        Assert.Equal(expected, Joaat.Hash(text));
    }

    [Theory]
    [InlineData("WORLD_HUMAN_SMOKING", ScenarioCategory.PedActivity)]
    [InlineData("Walk", ScenarioCategory.PedActivity)]
    [InlineData("PROP_HUMAN_SEAT_BENCH", ScenarioCategory.PedSeated)]
    [InlineData("WORLD_VEHICLE_DRIVE_SOLO", ScenarioCategory.Vehicle)]
    [InlineData("DRIVE", ScenarioCategory.Vehicle)]
    [InlineData("WORLD_DEER_GRAZING", ScenarioCategory.Animal)]
    [InlineData(null, ScenarioCategory.Other)]
    public void Categorises_scenario_types(string? name, ScenarioCategory expected)
    {
        Assert.Equal(expected, ScenarioCategories.FromTypeName(name));
    }
}
