using System.Numerics;

namespace NodeRunner.Data.Maps;

// Among the maps only loaded by scripts, finds the alternative versions of a same place, so that only one is shown by
// default: decoration styles of an apartment or office (different interiors at the same spot), and states named in
// the file names (open / closed door, intact / destroyed, original house / new mansion...).
public static class ScriptedMapVariants
{
    // State word of an alternative version -> name of the default version, with that word replaced (empty: removed).
    private static readonly Dictionary<string, string[]> Alternatives = new()
    {
        ["open"] = ["closed"],
        ["end"] = ["start"],
        ["fake"] = ["real"],
        ["broken"] = ["closed", ""],
        ["swap"] = [""],
        ["up"] = [""],
        // Nightclub posters: the club is for sale until bought.
        ["dixon"] = ["forsale"],
        ["madonna"] = ["forsale"],
        ["solomun"] = ["forsale"],
        ["taleofus"] = ["forsale"],
        // Online mansions: the new mansion ("shared" + "private") replaces the original house and its props.
        ["original"] = ["shared", "mansion"],
        ["generic"] = ["private"],
    };

    private static readonly HashSet<string> SceneOnlyWords = ["cutscene", "replay"];

    // DLC pack that put the mansions in use ("A Safehouse in the Hills", 2025).
    public const string MansionsPack = "mp2025_02";

    // Names (as given) of the maps to hide by default. regularMaps are the maps always loaded: a scripted interior at
    // the same spot as one of theirs is an alternative version too. withMansions: the new mansions replace the
    // original houses (MansionsPack applied).
    public static HashSet<string> DefaultHidden(IReadOnlyList<(string Name, MapData Map)> maps, IEnumerable<MapData> regularMaps,
        bool withMansions = true)
    {
        var names = maps.Select(m => m.Name).ToHashSet();
        var hidden = names.Where(name => withMansions
            ? IsNamedAlternative(name, names)
            : IsNamedAlternative(name, names, OriginalHouses) || IsNewMansion(name, names)).ToHashSet();
        // Blocks only casting the sun's shadow in the game (e.g. hei_shadowsunmesh_kt1_08), never seen.
        hidden.UnionWith(names.Where(name => name.Contains("shadowsunmesh") || name.Contains("shadowmesh")));

        static (int, int, int) Spot(Vector3 p) => ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
        var regularSpots = regularMaps.SelectMany(m => m.Entities).Where(e => e.IsInterior).Select(e => Spot(e.Position)).ToHashSet();
        var spots = maps
            .SelectMany(m => m.Map.Entities.Where(e => e.IsInterior).Select(e => (Spot: Spot(e.Position), m.Name)))
            .GroupBy(x => x.Spot, x => x.Name);
        foreach (var spot in spots)
        {
            var here = spot.Distinct().Order(StringComparer.Ordinal).ToList();
            bool keepOne = !regularSpots.Contains(spot.Key);
            if (here.Count < 2 && keepOne)
                continue;
            // Keep the first version not already hidden (e.g. style 01 of an apartment), unless the regular map has its own.
            string? kept = keepOne ? here.FirstOrDefault(n => !hidden.Contains(n)) : null;
            foreach (var name in here)
            {
                if (name != kept)
                    hidden.Add(name);
            }
        }
        return hidden;
    }

    // Before the mansions: the same rules without the mansion ones (the original house is kept).
    private static readonly Dictionary<string, string[]> OriginalHouses =
        Alternatives.Where(a => a.Key is not ("original" or "generic")).ToDictionary();

    // A part of a new mansion ("x_mansion_shared", "x_roads_mansion"...): the word "mansion" after a place
    // ("hei_ch1_06e") that also has an original version ("hei_ch1_06e_mansion_original",
    // "hei_ch1_06e_props_original").
    private static bool IsNewMansion(string name, HashSet<string> names)
    {
        var words = name.Split('_');
        if (words.Contains("original"))
            return false;
        int i = Array.IndexOf(words, "mansion");
        if (i < 2)
            return false;
        string place = string.Join('_', words, 0, i) + "_";
        return names.Any(n => n.StartsWith(place, StringComparison.Ordinal) && n.Split('_').Contains("original"));
    }

    private static bool IsNamedAlternative(string name, HashSet<string> names) => IsNamedAlternative(name, names, Alternatives);

    private static bool IsNamedAlternative(string name, HashSet<string> names, Dictionary<string, string[]> alternatives)
    {
        var words = name.Split('_');
        for (int i = 0; i < words.Length; i++)
        {
            if (SceneOnlyWords.Contains(words[i]))
                return true;
            if (i == 0 || !alternatives.TryGetValue(words[i], out var defaults))
                continue;
            // The default version, with or without the end of the name ("x_open_props" -> "x_closed", "x_fake_y" -> "x_real_y").
            string before = string.Join('_', words, 0, i);
            string after = string.Concat(words.Skip(i + 1).Select(w => "_" + w));
            foreach (var d in defaults)
            {
                string other = d.Length == 0 ? before : $"{before}_{d}";
                if (names.Contains(other) || after.Length > 0 && names.Contains(other + after))
                    return true;
            }
            // "xxx_props_original" goes with "xxx_mansion_original": hidden when "xxx_mansion_shared" exists.
            if (words[i] == "original" && i >= 2 && names.Contains($"{string.Join('_', words, 0, i - 1)}_mansion_shared"))
                return true;
        }
        return false;
    }
}
