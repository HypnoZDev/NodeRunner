using System.Numerics;

namespace NodeRunner.Data.Maps;

// A parking spot of a map file (CCarGen): the game may park a car there, of the given model or of a model of the
// population group (any model if both are 0).
public sealed record CarGenerator
{
    // Center of the spot.
    public required Vector3 Position { get; init; }

    // Along the length of the car, towards its front; its length is the length of the spot.
    public required Vector2 Orientation { get; init; }

    // Width of the spot.
    public float Width { get; init; }

    public uint CarModel { get; init; }
    public uint Flags { get; init; }

    // Colors of the body, -1 = random.
    public int Color1 { get; init; } = -1;
    public int Color2 { get; init; } = -1;
    public int Color3 { get; init; } = -1;
    public int Color4 { get; init; } = -1;

    public uint PopGroup { get; init; }

    // -1 = random.
    public int Livery { get; init; } = -1;

    // Place in the map file's list, -1 for a spot added by the user.
    public int SourceIndex { get; init; } = -1;

    public float Length => Orientation.Length();

    // Heading of the car, in radians from the X axis (east), counterclockwise.
    public float Angle => MathF.Atan2(Orientation.Y, Orientation.X);

    // Footprint corners, counterclockwise from the back right.
    public Vector3[] Corners()
    {
        var forward = Length > 1e-4f ? Vector2.Normalize(Orientation) : Vector2.UnitX;
        var right = new Vector2(forward.Y, -forward.X);
        var f = new Vector3(forward * (Length / 2), 0);
        var r = new Vector3(right * (Width / 2), 0);
        return [Position - f + r, Position + f + r, Position + f - r, Position - f - r];
    }
}
