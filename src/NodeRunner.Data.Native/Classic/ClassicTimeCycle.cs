using System.Globalization;
using System.Numerics;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Classic;

// Colours of one moment of the day for one weather ("data\timecyc.dat"), 0 to 1. Ambient: light added to the
// pre-lit colours of the buildings; AmbientObjects: of the objects without pre-lit colours (vehicles...).
public readonly record struct ClassicTimeKey(float Hour, Vector3 Ambient, Vector3 AmbientObjects, Vector3 Directional,
    Vector3 SkyTop, Vector3 SkyBottom, Vector3 SunCore, float FarClip, float FogStart);

// The time cycle of GTA III, Vice City or San Andreas: per weather, the colours of the light and of the sky through
// the day. GTA III and Vice City give the 24 hours of each weather in a row (columns differ: Vice City adds the
// objects' ambient and the "bl" variants); San Andreas gives 8 moments (0, 5, 6, 7, 12, 19, 20, 22 h) of each named
// weather, with its own columns.
public sealed class ClassicTimeCycle
{
    private static readonly float[] SanAndreasHours = [0, 5, 6, 7, 12, 19, 20, 22];

    private readonly Dictionary<string, ClassicTimeKey[]> _weathers;

    public ClassicGame Game { get; }

    public IReadOnlyList<string> Weathers { get; }

    private ClassicTimeCycle(ClassicGame game, List<(string Name, ClassicTimeKey[] Keys)> weathers)
    {
        Game = game;
        _weathers = new Dictionary<string, ClassicTimeKey[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, keys) in weathers)
            _weathers.TryAdd(name, keys);
        Weathers = weathers.Select(w => w.Name).ToList();
    }

    public bool Has(string weather) => _weathers.ContainsKey(weather);

    public static ClassicTimeCycle? Load(ClassicFileSystem files, ClassicGame game) =>
        files.ReadFile(@"data\timecyc.dat") is { } data ? Parse(System.Text.Encoding.ASCII.GetString(data), game) : null;

    public static ClassicTimeCycle Parse(string text, ClassicGame game)
    {
        bool sanAndreas = game == ClassicGame.SanAndreas;
        int minimum = game switch { ClassicGame.Gta3 => 26, ClassicGame.ViceCity => 35, _ => 29 };
        var weathers = new List<(string, ClassicTimeKey[])>();
        var keys = new List<ClassicTimeKey>();
        string name = "";

        void Close()
        {
            if (keys.Count > 0)
                weathers.Add((name.Length > 0 ? name : $"{weathers.Count}", [.. keys]));
            keys.Clear();
            name = "";
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                // San Andreas: "//////////// SUNNY_LA" opens a weather (the other games' titles are not reliable).
                if (sanAndreas && line.StartsWith("////", StringComparison.Ordinal) && line.TrimStart('/').Trim() is { Length: > 0 } title
                    && title.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    Close();
                    name = title;
                }
                continue;
            }
            var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < minimum)
                continue;
            var v = new float[values.Length];
            bool ok = true;
            for (int i = 0; i < values.Length && ok; i++)
                ok = float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
            if (!ok)
                continue;
            int hour = keys.Count;
            keys.Add(ToKey(v, game, sanAndreas ? (hour < SanAndreasHours.Length ? SanAndreasHours[hour] : 23f) : hour));
            if (keys.Count == (sanAndreas ? SanAndreasHours.Length : 24))
                Close();
        }
        Close();
        return new ClassicTimeCycle(game, weathers);
    }

    private static ClassicTimeKey ToKey(float[] v, ClassicGame game, float hour)
    {
        Vector3 C(int at) => new Vector3(v[at], v[at + 1], v[at + 2]) / 255f;
        return game switch
        {
            // Amb, Dir, SkyTop, SkyBot, SunCore, SunCorona, SunSz, SprSz, SprBght, Shdw, LightShd, TreeShd, FarClp, FogSt.
            ClassicGame.Gta3 => new ClassicTimeKey(hour, C(0), C(0), C(3), C(6), C(9), C(12), v[24], v[25]),
            // Amb, Amb_Obj, Amb_bl, Amb_Obj_bl, Dir, SkyTop, SkyBot, SunCore, SunCorona, SunSz, SprSz, SprBght, Shdw,
            // LightShd, PoleShd, FarClp, FogSt.
            ClassicGame.ViceCity => new ClassicTimeKey(hour, C(0), C(3), C(12), C(15), C(18), C(21), v[33], v[34]),
            // Amb, Amb_Obj, Dir, SkyTop, SkyBot, SunCore, SunCorona, SunSz, SprSz, SprBght, Shdw, LightShd, PoleShd,
            // FarClp, FogSt.
            _ => new ClassicTimeKey(hour, C(0), C(3), C(6), C(9), C(12), C(15), v[27], v[28]),
        };
    }

    // The colours at hour (0 to 24) of a weather (the first one if unknown), blended between the two nearest moments.
    public ClassicTimeKey Sample(string weather, float hour)
    {
        if (!_weathers.TryGetValue(weather, out var keys))
            keys = _weathers.Count > 0 ? _weathers[Weathers[0]] : [];
        if (keys.Length == 0)
            return default;
        hour = ((hour % 24f) + 24f) % 24f;
        int next = Array.FindIndex(keys, k => k.Hour > hour);
        var b = next < 0 ? keys[0] : keys[next];
        var a = next < 0 ? keys[^1] : next == 0 ? keys[^1] : keys[next - 1];
        float span = (b.Hour - a.Hour + 24f) % 24f;
        float t = span <= 0f ? 0f : ((hour - a.Hour + 24f) % 24f) / span;
        return Blend(a, b, t) with { Hour = hour };
    }

    public static ClassicTimeKey Blend(ClassicTimeKey a, ClassicTimeKey b, float t) => new(
        float.Lerp(a.Hour, b.Hour, t),
        Vector3.Lerp(a.Ambient, b.Ambient, t),
        Vector3.Lerp(a.AmbientObjects, b.AmbientObjects, t),
        Vector3.Lerp(a.Directional, b.Directional, t),
        Vector3.Lerp(a.SkyTop, b.SkyTop, t),
        Vector3.Lerp(a.SkyBottom, b.SkyBottom, t),
        Vector3.Lerp(a.SunCore, b.SunCore, t),
        float.Lerp(a.FarClip, b.FarClip, t),
        float.Lerp(a.FogStart, b.FogStart, t));

    // The weather the game shows by default in good weather where the camera stands: GTA III and Vice City "sunny"
    // (the first one); San Andreas by area (Los Santos, San Fierro, Las Venturas, desert, countryside).
    public string SunnyWeatherAt(Vector3 position)
    {
        if (Game != ClassicGame.SanAndreas)
            return Weathers.Count > 0 ? Weathers[0] : "";
        float x = position.X, y = position.Y;
        string name = x > 44f && y < -768f ? "SUNNY_LA"
            : x < -1213f && y > -1115f && y < 1659f ? "SUNNY_SF"
            : x > 869f && y > 596f ? "SUNNY_VEGAS"
            : y > 596f ? "SUNNY_DESERT"
            : "SUNNY_COUNTRYSIDE";
        return Has(name) ? name : Weathers.Count > 0 ? Weathers[0] : "";
    }
}
