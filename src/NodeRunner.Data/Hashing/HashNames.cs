using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Hashing;

/// <summary>
/// Finds the readable name behind a hash. The game files only store hashes (see <see cref="Joaat"/>);
/// names are recovered from known strings, here the names of the game's files: an object type usually has
/// the name of its model file (prop_bin_05a.yft), a map file its own name.
/// </summary>
public sealed class HashNames
{
    private static readonly string[] NamedExtensions = [".ydr", ".yft", ".ydd", ".ytd", ".ymap", ".ytyp", ".ybn"];
    private static readonly string[] GroupDictionarySuffixes = ["_slod_children", "_dslod_children"];
    private const int MaxGroupNumber = 99;

    private readonly Dictionary<uint, string> _names = new();

    // Names may be learnt while the interface runs (a name typed in the search): reads and writes are locked.
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) return _names.Count; }
    }

    public void Add(string name)
    {
        lock (_lock)
            _names.TryAdd(Joaat.Hash(name), name.ToLowerInvariant());
    }

    /// <summary>
    /// Adds the names of a text file written by the user (one name per line; empty lines and lines starting with
    /// // or # are skipped). Returns the number of names read.
    /// </summary>
    public int AddLines(IEnumerable<string> lines)
    {
        int count = 0;
        foreach (var line in lines)
        {
            string name = line.Trim();
            if (name.Length == 0 || name.StartsWith("//") || name.StartsWith('#'))
                continue;
            Add(name);
            count++;
        }
        return count;
    }

    public string? TryGet(uint hash)
    {
        lock (_lock)
            return _names.GetValueOrDefault(hash);
    }

    /// <summary>The name if known, otherwise the hash in hexadecimal.</summary>
    public string Describe(uint hash) => TryGet(hash) ?? $"0x{hash:X8}";

    /// <summary>
    /// Names of every model, texture dictionary, map and collision file of the game, plus the names of
    /// simplified models, which are stored in dictionaries (.ydd) keeping only hashes. They follow two patterns:
    /// the detailed model's name plus "_lod" (or "_loda", "_lodb": id2_00_a_build14_lod), and for groups of props, the dictionary's
    /// name followed by a number (cs6_08_props_combo06_slod_children.ydd holds cs6_08_props_combo06_09_lod).
    /// Those guesses are only kept when they match one of <paramref name="wanted"/> (the object types).
    /// The game's .nametable files hold audio names only (measured: 8 more map types named out of 126 486): not read.
    /// </summary>
    public static HashNames FromFileNames(GameContent content, IReadOnlySet<uint> wanted)
    {
        var names = new HashNames();
        void AddIfWanted(string name)
        {
            if (wanted.Contains(Joaat.Hash(name)))
                names.Add(name);
        }

        foreach (var extension in NamedExtensions)
        {
            foreach (var path in content.EnumerateFiles(extension, withInactiveFallback: true))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                names.Add(name);
                AddIfWanted(name + "_lod");
                AddIfWanted(name + "_loda");
                AddIfWanted(name + "_lodb");

                foreach (var suffix in GroupDictionarySuffixes)
                {
                    if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string group = name[..^suffix.Length];
                    for (int i = 0; i <= MaxGroupNumber; i++)
                        AddIfWanted($"{group}_{i:00}_lod");
                }
            }
        }
        return names;
    }
}
