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
