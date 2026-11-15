using System.Text.RegularExpressions;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.App.World;

internal enum ViewerKind { Vehicle, Object }

// A model the viewer can open: its name (spawn name for a vehicle, archetype for an object) and where it is.
internal sealed record ViewerItem(string Name, ViewerKind Kind, ModelLocation Location)
{
    // Pack of the prop library the object type comes from, null for the game's.
    public string? Pack { get; init; }

    public uint ArchetypeHash => Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                 && uint.TryParse(Name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hash)
        ? hash : Joaat.Hash(Name);
}

// Everything the model viewer lists: the vehicles of the game (DLC included), the object types of the map and those of
// the prop library.
internal sealed partial class ViewerCatalog
{
    public required IReadOnlyList<ViewerItem> Vehicles { get; init; }
    public required IReadOnlyList<ViewerItem> Objects { get; init; }
    public IReadOnlyList<ViewerItem> Library { get; init; } = [];

    // Vehicle archives: levels\gta5\vehicles.rpf, and in DLC packs levels\gta5\vehicles\<pack>vehicles.rpf
    // (tuning parts are in "vehiclemods", not listed). GTA IV: models\cdimages\vehicles.img.
    [GeneratedRegex(@"[\\/]vehicles(\.rpf|\.img)?[\\/]|[\\/][^\\/]*vehicles\.rpf[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex VehicleArchive();

    public static bool IsVehicleFile(string path) => VehicleArchive().IsMatch(path);

    public static ViewerCatalog Build(WorldLoader world)
    {
        var vehicles = new List<ViewerItem>();
        var objects = new List<ViewerItem>();
        var props = new List<ViewerItem>();
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

        props = LibraryItems(world);
        vehicles.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        objects.Sort(ByName);
        return new ViewerCatalog { Vehicles = vehicles, Objects = objects, Library = props };
    }

    // Readable names first, the names only known by their hash ("0x...") at the end.
    private static int ByName(ViewerItem a, ViewerItem b)
    {
        int byKind = a.Name.StartsWith("0x", StringComparison.Ordinal).CompareTo(b.Name.StartsWith("0x", StringComparison.Ordinal));
        return byKind != 0 ? byKind : string.CompareOrdinal(a.Name, b.Name);
    }

    // GTA IV: the vehicles of the game or episode (".wft" of "vehicles.img"), and the object types of its map.
    public static ViewerCatalog BuildGta4(Data.Native.Gta4.Gta4Scene scene)
    {
        var vehicles = new List<ViewerItem>();
        foreach (var path in scene.Models.FragmentFiles)
        {
            if (!path.Contains(@"\vehicles.img\", StringComparison.OrdinalIgnoreCase))
                continue;
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            uint hash = Joaat.Hash(name);
            vehicles.Add(new ViewerItem(name, ViewerKind.Vehicle, new ModelLocation(path, hash, hash)));
        }
        var objects = new List<ViewerItem>();
        foreach (uint hash in scene.Library.GetArchetypeHashes())
        {
            if (scene.Library.GetArchetype(hash) is not { } archetype || scene.Models.Locate(archetype) is not { } location)
                continue;
            // The search is in lower case; some ".ide" names are not.
            string name = scene.Names.Describe(hash);
            objects.Add(new ViewerItem(name.StartsWith("0x", StringComparison.Ordinal) ? name : name.ToLowerInvariant(), ViewerKind.Object, location));
        }
        vehicles.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        objects.Sort(ByName);
        return new ViewerCatalog { Vehicles = vehicles, Objects = objects };
    }

    // The object types of the prop library, by pack then name.
    public static List<ViewerItem> LibraryItems(WorldLoader world)
    {
        var props = new List<ViewerItem>();
        if (world.Maps is not { } library || world.Names is not { } names || world.Models is not { } models
            || world.ProjectFiles is not { LibraryFileCount: > 0 } overlay)
            return props;
        foreach (uint hash in library.GetArchetypeHashes())
        {
            if (library.GetArchetypeFile(hash) is not { } file || !Data.FileSystem.GameContent.IsLibraryFile(file)
                || library.GetArchetype(hash) is not { IsShadowOrReflectionProxy: false } archetype
                || models.Locate(archetype) is not { } location)
                continue;
            props.Add(new ViewerItem(names.Describe(hash), ViewerKind.Object, location) { Pack = overlay.LibraryFileOf(file)?.Pack ?? "" });
        }
        props.Sort((a, b) =>
        {
            int byPack = string.Compare(a.Pack, b.Pack, StringComparison.OrdinalIgnoreCase);
            return byPack != 0 ? byPack : string.CompareOrdinal(a.Name, b.Name);
        });
        return props;
    }
}
