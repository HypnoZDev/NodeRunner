using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;

namespace NodeRunner.Data.Models;

/// <summary>
/// Where to find the model of an archetype: a file, the model's name inside it,
/// and the texture dictionary (.ytd) the model takes its textures from (0 if none).
/// </summary>
public readonly record struct ModelLocation(string Path, uint ModelHash, uint TextureDictionaryHash = 0);

/// <summary>
/// Index of the model files (.ydr, .ydd, .yft) the game uses (base game, update and DLC packs, see
/// <see cref="GameContent"/>), by name hash.
/// </summary>
public sealed class ModelCatalog
{
    private Dictionary<uint, string> _drawables = new();
    private Dictionary<uint, string> _dictionaries = new();
    private Dictionary<uint, string> _fragments = new();

    public int FileCount => _drawables.Count + _dictionaries.Count + _fragments.Count;

    /// <summary>Paths of the fragment files (.yft): vehicles, breakable props...</summary>
    public IEnumerable<string> FragmentFiles => _fragments.Values;

    public static ModelCatalog Create(GameContent content) => new()
    {
        _drawables = GameFileIndex.Build(content, ".ydr"),
        _dictionaries = GameFileIndex.Build(content, ".ydd"),
        _fragments = GameFileIndex.Build(content, ".yft"),
    };

    /// <summary>Finds the file holding the model of <paramref name="archetype"/>, or null if it has none.</summary>
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
