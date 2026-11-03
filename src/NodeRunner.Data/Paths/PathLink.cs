namespace NodeRunner.Data.Paths;

/// <summary>A one-way connection from a node to another node (possibly in a neighbouring area).</summary>
public readonly record struct PathLink(PathNodeId Target, byte Flags0, byte Flags1, byte Flags2, byte Length)
{
    /// <summary>Number of lanes going from the source node towards the target.</summary>
    public int LanesForward => (Flags2 >> 5) & 7;

    /// <summary>Number of lanes going from the target back towards the source.</summary>
    public int LanesBackward => (Flags2 >> 2) & 7;

    public bool IsShortcut => (Flags2 & 0x02) != 0;
    public bool DontUseForNavigation => (Flags2 & 0x01) != 0;
    public bool IsNarrowRoad => (Flags1 & 0x02) != 0;
}
