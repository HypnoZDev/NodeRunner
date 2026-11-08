using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Models;

// Reader for carcols (palette) and carvariations (colours of each vehicle): .ymt in PSO in the base game, .meta XML in
// the DLC packs. The base game's PSO files name some fields with other hashes than the XML names: given here.
public sealed partial class VehiclePaintReader : IVehiclePaintReader
{
    private static readonly string[] Names =
    [
        "Colors", "color", "colorName", "rampTextureName", "variationData", "modelName", "indices", "liveries", "kits",
        "plateProbabilities", "Probabilities", "Name", "Value", "lightSettings", "sirenSettings",
        "Standard White", "White Plate 2", "Blue Plate", "Yellow Plate", "Blue Plate 2", "Yellow Plate 2",
        "Yankton Plate", "eCola Plate", "Las Venturas Plate", "Liberty City Plate", "Los Santos Plate",
    ];

    private static readonly Dictionary<uint, string> PsoNames = new()
    {
        [0x2B020DDD] = "Colors",
        [0xD8BC1C53] = "colorName",
        [0xCEAE9967] = "variationData",
        [0x0B939623] = "modelName",
        [0x2D3FBA3F] = "plateProbabilities",
        [0xFD703EAE] = "Probabilities",
        [0xACEC22BE] = "Name",
        [0xF4E53011] = "Value",
        [0x8DB32D08] = "lightSettings",
        [0x84EB9B0D] = "sirenSettings",
    };

    public IReadOnlyList<VehicleColor> ReadColors(byte[] data)
    {
        var colors = new List<VehicleColor>();
        foreach (var item in Child(Parse(data), "Colors")?.Elements() ?? [])
        {
            string name = CleanName(Child(item, "colorName")?.Value ?? "");
            string? ramp = Child(item, "rampTextureName")?.Value.Trim() is { Length: > 0 } r ? r : null;
            uint argb = ParseUInt(Value(Child(item, "color")));
            var rgb = new Vector3((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF) / 255f;
            colors.Add(new VehicleColor(colors.Count, name, rgb, VehicleColor.FinishOf(name, ramp), ramp));
        }
        return colors;
    }

    public IReadOnlyList<(uint ModelHash, VehicleVariation Variation)> ReadVariations(byte[] data)
    {
        var result = new List<(uint, VehicleVariation)>();
        foreach (var item in Child(Parse(data), "variationData")?.Elements() ?? [])
        {
            string? model = Child(item, "modelName")?.Value.Trim();
            if (string.IsNullOrEmpty(model))
                continue;
            var (modelHash, modelName) = HashOf(model);
            var sets = new List<VehicleColorSet>();
            foreach (var set in Child(item, "colors")?.Elements() ?? [])
            {
                var indices = (Child(set, "indices")?.Value ?? "")
                    .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
                    .ToList();
                if (indices.Count > 0)
                    sets.Add(new VehicleColorSet(indices));
            }
            var kits = (Child(item, "kits")?.Elements() ?? []).Select(k => k.Value.Trim()).Where(k => k.Length > 0).ToList();
            var plates = new List<(string, int)>();
            foreach (var plate in Child(Child(item, "plateProbabilities"), "Probabilities")?.Elements() ?? [])
            {
                string name = Child(plate, "Name")?.Value.Trim() ?? "";
                plates.Add((name.StartsWith("hash_", StringComparison.Ordinal) ? "0x" + name[5..] : name,
                    (int)ParseUInt(Value(Child(plate, "Value")))));
            }
            result.Add((modelHash, new VehicleVariation(modelName, sets, kits, plates,
                (int)ParseUInt(Value(Child(item, "lightSettings"))), (int)ParseUInt(Value(Child(item, "sirenSettings"))), null)));
        }
        return result;
    }

    private static XElement Parse(byte[] data)
    {
        if (PsoDocument.IsPso(data))
            return PsoDocument.Parse(data, Names, PsoNames);
        try
        {
            return XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root
                   ?? throw new InvalidDataException("XML vide.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}", ex);
        }
    }

    // Field names differ in case between files ("colors" / "Colors").
    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    // XML keeps numbers in a "value" attribute, the PSO reader as text.
    private static string Value(XElement? element) => element?.Attribute("value")?.Value ?? element?.Value ?? "";

    private static uint ParseUInt(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) ? hex : 0;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? (uint)value : 0;
    }

    private static (uint Hash, string? Name) HashOf(string text) =>
        text.StartsWith("hash_", StringComparison.Ordinal)
        && uint.TryParse(text.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)
            ? (hash, null)
            : (Joaat.Hash(text), text.ToLowerInvariant());

    // " 1 Metallic Graphite Black \t\t" -> "Metallic Graphite Black".
    private static string CleanName(string name) => Spaces().Replace(LeadingNumber().Replace(name.Trim(), ""), " ").Trim();

    [GeneratedRegex(@"^\d+\s+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
