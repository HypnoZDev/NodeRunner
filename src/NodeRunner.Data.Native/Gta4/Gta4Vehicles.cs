using System.Globalization;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.Data.Native.Gta4;

// A vehicle of "vehicles.ide" (section "cars").
public sealed record Gta4VehicleDef(string Model, string TextureDictionary, string Type, string HandlingId, string GameName,
    string Anims, int Frequency, int MaxNumber, float WheelScaleFront, float WheelScaleRear, float DirtLevel, int Swankness,
    float LodMultiplier, string Flags, string Source);

// What GTA IV says of its vehicles besides their models: the palette and the colours planned for each one
// ("carcols.dat"), and their definitions ("vehicles.ide"). An episode adds colours after the base palette and replaces
// the lines of the vehicles it names.
public sealed class Gta4Vehicles
{
    public static readonly Gta4Vehicles Empty = new() { Paints = VehiclePaints.Empty, Definitions = new Dictionary<uint, Gta4VehicleDef>() };

    public required VehiclePaints Paints { get; init; }

    public required IReadOnlyDictionary<uint, Gta4VehicleDef> Definitions { get; init; }

    public List<string> Errors { get; } = [];

    public Gta4VehicleDef? Find(uint modelHash) => Definitions.GetValueOrDefault(modelHash);

    public static Gta4Vehicles Load(Gta4FileSystem files, Gta4Episode episode)
    {
        var folders = new List<string> { "" };
        if (Gta4Map.EpisodeFolder(episode) is { Length: > 0 } folder)
            folders.Add(folder + "\\");
        var colors = new List<VehicleColor>();
        var variations = new Dictionary<uint, VehicleVariation>();
        var definitions = new Dictionary<uint, Gta4VehicleDef>();
        var errors = new List<string>();
        foreach (string prefix in folders)
        {
            string carcols = prefix + @"common\data\carcols.dat";
            string ide = prefix + @"common\data\vehicles.ide";
            try
            {
                if (files.ReadFile(carcols) is { } data)
                    ReadCarcols(Encoding.ASCII.GetString(data), carcols, colors, variations);
                if (files.ReadFile(ide) is { } list)
                    ReadDefinitions(Encoding.ASCII.GetString(list), ide, definitions);
            }
            catch (IOException ex)
            {
                errors.Add(ex.Message);
            }
        }
        var vehicles = new Gta4Vehicles
        {
            Paints = VehiclePaints.Create(colors, variations),
            Definitions = definitions,
        };
        vehicles.Errors.AddRange(errors);
        return vehicles;
    }

    // Sections "col" (r, g, b, prefix, colour # number name), "car3" and "car4" (model, then 3 or 4 numbers per
    // combination: colours 1, 2, 3 and 4).
    private static void ReadCarcols(string text, string source, List<VehicleColor> colors, Dictionary<uint, VehicleVariation> variations)
    {
        string? section = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (section is null)
            {
                if (line.Equals("col", StringComparison.OrdinalIgnoreCase) || line.Equals("car3", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("car4", StringComparison.OrdinalIgnoreCase))
                    section = line.ToLowerInvariant();
                continue;
            }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                section = null;
                continue;
            }
            int hash = line.IndexOf('#');
            string comment = hash >= 0 ? line[(hash + 1)..].Trim() : "";
            string[] fields = (hash >= 0 ? line[..hash] : line).Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (section == "col")
            {
                if (fields.Length < 3 || !Byte(fields[0], out int r) || !Byte(fields[1], out int g) || !Byte(fields[2], out int b))
                    continue;
                string name = ColorName(comment, fields);
                colors.Add(new VehicleColor(colors.Count, name, new Vector3(r, g, b) / 255f,
                    name.Contains("poly", StringComparison.OrdinalIgnoreCase) ? VehicleFinish.Metallic : VehicleFinish.Other, null));
                continue;
            }
            if (fields.Length < 2)
                continue;
            int size = section == "car4" ? 4 : 3;
            var numbers = fields.Skip(1).Select(f => int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1).ToList();
            var sets = new List<VehicleColorSet>();
            for (int i = 0; i + size <= numbers.Count; i += size)
                sets.Add(new VehicleColorSet(numbers.GetRange(i, size)));
            if (sets.Count > 0)
                variations[Joaat.Hash(fields[0].ToLowerInvariant())] = new VehicleVariation(fields[0], sets, [], [], 0, 0, source);
        }
    }

    // "# 12 astra silver poly" gives "astra silver poly"; without a comment, the words of the police scanner.
    private static string ColorName(string comment, string[] fields)
    {
        int space = comment.IndexOf(' ');
        if (comment.Length > 0 && char.IsDigit(comment[0]))
            comment = space > 0 ? comment[(space + 1)..].Trim() : "";
        if (comment.Length > 0)
            return comment;
        return string.Join(' ', fields.Skip(3).Where(f => f != "-"));
    }

    private static bool Byte(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 255;

    // Section "cars": model, textures, type, handling, game name, anims, anims 2, frequency, maximum number, wheel
    // scales front and rear, dirt level, swankness, LOD multiplier, (unused), flags.
    private static void ReadDefinitions(string text, string source, Dictionary<uint, Gta4VehicleDef> definitions)
    {
        bool cars = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (!cars)
            {
                cars = line.Equals("cars", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
                break;
            string[] f = line.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9)
                continue;
            string At(int i) => i < f.Length ? f[i] : "";
            int Int(int i) => int.TryParse(At(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            float Float(int i) => float.TryParse(At(i), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
            definitions[Joaat.Hash(f[0].ToLowerInvariant())] = new Gta4VehicleDef(f[0], At(1), At(2), At(3), At(4), At(5), Int(7), Int(8),
                Float(9), Float(10), Float(11), Int(12), Float(13), f.Length > 15 ? f[15] : "", source);
        }
    }
}
