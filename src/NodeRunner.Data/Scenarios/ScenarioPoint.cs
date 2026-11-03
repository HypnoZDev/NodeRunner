using System.Numerics;

namespace NodeRunner.Data.Scenarios;

// A place where the game can spawn a ped, vehicle or animal doing an activity (sitting on a bench, smoking, fishing,
// parked car...).
public sealed class ScenarioPoint
{
    public required Vector3 Position { get; init; }

    public float Heading { get; init; }

    public uint TypeHash { get; init; }

    public string? TypeName { get; set; }

    public ScenarioCategory Category { get; set; } = ScenarioCategory.Other;

    public byte StartHour { get; init; }

    public byte EndHour { get; init; }

    public byte Probability { get; init; }

    public uint Flags { get; init; }

    public Vector3 Direction => new(-MathF.Sin(Heading), MathF.Cos(Heading), 0f);
}

// Broad families of scenarios, used for colours and layers.
public enum ScenarioCategory
{
    PedActivity,

    PedSeated,

    Vehicle,

    Animal,

    Other,
}

public static class ScenarioCategories
{
    public static ScenarioCategory FromTypeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return ScenarioCategory.Other;

        name = name.ToUpperInvariant();
        if (name.StartsWith("PROP_HUMAN_SEAT") || name.Contains("_SEAT_") || name.Contains("BENCH"))
            return ScenarioCategory.PedSeated;
        if (name.StartsWith("WORLD_VEHICLE") || name == "DRIVE")
            return ScenarioCategory.Vehicle;
        if (name.StartsWith("WORLD_HUMAN") || name.StartsWith("PROP_HUMAN") || name.StartsWith("CODE_HUMAN"))
            return ScenarioCategory.PedActivity;
        // Steps of scenario chains (a ped walking from one point to the next).
        if (name is "WALK" or "STANDING")
            return ScenarioCategory.PedActivity;
        if (name.StartsWith("WORLD_"))
            return ScenarioCategory.Animal; // WORLD_DEER_GRAZING, WORLD_GULL_STANDING, WORLD_FISH_IDLE...
        return ScenarioCategory.Other;
    }
}
