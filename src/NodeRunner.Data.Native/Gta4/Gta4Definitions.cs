using System.Globalization;
using System.Numerics;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Gta4;

// A model declared in a ".ide" file. The box and sphere are in model space; DrawableDictionary names the ".wdd" file
// holding the model ("nj01_slod21_grp"), null when the model has its own ".wdr" or ".wft". TimeFlags: hours shown
// (bit per hour) for the timed sections, 0 otherwise.
public sealed record Gta4ModelDef(string Name, uint Hash, string TextureDictionary, string? AnimationDictionary,
    float DrawDistance, uint Flags, Vector3 BoxMin, Vector3 BoxMax, Vector3 SphereCenter, float SphereRadius,
    string? DrawableDictionary, bool IsTimed, uint TimeFlags = 0);

// Model definitions of GTA IV (".ide" text files): "objs", "tobj" (objs + hours), "anim" (objs with an animation
// dictionary after the texture dictionary), "tanm" (anim + hours) and the texture dictionary parents ("txdp").
public sealed class Gta4Definitions
{
    private readonly Dictionary<uint, Gta4ModelDef> _models = [];
    private readonly Dictionary<string, string> _textureParents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<uint, Gta4ModelDef> Models => _models;

    // Texture dictionary -> its parent dictionary, searched when a texture is missing.
    public IReadOnlyDictionary<string, string> TextureParents => _textureParents;

    public Gta4ModelDef? Find(uint hash) => _models.GetValueOrDefault(hash);

    // Reads one ".ide" file; returns the number of lines that could not be read. A later file replaces earlier models.
    public int Read(string text)
    {
        int bad = 0;
        string section = "";
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment].TrimEnd();
            if (line.IsEmpty)
                continue;
            if (line.IndexOf(',') < 0)
            {
                section = line.Equals("end", StringComparison.OrdinalIgnoreCase) ? "" : line.ToString().ToLowerInvariant();
                continue;
            }
            switch (section)
            {
                case "objs" or "tobj" or "anim" or "tanm":
                    if (ReadModel(line.ToString(), section) is { } model)
                        _models[model.Hash] = model;
                    else
                        bad++;
                    break;
                case "txdp":
                    var parts = Split(line.ToString());
                    if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
                        _textureParents[parts[0]] = parts[1];
                    else
                        bad++;
                    break;
            }
        }
        return bad;
    }

    private static Gta4ModelDef? ReadModel(string line, string section)
    {
        var p = Split(line);
        bool animated = section is "anim" or "tanm";
        bool timed = section is "tobj" or "tanm";
        int o = animated ? 1 : 0;
        if (p.Length < 16 + o || p[0].Length == 0)
            return null;
        if (!TryFloat(p[2 + o], out float drawDistance) || !uint.TryParse(p[3 + o], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags))
            return null;
        Span<float> v = stackalloc float[10];
        for (int i = 0; i < 10; i++)
            if (!TryFloat(p[5 + o + i], out v[i]))
                return null;
        string dictionary = p[15 + o];
        uint hours = 0;
        if (timed && (p.Length < 17 + o || !uint.TryParse(p[16 + o], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)))
            return null;
        return new Gta4ModelDef(p[0], Joaat.Hash(p[0]), p[1], animated ? p[2] : null, drawDistance, flags,
            new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]), new Vector3(v[6], v[7], v[8]), v[9],
            dictionary.Length == 0 || dictionary.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : dictionary, timed, hours);
    }

    private static string[] Split(string line) => line.Split(',', StringSplitOptions.TrimEntries);

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
