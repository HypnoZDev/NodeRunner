using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace NodeRunner.Editor;

// A position pasted by the user: X, Y, optionally Z and a heading (degrees, as in FiveM).
public readonly partial record struct Coordinates(float X, float Y, float? Z, float? Heading)
{
    // Reads a position as written in FiveM scripts or copied from elsewhere: vector3(1.0, 2.0, 3.0), vector4(1, 2, 3,
    // 90), vec3(...), {x = 1.0, y = 2.0, z = 3.0}, 1.0, 2.0, 3.0, 1.0 2.0 3.0 or just x, y.
    public static Coordinates? TryParse(string text)
    {
        string cleaned = Wrappers().Replace(text.Trim(), " ");
        var parts = cleaned.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4)
            return null;

        var values = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].EndsWith('f') ? parts[i][..^1] : parts[i];
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i]))
                return null;
        }
        return new Coordinates(values[0], values[1],
            values.Length > 2 ? values[2] : null,
            values.Length > 3 ? values[3] : null);
    }

    public Vector3 ToVector3(float fallbackZ) => new(X, Y, Z ?? fallbackZ);

    public Vector2? HeadingDirection =>
        Heading is { } h ? new Vector2(-MathF.Sin(h * MathF.PI / 180f), MathF.Cos(h * MathF.PI / 180f)) : null;

    // Function names (vector3, vec4...), component names (x =, y:, heading =), brackets and the '=' or ':' left.
    [GeneratedRegex(@"\b(?:vector[234]|vec[234]?)\b|\b(?:[xyzhw]|heading)\s*[=:]|[(){}\[\]=:]", RegexOptions.IgnoreCase)]
    private static partial Regex Wrappers();
}
