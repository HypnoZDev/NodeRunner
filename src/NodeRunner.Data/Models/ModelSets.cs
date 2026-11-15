using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Models;

// Model sets of the game: a name (LARGE_CITY, ANIMAL_DEER...) and the models the game picks from. Vehicles
// (vehiclemodelsets.meta) for the parking spots (popGroup) and vehicle scenarios; peds (ambientpedmodelsets.meta) for
// the other scenarios.
public sealed class ModelSets
{
    private static readonly string[] VehicleFiles =
    [
        @"update\update.rpf\common\data\ai\vehiclemodelsets.meta",
        @"common.rpf\data\ai\vehiclemodelsets.meta",
    ];

    private static readonly string[] PedFiles =
    [
        @"update\update.rpf\common\data\ai\ambientpedmodelsets.meta",
        @"common.rpf\data\ai\ambientpedmodelsets.meta",
    ];

    private readonly Dictionary<uint, (string Name, IReadOnlyList<string> Models)> _sets = new();

    public static readonly ModelSets Empty = new();

    public int Count => _sets.Count;

    public IEnumerable<string> AllNames => _sets.Values.SelectMany(s => s.Models.Prepend(s.Name));

    // The models of a set (by the hash of its name), empty if unknown.
    public IReadOnlyList<string> ModelsOf(uint setHash) => _sets.TryGetValue(setHash, out var set) ? set.Models : [];

    public bool Contains(uint setHash) => _sets.ContainsKey(setHash);

    public static ModelSets LoadVehicles(IGameFileSystem files) => Load(files, VehicleFiles);

    public static ModelSets LoadPeds(IGameFileSystem files) => Load(files, PedFiles);

    // The first file found (the update's replaces the base game's).
    private static ModelSets Load(IGameFileSystem files, string[] paths)
    {
        foreach (var path in paths)
        {
            if (files.ReadFile(path) is { } data)
                return Parse(data);
        }
        return Empty;
    }

    public static ModelSets Parse(byte[] data)
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
        var result = new ModelSets();
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
