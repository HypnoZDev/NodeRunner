using System.Numerics;

namespace NodeRunner.Data.Audio;

public enum AudioZoneShape : byte
{
    Box = 0,
    Sphere = 1,
    Line = 2,
}

// Where an ambient zone applies. Box: centre, size, turned by RotationDegrees around Z. Sphere: radius = Size.X.
// Line: from Centre to Size (the game stores the second end in the size field).
public readonly record struct AudioZoneVolume(Vector3 Centre, Vector3 Size, float RotationDegrees);

// A sound the ambient zone plays in its direction (wind in the trees, distant traffic...).
public readonly record struct DirectionalAmbience(uint Sound, float Volume);

// Ambient zone (game data item 37): the area where its ambient rules play (birds, city noise, water...).
// Activation: where the zone starts playing; Positioning: where its sounds are placed.
public sealed record AmbientZone(uint NameHash, uint Flags, AudioZoneShape Shape, AudioZoneVolume Activation,
    AudioZoneVolume Positioning, float BuiltUpFactor, float MinPedDensity, float MaxPedDensity, uint WindSounds,
    uint EnvironmentRule, uint AudioScene, uint PedWalla, int RulesToPlay, IReadOnlyList<uint> Rules,
    IReadOnlyList<DirectionalAmbience> DirectionalAmbiences, string File);

// Static emitter (game data item 6): a sound or a radio placed at a fixed point (bars, shops, alarms, fans...).
// Volume in hundredths of a decibel; times in minutes after midnight (both 0: always).
public sealed record StaticEmitter(uint NameHash, uint Flags, uint ChildSound, uint RadioStation, Vector3 Position,
    float MinDistance, float MaxDistance, int Volume, uint Interior, uint Room, int StartMinutes, int EndMinutes,
    string File);
