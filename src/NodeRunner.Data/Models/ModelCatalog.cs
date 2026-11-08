using NodeRunner.Data.FileSystem;
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

    public static ModelCatalog Create(GameContent content) => new()
    {
        _drawables = GameFileIndex.Build(content, ".ydr"),
        _dictionaries = GameFileIndex.Build(content, ".ydd"),
        _fragments = GameFileIndex.Build(content, ".yft"),
    };

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
