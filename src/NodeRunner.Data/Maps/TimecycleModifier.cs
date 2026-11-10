using System.Numerics;

namespace NodeRunner.Data.Maps;

// A zone of a map file (CTimeCycleModifier) where the game changes the look of the picture (light, colours, fog) with a
// modifier of the timecycle files (timecycle_mods_*.xml), by name hash.
public sealed record TimecycleModifier
{
    public required uint NameHash { get; init; }

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    // Strength of the modifier inside the box, 0 to 100.
    public float Percentage { get; init; }

    // Distance (m) around the box over which the modifier fades in.
    public float Range { get; init; }

    // Hours of the day (0 to 23) when it applies.
    public uint StartHour { get; init; }
    public uint EndHour { get; init; }

    // Place in the map file's list, -1 for a zone added by the user.
    public int SourceIndex { get; init; } = -1;

    public bool IsDeleted { get; init; }

    public Vector3 Center => (BoundsMin + BoundsMax) / 2;
    public Vector3 Size => BoundsMax - BoundsMin;
}
