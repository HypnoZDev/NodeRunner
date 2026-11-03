using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Textures;

// "Child" texture dictionary that falls back on "Parent" for the textures it does not hold.
public readonly record struct TextureParentLink(string Child, string Parent);

// Reads the texture dictionary relationships files (gtxd.ymt, gtxd.meta).
public interface ITextureParentReader
{
    IReadOnlyList<TextureParentLink> ReadParents(byte[] data);
}

// Index of the texture dictionaries (.ytd) the game uses (base game, update and DLC packs, see GameContent), by name
// hash, with their parents: a model looks for a texture in its own dictionary, then in the parent, the
// grand-parent...
public sealed class TextureCatalog
{
    private const int MaxChainLength = 16;

    private readonly Dictionary<uint, string> _dictionaries;
    private readonly Dictionary<uint, uint> _parents;

    public TextureCatalog(Dictionary<uint, string> dictionaries, Dictionary<uint, uint> parents)
    {
        _dictionaries = dictionaries;
        _parents = parents;
    }

    public int FileCount => _dictionaries.Count;
    public int ParentLinkCount => _parents.Count;

    public List<string> Errors { get; } = new();

    public static TextureCatalog Create(GameContent content, ITextureParentReader parentReader)
    {
        var parents = new Dictionary<uint, uint>();
        var errors = new List<string>();

        // Every pack can bring its own gtxd file: they add up, the latest one winning for a given dictionary.
        var relationshipFiles = content.EnumerateAllVersions(".ymt").Concat(content.EnumerateAllVersions(".meta"))
            .Where(p => Path.GetFileNameWithoutExtension(p).EndsWith("gtxd", StringComparison.OrdinalIgnoreCase));
        foreach (var path in relationshipFiles)
        {
            try
            {
                var data = content.ReadFile(path);
                if (data is null)
                    continue;
                foreach (var link in parentReader.ReadParents(data))
                    parents[Joaat.Hash(link.Child)] = Joaat.Hash(link.Parent);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            }
        }

        var catalog = new TextureCatalog(GameFileIndex.Build(content, ".ytd"), parents);
        catalog.Errors.AddRange(errors);
        return catalog;
    }

    public string? Locate(uint dictionaryHash) => _dictionaries.GetValueOrDefault(dictionaryHash);

    public uint GetParent(uint dictionaryHash) => _parents.GetValueOrDefault(dictionaryHash);

    // Paths of the .ytd files to search for a texture, in order: the dictionary itself, then its parents.
    public List<string> GetSearchChain(uint dictionaryHash)
    {
        var chain = new List<string>();
        var seen = new HashSet<uint>();
        for (uint hash = dictionaryHash; hash != 0 && seen.Count < MaxChainLength && seen.Add(hash); hash = GetParent(hash))
        {
            if (Locate(hash) is { } path)
                chain.Add(path);
        }
        return chain;
    }
}
