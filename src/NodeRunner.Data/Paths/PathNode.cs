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

    // Change these through PathNetwork.SetValues, like the position.
    public byte Flags0 { get; set; }
    public byte Flags1 { get; set; }
    public byte Flags2 { get; set; }
    public byte Flags3 { get; set; }
    public byte Flags4 { get; set; }

    public byte LinkCountExtra { get; set; }

    public PathNodeValues Values
    {
        get => new(Flags0, Flags1, Flags2, Flags3, Flags4, LinkCountExtra);
        set => (Flags0, Flags1, Flags2, Flags3, Flags4, LinkCountExtra) =
            (value.Flags0, value.Flags1, value.Flags2, value.Flags3, value.Flags4, (byte)(value.LinkCountExtra & 7));
    }

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

// The editable bytes of a node as the .ynd file stores them: flags 0 to 4, and the low 3 bits of the link count byte.
public readonly record struct PathNodeValues(byte Flags0, byte Flags1, byte Flags2, byte Flags3, byte Flags4, byte LinkCountExtra)
{
    public int Speed => (LinkCountExtra >> 1) & 3;
    public int Density => Flags4 & 0x0F;
    public PathNodeSpecialType Special => (PathNodeSpecialType)(Flags1 >> 3);

    public PathNodeValues WithSpeed(int speed) => this with { LinkCountExtra = (byte)((LinkCountExtra & ~6) | ((speed & 3) << 1)) };
    public PathNodeValues WithDensity(int density) => this with { Flags4 = (byte)((Flags4 & 0xF0) | Math.Clamp(density, 0, 15)) };
    public PathNodeValues WithSpecial(PathNodeSpecialType type) => this with { Flags1 = (byte)((Flags1 & 7) | (((int)type & 31) << 3)) };

    public bool Has(PathNodeFlag flag) => flag switch
    {
        PathNodeFlag.OffRoad => (Flags0 & 0x08) != 0,
        PathNodeFlag.NoBigVehicles => (Flags0 & 0x20) != 0,
        PathNodeFlag.SlipRoad => (Flags1 & 0x01) != 0,
        PathNodeFlag.NoGps => (Flags2 & 0x01) != 0,
        PathNodeFlag.Highway => (Flags2 & 0x40) != 0,
        PathNodeFlag.SwitchedOff => (Flags2 & 0x90) != 0,
        PathNodeFlag.Tunnel => (Flags3 & 0x01) != 0,
        _ => false,
    };

    // Switching a node off sets both of the game's bits (current and original state).
    public PathNodeValues With(PathNodeFlag flag, bool on) => flag switch
    {
        PathNodeFlag.OffRoad => this with { Flags0 = Bit(Flags0, 0x08, on) },
        PathNodeFlag.NoBigVehicles => this with { Flags0 = Bit(Flags0, 0x20, on) },
        PathNodeFlag.SlipRoad => this with { Flags1 = Bit(Flags1, 0x01, on) },
        PathNodeFlag.NoGps => this with { Flags2 = Bit(Flags2, 0x01, on) },
        PathNodeFlag.Highway => this with { Flags2 = Bit(Flags2, 0x40, on) },
        PathNodeFlag.SwitchedOff => this with { Flags2 = Bit(Flags2, 0x90, on) },
        PathNodeFlag.Tunnel => this with { Flags3 = Bit(Flags3, 0x01, on) },
        _ => this,
    };

    private static byte Bit(byte value, int mask, bool on) => (byte)(on ? value | mask : value & ~mask);
}

// The on / off settings of a node whose meaning is known.
public enum PathNodeFlag
{
    OffRoad,
    NoBigVehicles,
    SlipRoad,
    NoGps,
    Highway,
    SwitchedOff,
    Tunnel,
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
