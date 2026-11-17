using System.Globalization;

namespace NodeRunner.Data.Native.Classic;

// A vehicle declared in the "cars" section of a ".ide" file: number, model and texture dictionary. Cars of GTA III and
// Vice City name the model of their wheels (taken from "models\generic\wheels.dff") and its size; -1 when the vehicle
// file holds its own wheel (San Andreas).
public sealed record ClassicVehicleDef(int Id, string Name, string TextureDictionary, int WheelModel = -1, float WheelScale = 1f);

// A model declared in a ".ide" file. Timed models ("tobj") are only shown from TimeOn to TimeOff (hours, may wrap past
// midnight). AnimationFile: San Andreas "anim" section (animated buildings).
public sealed record ClassicModelDef(int Id, string Name, string TextureDictionary, float DrawDistance, uint Flags,
    bool IsTimed = false, int TimeOn = 0, int TimeOff = 24, string? AnimationFile = null);

// Model definitions of GTA III / Vice City / San Andreas (".ide" text files): "objs", "tobj" and "anim", and the texture
// dictionary parents of San Andreas ("txdp"). A model has a number (used by the binary placement files) and a name.
// "objs" lines come in two layouts:
// - GTA III / Vice City: id, model, txd, mesh count (1 to 3), one draw distance per mesh, flags;
// - San Andreas: id, model, txd, draw distance, flags (the older layout is still accepted).
public sealed class ClassicDefinitions
{
    private readonly Dictionary<int, ClassicModelDef> _byId = [];
    private readonly Dictionary<string, ClassicModelDef> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _textureParents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ClassicVehicleDef> _vehicles = [];

    public IReadOnlyCollection<ClassicVehicleDef> Vehicles => _vehicles.Values;

    public ClassicVehicleDef? FindVehicle(string name) =>
        _vehicles.Values.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyDictionary<int, ClassicModelDef> Models => _byId;

    public IReadOnlyDictionary<string, string> TextureParents => _textureParents;

    public ClassicModelDef? Find(int id) => _byId.GetValueOrDefault(id);

    public ClassicModelDef? Find(string name) => _byName.GetValueOrDefault(name);

    // GTA III / Vice City: the low detail model of a building is the one named "LOD" + its name from the 4th
    // character ("LODroad01" for "barroad01").
    public ClassicModelDef? LodModelOf(ClassicModelDef model) =>
        model.Name.Length > 3 && !IsLodName(model.Name) ? Find("LOD" + model.Name[3..]) : null;

    public static bool IsLodName(string name) => name.StartsWith("LOD", StringComparison.OrdinalIgnoreCase);

    // Reads one ".ide" file; returns the number of lines that could not be read. A later definition of the same number
    // replaces the earlier one.
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
            if (line.IndexOf(',') < 0 && line.IndexOfAny(' ', '\t') < 0)
            {
                section = line.Equals("end", StringComparison.OrdinalIgnoreCase) ? "" : line.ToString().ToLowerInvariant();
                continue;
            }
            switch (section)
            {
                case "objs" or "tobj" or "anim":
                    if (ReadModel(Split(line.ToString()), section) is { } model)
                        Add(model);
                    else
                        bad++;
                    break;
                case "cars":
                    var car = Split(line.ToString());
                    if (car.Length >= 3 && int.TryParse(car[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int carId) && car[1].Length > 0)
                        _vehicles[carId] = ReadVehicle(carId, car);
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

    // Cars only: wheel model and size are the last two fields in GTA III / Vice City, fields 12 and 13 in San Andreas.
    private static ClassicVehicleDef ReadVehicle(int id, string[] p)
    {
        int wheel = -1;
        float scale = 1f;
        if (p.Length >= 12 && p[3].Equals("car", StringComparison.OrdinalIgnoreCase))
        {
            int at = p.Length >= 15 ? 11 : p.Length - 2;
            if (int.TryParse(p[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && TryFloat(p[at + 1], out float s) && s > 0f)
                (wheel, scale) = (w, s);
        }
        return new ClassicVehicleDef(id, p[1], p[2], wheel, scale);
    }

    private void Add(ClassicModelDef model)
    {
        if (_byId.TryGetValue(model.Id, out var previous))
            _byName.Remove(previous.Name);
        _byId[model.Id] = model;
        _byName[model.Name] = model;
    }

    internal static ClassicModelDef? ReadModel(string[] p, string section)
    {
        if (p.Length < 5 || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || p[1].Length == 0)
            return null;
        string? animation = null;
        int next = 3;
        if (section == "anim")
        {
            animation = p[3];
            next = 4;
        }
        float distance;
        // Older layout: a mesh count then that many draw distances (the furthest is kept).
        if (section != "anim" && p.Length >= 6 && int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meshes)
            && meshes is >= 1 and <= 3 && p.Length >= 5 + meshes + (section == "tobj" ? 2 : 0)
            && !p[3].Contains('.'))
        {
            distance = 0;
            for (int i = 0; i < meshes; i++)
            {
                if (!TryFloat(p[4 + i], out float d))
                    return null;
                distance = Math.Max(distance, d);
            }
            next = 4 + meshes;
        }
        else
        {
            if (p.Length <= next || !TryFloat(p[next], out distance))
                return null;
            next++;
        }
        if (p.Length <= next || !uint.TryParse(p[next], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flags))
            return null;
        next++;
        if (section != "tobj")
            return new ClassicModelDef(id, p[1], p[2], distance, flags, AnimationFile: animation);
        if (p.Length < next + 2 || !int.TryParse(p[next], NumberStyles.Integer, CultureInfo.InvariantCulture, out int on)
            || !int.TryParse(p[next + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int off))
            return null;
        return new ClassicModelDef(id, p[1], p[2], distance, flags, true, on, off);
    }

    // Fields separated by commas and/or spaces.
    internal static string[] Split(string line) =>
        line.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
