using System.Numerics;

namespace NodeRunner.Data.Paths;

/// <summary>
/// A node of the game's road and pedestrian network (one point of a path, stored in a nodesN.ynd file).
/// Flag meanings come from community research; unknown bits are kept in the raw flag bytes.
/// </summary>
public sealed class PathNode
{
    public required PathNodeId Id { get; init; }

    /// <summary>World position in metres.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>Hash of the street name (text label), 0 if none.</summary>
    public uint StreetNameHash { get; init; }

    /// <summary>True for pedestrian nodes, false for vehicle (and boat) nodes.</summary>
    public bool IsPedestrian { get; init; }

    /// <summary>Index of the first outgoing link in <see cref="PathNodeArea.Links"/>.</summary>
    public int FirstLink { get; init; }

    public int LinkCount { get; init; }

    public byte Flags0 { get; init; }
    public byte Flags1 { get; init; }
    public byte Flags2 { get; init; }
    public byte Flags3 { get; init; }
    public byte Flags4 { get; init; }

    /// <summary>The 3 low bits stored next to the link count (bits 1-2 are the speed category).</summary>
    public byte LinkCountExtra { get; init; }

    public bool IsOffRoad => (Flags0 & 0x08) != 0;
    public bool NoBigVehicles => (Flags0 & 0x20) != 0;
    public bool IsSlipRoad => (Flags1 & 0x01) != 0;
    public PathNodeSpecialType Special => (PathNodeSpecialType)(Flags1 >> 3);
    public bool NoGps => (Flags2 & 0x01) != 0;
    public bool IsJunction => (Flags2 & 0x04) != 0;
    public bool IsHighway => (Flags2 & 0x40) != 0;

    /// <summary>"Switched off" by default: AI traffic does not spawn or drive here unless a script enables it.</summary>
    public bool IsSwitchedOff => (Flags2 & 0x80) != 0 || (Flags2 & 0x10) != 0;

    public bool IsTunnel => (Flags3 & 0x01) != 0;

    /// <summary>Traffic / pedestrian density, 0 to 15.</summary>
    public int Density => Flags4 & 0x0F;

    /// <summary>Speed category, 0 (slow) to 3 (fast).</summary>
    public int SpeedCategory => (LinkCountExtra >> 1) & 3;
}

/// <summary>Identifies a node across the whole map: area (file) number + node number in that file.</summary>
public readonly record struct PathNodeId(ushort AreaId, ushort NodeId);

/// <summary>Special node types stored in the top 5 bits of Flags1 (known values only).</summary>
public enum PathNodeSpecialType : byte
{
    None = 0,
    ParkingSpace = 2,
    PedCrossing = 10,
    PedAssistedMovement = 14,
    TrafficLightStop = 15,
    StopSign = 16,
    Caution = 17,
    PedCrossingNoWait = 18,
    EmergencyVehiclesOnly = 19,
    OffRoadJunction = 20,
}
