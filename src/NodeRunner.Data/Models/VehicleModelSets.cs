using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Models;

// The game's vehicle model sets (vehiclemodelsets.meta): a name (LARGE_CITY...) and the models the game picks from,
// as used by the parking spots (popGroup).
public sealed class VehicleModelSets
{
    private static readonly string[] Files =
    [
        @"update\update.rpf\common\data\ai\vehiclemodelsets.meta",
        @"common.rpf\data\ai\vehiclemodelsets.meta",
    ];

    private readonly Dictionary<uint, (string Name, IReadOnlyList<string> Models)> _sets = new();

    public static readonly VehicleModelSets Empty = new();

    public int Count => _sets.Count;

    public IEnumerable<string> AllNames => _sets.Values.SelectMany(s => s.Models.Prepend(s.Name));

    // The models of a set (by the hash of its name), empty if unknown.
    public IReadOnlyList<string> ModelsOf(uint setHash) => _sets.TryGetValue(setHash, out var set) ? set.Models : [];

    // The first file found (the update's replaces the base game's).
    public static VehicleModelSets Load(IGameFileSystem files)
    {
        foreach (var path in Files)
        {
            if (files.ReadFile(path) is { } data)
                return Parse(data);
        }
        return Empty;
    }

    public static VehicleModelSets Parse(byte[] data)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}", ex);
        }
        var result = new VehicleModelSets();
        foreach (var item in document.Root?.Element("ModelSets")?.Elements("Item") ?? [])
        {
            string? name = item.Element("Name")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            var models = (item.Element("Models")?.Elements("Item") ?? [])
                .Select(m => m.Element("Name")?.Value.Trim().ToLowerInvariant())
                .OfType<string>().Where(m => m.Length > 0).ToList();
            result._sets.TryAdd(Joaat.Hash(name), (name, models));
        }
        return result;
    }
}
