using System.Numerics;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Models;

// Which paint colour a part of a vehicle takes, from its shader's matDiffuseColor: (2, n, n) = colour slot n
// (measured on the 1 430 vehicles of the game), anything else = its own colour.
public enum VehiclePaintSlot
{
    None = 0,
    Primary = 1,
    Secondary = 2,
    Pearlescent = 3,
    Wheel = 4,
    Unpainted = 5,
    InteriorTrim = 6,
    Dashboard = 7,
}

// One colour of the game's palette (carcols): its number in scripts, its name, its colour.
// Ramp: a colour-shifting paint drawn from a gradient texture (no plain colour in the file).
public sealed record VehicleColor(int Index, string Name, Vector3 Color, VehicleFinish Finish, string? RampTexture)
{
    // The family, from the name the game gives the colour ("Metallic Black", "Matte Red", "Util Silver"...).
    public static VehicleFinish FinishOf(string name, string? ramp)
    {
        if (ramp is not null)
            return VehicleFinish.Chameleon;
        foreach (var (word, finish) in FinishWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                return finish;
        }
        return VehicleFinish.Other;
    }

    private static readonly (string, VehicleFinish)[] FinishWords =
    [
        ("Chrome", VehicleFinish.Chrome), ("Matte", VehicleFinish.Matte), ("Util", VehicleFinish.Util),
        ("Worn", VehicleFinish.Worn), ("Brushed", VehicleFinish.Metal), ("Metal", VehicleFinish.Metallic),
        ("Pure Gold", VehicleFinish.Metal), ("Gold", VehicleFinish.Metal), ("Epsilon", VehicleFinish.Other),
    ];
}

public enum VehicleFinish
{
    Metallic,
    Matte,
    Util,
    Worn,
    Metal,
    Chrome,
    Chameleon,
    Other,
}

// A colour combination of a vehicle (carvariations): primary, secondary, pearlescent, wheels, then interior trim
// and dashboard on recent vehicles.
public sealed record VehicleColorSet(IReadOnlyList<int> Indices)
{
    public int this[int slot] => slot < Indices.Count ? Indices[slot] : -1;
}

// What carvariations says of one model.
public sealed record VehicleVariation(string? ModelName, IReadOnlyList<VehicleColorSet> ColorSets, IReadOnlyList<string> Kits,
    IReadOnlyList<(string Plate, int Weight)> Plates, int LightSettings, int SirenSettings, string? Source);

public interface IVehiclePaintReader
{
    // The palette of a carcols file (empty if it has none: the DLC ones only bring tuning kits).
    IReadOnlyList<VehicleColor> ReadColors(byte[] data);

    // The vehicles of a carvariations file, keyed by model name hash.
    IReadOnlyList<(uint ModelHash, VehicleVariation Variation)> ReadVariations(byte[] data);
}

// The game's vehicle palette and the colours planned for each vehicle.
public sealed class VehiclePaints
{
    public static readonly VehiclePaints Empty = new([], new Dictionary<uint, VehicleVariation>(), []);

    private readonly Dictionary<uint, VehicleVariation> _variations;

    private VehiclePaints(IReadOnlyList<VehicleColor> colors, Dictionary<uint, VehicleVariation> variations, List<string> errors)
    {
        Colors = colors;
        _variations = variations;
        Errors = errors;
    }

    public IReadOnlyList<VehicleColor> Colors { get; }

    public int VariationCount => _variations.Count;

    public IReadOnlyList<string> Errors { get; }

    public VehicleColor? Color(int index) => index >= 0 && index < Colors.Count ? Colors[index] : null;

    public VehicleVariation? VariationOf(uint modelHash) => _variations.GetValueOrDefault(modelHash);

    public static VehiclePaintSlot SlotOf(Vector4? diffuseColor) =>
        diffuseColor is { X: 2f } c && c.Y == c.Z && c.Y is >= 1f and <= 7f && c.Y == MathF.Floor(c.Y)
            ? (VehiclePaintSlot)(int)c.Y
            : VehiclePaintSlot.None;

    // Every carcols and carvariations of the active packs, oldest first: palettes add up (carcols_gen9 brings the
    // colours after 160), a vehicle's colours come from the last file naming it.
    public static VehiclePaints Load(GameContent content, IVehiclePaintReader reader)
    {
        var errors = new List<string>();
        var colors = new List<VehicleColor>();
        var variations = new Dictionary<uint, VehicleVariation>();
        var files = content.EnumerateAllVersions(".ymt").Concat(content.EnumerateAllVersions(".meta"))
            .Where(p => IsFile(p, "carcols") || IsFile(p, "carvariations"))
            .OrderBy(p => IsFile(p, "carcols") && p.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        foreach (var path in files)
        {
            try
            {
                if (content.ReadFile(path) is not { } data)
                    continue;
                if (IsFile(path, "carcols"))
                {
                    // Most DLC carcols.meta only bring tuning kits (several MB of XML): not parsed.
                    if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && data.AsSpan().IndexOf("<Colors"u8) < 0)
                        continue;
                    // The update's carcols.ymt replaces the base game's.
                    if (path.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase))
                        colors.Clear();
                    foreach (var color in reader.ReadColors(data))
                        colors.Add(color with { Index = colors.Count });
                }
                else
                {
                    foreach (var (hash, variation) in reader.ReadVariations(data))
                        variations[hash] = variation with { Source = path };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            }
        }
        return new VehiclePaints(colors, variations, errors);
    }

    private static bool IsFile(string path, string name) =>
        Path.GetFileNameWithoutExtension(path).StartsWith(name, StringComparison.OrdinalIgnoreCase);
}
