using System.Numerics;

namespace NodeRunner.Data.Paths;

// A node of the game's road and pedestrian network (one point of a path, stored in a nodesN.ynd file).
public sealed class PathNode
{
    public required PathNodeId Id { get; init; }

    // Change it through PathNetwork.MoveNode, so that the network's Revision follows.
    public required Vector3 Position { get; set; }

    // Where the game's file puts the node (the link lengths stored by the game go with these positions).
    public Vector3 GamePosition { get; set; }

    public uint StreetNameHash { get; init; }

    public bool IsPedestrian { get; init; }

    public int FirstLink { get; init; }

    public int LinkCount { get; init; }

    public byte Flags0 { get; init; }
    public byte Flags1 { get; init; }
    public byte Flags2 { get; init; }
    public byte Flags3 { get; init; }
    public byte Flags4 { get; init; }

    public byte LinkCountExtra { get; init; }

    public bool IsOffRoad => (Flags0 & 0x08) != 0;
    public bool NoBigVehicles => (Flags0 & 0x20) != 0;
    public bool IsSlipRoad => (Flags1 & 0x01) != 0;
    public PathNodeSpecialType Special => (PathNodeSpecialType)(Flags1 >> 3);
    public bool NoGps => (Flags2 & 0x01) != 0;
    public bool IsJunction => (Flags2 & 0x04) != 0;
    public bool IsHighway => (Flags2 & 0x40) != 0;

    public bool IsSwitchedOff => (Flags2 & 0x80) != 0 || (Flags2 & 0x10) != 0;

    public bool IsTunnel => (Flags3 & 0x01) != 0;

    public int Density => Flags4 & 0x0F;

    public int SpeedCategory => (LinkCountExtra >> 1) & 3;
}

// Identifies a node across the whole map: area (file) number + node number in that file.
public readonly record struct PathNodeId(ushort AreaId, ushort NodeId);

// Special node types stored in the top 5 bits of Flags1 (known values only).
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
