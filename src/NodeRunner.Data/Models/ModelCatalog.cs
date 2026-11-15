using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;

namespace NodeRunner.Data.Models;

// Where to find the model of an archetype: a file, the model's name inside it, and the texture dictionary (.ytd) the
// model takes its textures from (0 if none).
public readonly record struct ModelLocation(string Path, uint ModelHash, uint TextureDictionaryHash = 0);

// Index of the model files (.ydr, .ydd, .yft) the game uses (base game, update and DLC packs, see GameContent), by
// name hash.
public sealed class ModelCatalog
{
    private Dictionary<uint, string> _drawables = new();
    private Dictionary<uint, string> _dictionaries = new();
    private Dictionary<uint, string> _fragments = new();

    public int FileCount => _drawables.Count + _dictionaries.Count + _fragments.Count;

    public IEnumerable<string> FragmentFiles => _fragments.Values;

    // Peds whose parts are files of their own, in a folder named after the ped (a_c_retriever\head_000_r.ydd, with
    // a texture dictionary per texture: head_diff_000_a_whi.ytd): by the hash of the folder name. Made in the background
    // after loading, only scenario previews need it.
    private Lazy<Dictionary<uint, (Dictionary<string, string> Models, Dictionary<string, string> Textures)>> _pedParts = new(() => new());

    public static ModelCatalog Create(GameContent content)
    {
        var catalog = new ModelCatalog
        {
            _drawables = GameFileIndex.Build(content, ".ydr"),
            _dictionaries = GameFileIndex.Build(content, ".ydd"),
            _fragments = GameFileIndex.Build(content, ".yft"),
            _pedParts = new(() => IndexPedParts(content)),
        };
        Task.Run(() => catalog._pedParts.Value);
        return catalog;
    }

    private static Dictionary<uint, (Dictionary<string, string> Models, Dictionary<string, string> Textures)> IndexPedParts(GameContent content)
    {
        var index = new Dictionary<uint, (Dictionary<string, string> Models, Dictionary<string, string> Textures)>();
        void Add(string path, bool texture)
        {
            string? folder = Path.GetDirectoryName(path);
            if (folder is null || folder.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                return;
            uint ped = Joaat.Hash(Path.GetFileName(folder));
            if (!index.TryGetValue(ped, out var parts))
                index[ped] = parts = (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
            (texture ? parts.Textures : parts.Models)[Path.GetFileName(path)] = path;
        }
        // Every version (the name index keeps one head_000_r.ydd for the whole game), newest last.
        foreach (var path in content.EnumerateAllVersions(".ydd"))
            Add(path, false);
        foreach (var path in content.EnumerateAllVersions(".ytd"))
            Add(path, true);
        return index;
    }

    // The drawable dictionary (.ydd) named so (a ped: its parts).
    public string? DictionaryPath(uint nameHash) => _dictionaries.GetValueOrDefault(nameHash);

    // The part files of a ped stored one part per file, empty for the others.
    public (IReadOnlyList<string> Models, IReadOnlyList<string> Textures) PedParts(uint pedHash) =>
        _pedParts.Value.TryGetValue(pedHash, out var parts) ? (parts.Models.Values.ToList(), parts.Textures.Values.ToList()) : ([], []);

    // Whether a ped's parts and skeleton are there.
    public bool HasPed(uint pedHash) =>
        _fragments.ContainsKey(pedHash) && (_dictionaries.ContainsKey(pedHash) || _pedParts.Value.GetValueOrDefault(pedHash).Models is { Count: > 0 });

    // Index made from lists of files (another game: GTA IV), each keyed by the hash of its name; later files win.
    public static ModelCatalog FromFiles(IEnumerable<string> drawables, IEnumerable<string> dictionaries, IEnumerable<string> fragments)
    {
        static Dictionary<uint, string> Index(IEnumerable<string> paths)
        {
            var index = new Dictionary<uint, string>();
            foreach (string path in paths)
                index[Joaat.Hash(Path.GetFileNameWithoutExtension(path))] = path;
            return index;
        }
        return new ModelCatalog { _drawables = Index(drawables), _dictionaries = Index(dictionaries), _fragments = Index(fragments) };
    }

    // The fragment file (.yft) of a model, by the hash of its name (a vehicle: its spawn name).
    public string? FragmentPath(uint nameHash) => _fragments.GetValueOrDefault(nameHash);

    public ModelLocation? Locate(Archetype archetype)
    {
        uint asset = archetype.AssetNameHash;
        string? path = archetype.AssetType switch
        {
            ArchetypeAssetType.Drawable => _drawables.GetValueOrDefault(asset),
            ArchetypeAssetType.DrawableDictionary => _dictionaries.GetValueOrDefault(archetype.DrawableDictionaryHash),
            ArchetypeAssetType.Fragment => _fragments.GetValueOrDefault(asset),
            _ => null,
        };
        return path is null ? null : new ModelLocation(path, asset, archetype.TextureDictionaryHash);
    }
}
