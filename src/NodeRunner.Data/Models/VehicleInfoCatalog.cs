using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Models;

// One value of a vehicle's sheet, as written in the game file (name of the field, its value as text).
public readonly record struct VehicleField(string Name, string Value);

// What vehicles.meta (model, make, class, flags...) and handling.meta (mass, speed, gears...) say of each vehicle,
// every simple field kept as text. Files of every active pack, the last one naming a vehicle winning.
public sealed class VehicleInfoCatalog
{
    public static readonly VehicleInfoCatalog Empty = new();

    private readonly Dictionary<uint, IReadOnlyList<VehicleField>> _vehicles = new();
    private readonly Dictionary<uint, IReadOnlyList<VehicleField>> _handlings = new();

    public List<string> Errors { get; } = new();

    public int Count => _vehicles.Count;

    public IReadOnlyList<VehicleField> VehicleOf(uint modelHash) => _vehicles.GetValueOrDefault(modelHash) ?? [];

    // The handling of a vehicle, through the handlingId of its sheet.
    public IReadOnlyList<VehicleField> HandlingOf(uint modelHash) =>
        Value(VehicleOf(modelHash), "handlingId") is { Length: > 0 } id ? _handlings.GetValueOrDefault(Joaat.Hash(id)) ?? [] : [];

    public static string? Value(IReadOnlyList<VehicleField> fields, string name) =>
        fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public static VehicleInfoCatalog Load(GameContent content)
    {
        var catalog = new VehicleInfoCatalog();
        foreach (var path in content.EnumerateAllVersions(".meta"))
        {
            string name = Path.GetFileName(path);
            bool vehicles = name.EndsWith("vehicles.meta", StringComparison.OrdinalIgnoreCase);
            bool handling = name.Equals("handling.meta", StringComparison.OrdinalIgnoreCase);
            if (!vehicles && !handling)
                continue;
            try
            {
                if (content.ReadFile(path) is not { } data)
                    continue;
                var root = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root;
                if (vehicles)
                    catalog.Add(root?.Element("InitDatas"), "modelName", catalog._vehicles);
                else
                    catalog.Add(root?.Element("HandlingData"), "handlingName", catalog._handlings);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
            {
                catalog.Errors.Add($"{name} : {ex.Message}");
            }
        }
        return catalog;
    }

    private void Add(XElement? list, string keyField, Dictionary<uint, IReadOnlyList<VehicleField>> into)
    {
        foreach (var item in list?.Elements("Item") ?? [])
        {
            string? key = item.Element(keyField)?.Value.Trim();
            if (string.IsNullOrEmpty(key))
                continue;
            var fields = new List<VehicleField>();
            foreach (var field in item.Elements())
            {
                if (Text(field) is { Length: > 0 } text)
                    fields.Add(new VehicleField(field.Name.LocalName, text));
            }
            into[Joaat.Hash(key)] = fields;
        }
    }

    // value="..." / x y z (w) / plain text / a list of plain items; null for empty or nested structures.
    private static string? Text(XElement field)
    {
        if (field.Attribute("value") is { } value)
            return Number(value.Value.Trim());
        if (field.Attribute("x") is { } x)
        {
            var parts = new[] { x, field.Attribute("y"), field.Attribute("z"), field.Attribute("w") }
                .Where(a => a is not null).Select(a => Number(a!.Value));
            return string.Join(", ", parts);
        }
        if (!field.HasElements)
        {
            string text = string.Join(" ", field.Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return text.Length == 0 || text.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : text;
        }
        var items = field.Elements().ToList();
        if (items.All(i => !i.HasElements && !i.HasAttributes))
            return string.Join(", ", items.Select(i => i.Value.Trim()).Where(t => t.Length > 0));
        return null;
    }

    private static string Number(string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : text;
}
