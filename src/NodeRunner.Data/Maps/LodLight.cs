using System.Numerics;

namespace NodeRunner.Data.Maps;

// A light seen from far away (CDistantLODLightsSOA of a distlodlights_*.ymap): where it is and its colour.
public readonly record struct DistantLodLight(Vector3 Position, uint Rgbi)
{
    public byte Red => (byte)(Rgbi >> 16);
    public byte Green => (byte)(Rgbi >> 8);
    public byte Blue => (byte)Rgbi;
    public byte Intensity => (byte)(Rgbi >> 24);
}

public enum LodLightType
{
    Point = 1,
    Spot = 2,
    Capsule = 4,
}

// The details of a distant light (CLODLightsSOA of a lodlights_*.ymap), in the same order as the lights of its parent
// distlodlights file.
public readonly record struct LodLight(
    Vector3 Direction,
    float Falloff,
    float FalloffExponent,
    uint TimeAndStateFlags,
    uint Hash,
    byte ConeInnerAngle,
    byte ConeOuterAngleOrCapExt,
    byte CoronaIntensity)
{
    // Bits 26-28 (measured: 1, 2 or 4 on all the game's lights).
    public LodLightType Type => (LodLightType)((TimeAndStateFlags >> 26) & 7);

    // One bit per hour of the day (bits 0 to 23): the light is on during that hour.
    public uint HourMask => TimeAndStateFlags & 0xFFFFFF;

    public bool IsOnAt(int hour) => (HourMask >> hour & 1) != 0;
}
