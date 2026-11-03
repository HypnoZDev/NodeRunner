using System.Numerics;

namespace NodeRunner.Data.Scenarios;

/// <summary>
/// A place where the game can spawn a ped, vehicle or animal doing an activity
/// (sitting on a bench, smoking, fishing, parked car...). Stored in the scenario region files (.ymt).
/// </summary>
public sealed class ScenarioPoint
{
    /// <summary>World position in metres.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>Facing direction in radians around the vertical axis (0 = north, counter-clockwise).</summary>
    public float Heading { get; init; }

    /// <summary>Hash of the scenario type name (e.g. WORLD_HUMAN_SMOKING).</summary>
    public uint TypeHash { get; init; }

    /// <summary>Scenario type name, or null if the hash could not be resolved.</summary>
    public string? TypeName { get; set; }

    public ScenarioCategory Category { get; set; } = ScenarioCategory.Other;

    /// <summary>Hour the point becomes active (0-23); equal to <see cref="EndHour"/> means "all day".</summary>
    public byte StartHour { get; init; }

    public byte EndHour { get; init; }

    /// <summary>Chance of being used, 0-100 (0 = the scenario type's default).</summary>
    public byte Probability { get; init; }

    public uint Flags { get; init; }

    /// <summary>Facing direction as a horizontal unit vector.</summary>
    public Vector3 Direction => new(-MathF.Sin(Heading), MathF.Cos(Heading), 0f);
}

/// <summary>Broad families of scenarios, used for colours and layers.</summary>
public enum ScenarioCategory
{
    /// <summary>Standing activities: smoking, phoning, jogging, drinking...</summary>
    PedActivity,

    /// <summary>Sitting on benches, chairs, walls...</summary>
    PedSeated,

    /// <summary>Parked or driving vehicles.</summary>
    Vehicle,

    /// <summary>Animals: birds, fish, deer, dogs...</summary>
    Animal,

    Other,
}

public static class ScenarioCategories
{
    /// <summary>Guesses the category from the scenario type name, using the game's naming conventions.</summary>
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
