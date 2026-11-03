using System.Text.RegularExpressions;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

internal enum ViewerKind { Vehicle, Object }

/// <summary>A model the viewer can open: its name (spawn name for a vehicle, archetype for an object) and where it is.</summary>
internal sealed record ViewerItem(string Name, ViewerKind Kind, ModelLocation Location)
{
    /// <summary>Hash of the object type (its name, or the "0x..." of a type without known name).</summary>
    public uint ArchetypeHash => Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                 && uint.TryParse(Name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hash)
        ? hash : Joaat.Hash(Name);
}

/// <summary>Everything the model viewer lists: the vehicles of the game (DLC included) and the object types of the map.</summary>
internal sealed partial class ViewerCatalog
{
    public required IReadOnlyList<ViewerItem> Vehicles { get; init; }
    public required IReadOnlyList<ViewerItem> Objects { get; init; }

    // Vehicle archives: levels\gta5\vehicles.rpf, and in DLC packs levels\gta5\vehicles\<pack>vehicles.rpf
    // (tuning parts are in "vehiclemods", not listed).
    [GeneratedRegex(@"[\\/]vehicles(\.rpf)?[\\/]|[\\/][^\\/]*vehicles\.rpf[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex VehicleArchive();

    /// <summary>Built on a background thread from a loaded map (a few hundred milliseconds).</summary>
    public static ViewerCatalog Build(WorldLoader world)
    {
        var vehicles = new List<ViewerItem>();
        var objects = new List<ViewerItem>();
        if (world.Models is not { } models)
            return new ViewerCatalog { Vehicles = vehicles, Objects = objects };

        foreach (var path in models.FragmentFiles)
        {
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            // "_hi" = very detailed version of the same vehicle, used only up close.
            if (!VehicleArchive().IsMatch(path) || name.EndsWith("_hi", StringComparison.Ordinal))
                continue;
            uint hash = Joaat.Hash(name);
            vehicles.Add(new ViewerItem(name, ViewerKind.Vehicle, new ModelLocation(path, hash, hash)));
        }

        if (world.Maps is { } library && world.Names is { } names)
        {
            foreach (uint hash in library.GetArchetypeHashes())
            {
                if (library.GetArchetype(hash) is not { IsShadowOrReflectionProxy: false } archetype
                    || models.Locate(archetype) is not { } location)
                    continue;
                objects.Add(new ViewerItem(names.Describe(hash), ViewerKind.Object, location));
            }
        }

        vehicles.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        // Readable names first, the names only known by their hash ("0x...") at the end.
        objects.Sort((a, b) =>
        {
            int byKind = a.Name.StartsWith("0x", StringComparison.Ordinal).CompareTo(b.Name.StartsWith("0x", StringComparison.Ordinal));
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Name, b.Name);
        });
        return new ViewerCatalog { Vehicles = vehicles, Objects = objects };
    }
}
