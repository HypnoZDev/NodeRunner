using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Scenarios;

// Whether the game would use a scenario point at a given hour: its hours, where it is available, and a draw against its
// probability that stays the same for a given point and hour.
public static class ScenarioSchedule
{
    // Steps of a chain that a ped or vehicle passes through, not places where one appears.
    public static bool IsChainStep(ScenarioPoint point) =>
        point.TypeName is { } name && (name.Equals("Walk", StringComparison.OrdinalIgnoreCase) || name.Equals("DRIVE", StringComparison.OrdinalIgnoreCase));

    // Hours from 0 to 24; the same start and end = all day; an end before the start goes past midnight.
    public static bool IsOpenAt(ScenarioPoint point, int hour)
    {
        int start = point.StartHour % 24, end = point.EndHour % 24;
        hour = (hour % 24 + 24) % 24;
        if (start == end)
            return true;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    public static bool IsAvailable(ScenarioPoint point, bool online) => point.Availability switch
    {
        1 => online,
        2 => !online,
        _ => true,
    };

    // From 0 to 1: the point's own, else its type's.
    public static float Probability(ScenarioPoint point, ScenarioSet scenarios) =>
        point.Probability > 0 ? Math.Min((int)point.Probability, 100) / 100f : scenarios.TypeSpawnProbability(point.TypeHash);

    public static bool IsUsed(ScenarioPoint point, ScenarioSet scenarios, int hour, bool online) =>
        !IsChainStep(point) && IsOpenAt(point, hour) && IsAvailable(point, online) && Draw(point, hour) < Probability(point, scenarios);

    // A number from 0 to 1 proper to the point and the hour; the points of a cluster share it, as they appear together.
    public static float Draw(ScenarioPoint point, int hour)
    {
        uint region = Joaat.Hash(point.Region?.Name ?? "");
        int cluster = point.Region?.GroupOf(point.Index) ?? -1;
        uint key = cluster >= 0 ? 0x80000000u | (uint)cluster : (uint)point.Index;
        uint h = region ^ key * 0x9E3779B1u ^ (uint)((hour % 24 + 24) % 24 + 1) * 0x85EBCA6Bu;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (h >> 8) / 16777216f;
    }
}
