using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Links between the streamed files of a resource (or of a project): a map's objects need their type (.ytyp), a type
// needs its model and textures, a model with no type cannot be placed. What the resource lacks may come from the game.
// Reads only.
public static class DependencyCheck
{
    public enum Kind
    {
        // Errors.
        DuplicateFile,
        Unreadable,
        MissingModel,
        UnknownArchetype,
        NoManifest,
        NoFxVersion,
        MissingDataFile,

        // Warnings.
        MissingTextures,
        ModelWithoutArchetype,
        Oversized,
        NotStreamed,
        MissingListedFile,
        NotAMap,
    }

    public static bool IsError(Kind kind) => kind < Kind.MissingTextures;

    // File: the file the line is about (name only). Hash: the type or texture dictionary concerned. Value: a count or a size.
    public sealed record Issue(Kind Kind, string File, uint Hash = 0, long Value = 0);

    // What the game itself has, by name hash.
    public sealed record GameFiles(Func<uint, bool> Archetype, Func<uint, bool> Drawable, Func<uint, bool> DrawableDictionary,
        Func<uint, bool> Fragment, Func<uint, bool> TextureDictionary);

    // Also streamed by FiveM, though NodeRunner does not import them (screens, resources of other kinds).
    private static readonly HashSet<string> OtherStreamed = [".gfx", ".rpf", ".ypdb", ".yld", ".yed", ".ypt", ".yvr", ".ywr", ".yfd", ".ysc", ".awc"];

    // FiveM warns above this memory size ("oversized asset"): such a file may not load for players with little memory.
    public const long OversizedBytes = 16 << 20;

    // files: full paths of the streamed files. types / maps: those that could be read; unreadable: those that could not.
    // memory: the larger of the two memory sizes of a resource file, null if it is not one.
    public static List<Issue> Check(IReadOnlyList<string> files, IReadOnlyList<(string File, MapTypes Types)> types,
        IReadOnlyList<(string File, MapData Map)> maps, IReadOnlyList<string> unreadable, GameFiles game, Func<string, long?> memory)
    {
        var issues = new List<Issue>();

        // FiveM streams every file by its name alone, from any folder of "stream": only one of two same names is used.
        foreach (var group in files.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new Issue(Kind.DuplicateFile, group.Key, Value: group.Count()));
        foreach (string file in unreadable)
            issues.Add(new Issue(Kind.Unreadable, Path.GetFileName(file)));

        var own = new HashSet<(uint, string)>();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(name).ToLowerInvariant();
            own.Add((Joaat.Hash(Path.GetFileNameWithoutExtension(name).ToLowerInvariant()), extension));
            if (!MappingImport.IsStreamed(name) && !OtherStreamed.Contains(extension))
                issues.Add(new Issue(Kind.NotStreamed, name));
            else if (memory(file) is > OversizedBytes and var size)
                issues.Add(new Issue(Kind.Oversized, name, Value: size));
        }
        bool Has(uint hash, string extension) => own.Contains((hash, extension));

        var archetypes = new HashSet<uint>();
        foreach (var (_, t) in types)
            foreach (var a in t.Archetypes)
                archetypes.Add(a.NameHash);
        bool Known(uint hash) => archetypes.Contains(hash) || game.Archetype(hash);

