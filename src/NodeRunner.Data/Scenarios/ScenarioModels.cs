using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.Data.Scenarios;

// Where the model shown on a scenario point comes from.
public enum ScenarioModelSource
{
    // The point's model set.
    Point,

    // The model set of its scenario type (scenarios.meta).
    Type,

    // Neither names one: the game takes a ped or vehicle of the area; an ordinary one stands in.
    Example,
}

// Picks a ped, animal or vehicle the game could spawn on a scenario point, always the same for a given point.
public static class ScenarioModels
{
    public static readonly string[] ExampleVehicles = ["asea", "premier", "tailgater", "primo", "stanier", "fugitive", "ingot", "washington"];

    public static readonly string[] ExamplePeds =
    [
        "a_m_y_business_01", "a_f_y_business_01", "a_m_m_business_01", "a_f_m_bevhills_01", "a_m_y_hipster_01",
        "a_f_y_hipster_01", "a_m_y_downtown_01", "a_f_y_tourist_01",
    ];

    // Set names a point carries when it leaves the choice to its type or to the area.
    private static readonly HashSet<uint> NoSet = [0, Joaat.Hash("none"), Joaat.Hash("usepopulation")];

    // exists: whether the model (lower-case name) can be read (.ydd for a ped, .yft for a vehicle). variant: a number
    // proper to the point.
    public static (string Name, ScenarioModelSource Source)? Choose(ScenarioPoint point, ScenarioSet scenarios, ModelSets pedSets,
        ModelSets vehicleSets, Func<string, bool> exists, int variant)
    {
        bool vehicle = point.Category == ScenarioCategory.Vehicle;
        var sets = vehicle ? vehicleSets : pedSets;
        uint typeSet = vehicle ? scenarios.TypeVehicleModelSet(point.TypeHash) : scenarios.TypePedModelSet(point.TypeHash);
        foreach (var (set, source) in new[] { (point.ModelSetHash, ScenarioModelSource.Point), (typeSet, ScenarioModelSource.Type) })
        {
            if (NoSet.Contains(set))
                continue;
            var models = sets.ModelsOf(set).Where(exists).ToList();
            if (models.Count > 0)
                return (models[(int)((uint)variant % (uint)models.Count)], source);
        }
        var examples = (vehicle ? ExampleVehicles : ExamplePeds).Where(exists).ToList();
        return examples.Count > 0 ? (examples[(int)((uint)variant % (uint)examples.Count)], ScenarioModelSource.Example) : null;
    }
}
