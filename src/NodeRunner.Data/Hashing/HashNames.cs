using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Hashing;

// Finds the readable name behind a hash.
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

    private static readonly string[] ModelSetFiles =
    [
        @"update\update.rpf\common\data\ai\vehiclemodelsets.meta",
        @"common.rpf\data\ai\vehiclemodelsets.meta",
    ];

    // Names of the vehicle model sets (popGroup of the parking spots: LARGE_CITY...) and of their models.
    public int AddModelSetNames(IGameFileSystem files)
    {
        int before = Count;
        foreach (var path in ModelSetFiles)
        {
            if (files.ReadFile(path) is not { } data)
                continue;
            var text = System.Text.Encoding.UTF8.GetString(data);
            for (int at = text.IndexOf("<Name>", StringComparison.Ordinal); at >= 0; at = text.IndexOf("<Name>", at, StringComparison.Ordinal))
            {
                at += "<Name>".Length;
                int end = text.IndexOf('<', at);
                if (end < 0)
                    break;
                string name = text[at..end].Trim();
                if (name.Length > 0 && !name.Any(char.IsWhiteSpace))
                    Add(name);
            }
        }
        return Count - before;
    }

    // Adds the names of a text file written by the user (one name per line; empty lines and lines starting with // or
    // # are skipped).
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

    public string Describe(uint hash) => TryGet(hash) ?? $"0x{hash:X8}";

    // Names of every model, texture dictionary, map and collision file of the game, plus the names of simplified
    // models, which are stored in dictionaries (.ydd) keeping only hashes.
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