        foreach (var (file, t) in types)
        {
            string name = Path.GetFileName(file);
            foreach (var a in t.Archetypes)
            {
                bool model = a.AssetType switch
                {
                    ArchetypeAssetType.Drawable => Has(a.AssetNameHash, ".ydr") || game.Drawable(a.AssetNameHash),
                    ArchetypeAssetType.DrawableDictionary => Has(a.DrawableDictionaryHash, ".ydd") || game.DrawableDictionary(a.DrawableDictionaryHash),
                    ArchetypeAssetType.Fragment => Has(a.AssetNameHash, ".yft") || game.Fragment(a.AssetNameHash),
                    _ => true,
                };
                if (!model)
                    issues.Add(new Issue(Kind.MissingModel, name, a.NameHash));
                // A dictionary named like the model: its textures are inside the model (Sollumz does it by default).
                else if (a.Kind != ArchetypeKind.Interior && a.TextureDictionaryHash != 0
                         && a.TextureDictionaryHash != a.NameHash && a.TextureDictionaryHash != a.AssetNameHash
                         && !Has(a.TextureDictionaryHash, ".ytd") && !game.TextureDictionary(a.TextureDictionaryHash))
                    issues.Add(new Issue(Kind.MissingTextures, name, a.TextureDictionaryHash, a.NameHash));
                if (a.Kind == ArchetypeKind.Interior)
                    foreach (uint missing in a.AllInteriorEntities.Select(e => e.ArchetypeHash).Distinct().Where(h => !Known(h)))
                        issues.Add(new Issue(Kind.UnknownArchetype, name, missing));
            }
        }

        foreach (var (file, map) in maps)
            foreach (uint missing in map.Entities.Select(e => e.ArchetypeHash).Distinct().Where(h => !Known(h)))
                issues.Add(new Issue(Kind.UnknownArchetype, Path.GetFileName(file), missing));

        // A drawable or fragment that no type names: the game cannot place it (a .ydd holds several models: skipped).
        var named = new HashSet<uint>();
        foreach (var (_, t) in types)
            foreach (var a in t.Archetypes)
                named.Add(a.AssetNameHash);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is not (".ydr" or ".yft"))
                continue;
            string bare = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            // Damaged / high-detail variants of a fragment go with it.
            if (extension == ".yft" && bare.EndsWith("_hi"))
                continue;
            uint hash = Joaat.Hash(bare);
            if (!named.Contains(hash) && !game.Archetype(hash))
                issues.Add(new Issue(Kind.ModelWithoutArchetype, name, hash));
        }
        return issues;
    }

    // The manifest of an exported resource: present, with its fx_version, and every file it names there.
    // files: the resource's files, relative paths with '/'.
    public static List<Issue> CheckManifest(string? manifestName, string? lua, IReadOnlyCollection<string> files, bool streamsMaps)
    {
        var issues = new List<Issue>();
        if (manifestName is null || lua is null)
        {
            issues.Add(new Issue(Kind.NoManifest, "fxmanifest.lua"));
            return issues;
        }
        bool legacy = manifestName.Equals("__resource.lua", StringComparison.OrdinalIgnoreCase);
        if (!legacy && !System.Text.RegularExpressions.Regex.IsMatch(lua, @"^\s*fx_version", System.Text.RegularExpressions.RegexOptions.Multiline))
            issues.Add(new Issue(Kind.NoFxVersion, manifestName));
        var manifest = ResourceExtras.Parse(lua);
        // A folder, or an audio file named without its end ("audio/x_game.dat" for "audio/x_game.dat151.rel").
        bool Present(string pattern) =>
            files.Any(f => ResourceExtras.Matches(pattern, f) || f.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                || f.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(f[pattern.Length..], @"^\d+\.rel$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        // A type request names a streamed .ytyp by its name alone.
        foreach (var (_, path) in manifest.DataFiles.Where(d => !d.Type.Equals("DLC_ITYP_REQUEST", StringComparison.OrdinalIgnoreCase))
                     .DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
            if (!Present(path))
                issues.Add(new Issue(Kind.MissingDataFile, path));
        foreach (string path in manifest.Files.Concat(manifest.Scripts).Distinct(StringComparer.OrdinalIgnoreCase))
            // Scripts may come from another resource ("@ox_lib/init.lua").
            if (!path.StartsWith('@') && !Present(path))
                issues.Add(new Issue(Kind.MissingListedFile, path));
        if (streamsMaps && !System.Text.RegularExpressions.Regex.IsMatch(lua, @"^\s*this_is_a_map", System.Text.RegularExpressions.RegexOptions.Multiline))
            issues.Add(new Issue(Kind.NotAMap, manifestName));
        return issues;
    }
}
