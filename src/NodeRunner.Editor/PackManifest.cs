using System.Xml.Linq;

namespace NodeRunner.Editor;

// Content of a resource's _manifest.ymf, in the XML form of the game's CPackFileMetaData: which .ytyp each .ymap needs
// (so that the game loads the types with the map, as for its own files), the .ytyp each interior's objects come from,
// and the interiors. Written only when the resource brings .ytyp files of its own.
public static class PackManifest
{
    public const string FileName = "_manifest.ymf";

    // A .ymap of the resource: name without extension, archetypes of its objects, whether it places an interior.
    public sealed record MapFile(string Name, IReadOnlyList<uint> Archetypes, bool HasInterior);

    // An interior defined in a .ytyp of the resource: its name hash and the archetypes of its objects (sets included).
    public sealed record Interior(uint Name, IReadOnlyList<uint> Archetypes);

    // A .ytyp of the resource that the game does not have.
    public sealed record TypeFile(string Name, IReadOnlyList<Interior> Interiors);

    // typeFileOf: the .ytyp (name without extension) defining an archetype, null when unknown. Null when there is
    // nothing to write.
    public static XElement? Build(IReadOnlyList<MapFile> maps, IReadOnlyList<TypeFile> newTypes, Func<uint, string?> typeFileOf)
    {
        if (newTypes.Count == 0)
            return null;
        var newNames = newTypes.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mapItems = new List<XElement>();
        foreach (var map in maps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var types = TypeFiles(map.Archetypes, typeFileOf, exclude: null);
            if (!types.Any(newNames.Contains))
                continue; // Only the game's types: the game's own manifests already load them.
            mapItems.Add(Item("imapName", map.Name, map.HasInterior, types));
        }

        var typeItems = new List<XElement>();
        var interiors = new List<XElement>();
        foreach (var file in newTypes.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (file.Interiors.Count == 0)
                continue;
            typeItems.Add(Item("itypName", file.Name, interior: true,
                TypeFiles(file.Interiors.SelectMany(i => i.Archetypes), typeFileOf, exclude: file.Name)));
            foreach (var interior in file.Interiors)
            {
                string name = HashName(interior.Name);
                interiors.Add(new XElement("Item", new XElement("Name", name), new XElement("Bounds", new XElement("Item", name))));
            }
        }
        if (mapItems.Count == 0 && typeItems.Count == 0)
            return null;

        return new XElement("CPackFileMetaData",
            new XElement("MapDataGroups"),
            new XElement("HDTxdBindingArray"),
            new XElement("imapDependencies"),
            new XElement("imapDependencies_2", mapItems),
            new XElement("itypDependencies_2", typeItems),
            new XElement("Interiors", interiors));
    }

    public static string HashName(uint hash) => $"hash_{hash:X8}";

    // Field names of CPackFileMetaData, to read a manifest back (PsoXml.Read).
    public static readonly string[] FieldNames =
    [
        "CPackFileMetaData", "MapDataGroups", "HDTxdBindingArray", "imapDependencies", "imapDependencies_2", "itypDependencies_2",
        "Interiors", "imapName", "itypName", "manifestFlags", "itypDepArray", "Name", "Bounds", "packFileName", "Flags",
        "WeatherTypes", "HoursOnOff", "assetType", "targetAsset", "HDTxd",
    ];

    // Adds to a manifest brought by the project (an imported mapping) what added (Build) lists and it lacks: maps, type
    // files and interiors, and the missing type files of the entries both have. The rest of it is kept as it is.
    // Returns whether anything was added.
    public static bool Merge(XElement existing, XElement added)
    {
        bool changed = false;
        foreach (var (section, key, list) in new[]
                 {
                     ("imapDependencies_2", "imapName", "itypDepArray"),
                     ("itypDependencies_2", "itypName", "itypDepArray"),
                     ("Interiors", "Name", "Bounds"),
                 })
        {
            if (added.Element(section) is not { HasElements: true } items)
                continue;
            var target = existing.Element(section);
            if (target is null)
                existing.Add(target = new XElement(section));
            foreach (var item in items.Elements("Item"))
            {
                uint name = Key((string?)item.Element(key));
                var same = target.Elements("Item").FirstOrDefault(i => Key((string?)i.Element(key)) == name);
                if (same is null)
                {
                    target.Add(new XElement(item));
                    changed = true;
                    continue;
                }
                var names = same.Element(list);
                if (names is null)
                    same.Add(names = new XElement(list));
                var known = names.Elements("Item").Select(i => Key(i.Value)).ToHashSet();
                foreach (var dependency in item.Element(list)?.Elements("Item") ?? [])
                {
                    if (known.Add(Key(dependency.Value)))
                    {
                        names.Add(new XElement(dependency));
                        changed = true;
                    }
                }
                if (item.Element("manifestFlags") is { Value.Length: > 0 } flags && same.Element("manifestFlags") is { } old
                    && old.Value.Trim() is "" or "0")
                {
                    old.Value = flags.Value;
                    changed = true;
                }
            }
        }
        return changed;
    }

    // The hash of a name as the manifest stores it ("hash_XXXXXXXX" when read back from a file).
    private static uint Key(string? name) =>
        name is null ? 0
        : name.StartsWith("hash_", StringComparison.OrdinalIgnoreCase)
          && uint.TryParse(name.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out uint hash) ? hash
        : NodeRunner.Data.Hashing.Joaat.Hash(name.Trim().ToLowerInvariant());

    // The .ytyp files of the archetypes, in the order first met.
    private static List<string> TypeFiles(IEnumerable<uint> archetypes, Func<uint, string?> typeFileOf, string? exclude)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (uint archetype in archetypes)
        {
            if (typeFileOf(archetype) is { Length: > 0 } file && !string.Equals(file, exclude, StringComparison.OrdinalIgnoreCase) && seen.Add(file))
                files.Add(file);
        }
        return files;
    }

    private static XElement Item(string nameField, string name, bool interior, List<string> types) =>
        new("Item",
            new XElement(nameField, name.ToLowerInvariant()),
            new XElement("manifestFlags", interior ? "INTERIOR_DATA" : ""),
            new XElement("itypDepArray", types.Select(t => new XElement("Item", t.ToLowerInvariant()))));
}
