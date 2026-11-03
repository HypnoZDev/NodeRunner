using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;

namespace NodeRunner.App.World;

// Identifies a texture: the file it comes from (.ytd, or the model file for embedded ones) and its name.
internal readonly record struct TextureKey(string Source, uint NameHash);

internal sealed record ResolvedTexture(TextureKey Key, TextureData Data);

// Finds the colour texture of each mesh of a model, as the game does: first in the model file itself, then in the
// archetype's texture dictionary (.ytd), then in its parent dictionaries.
internal sealed class TextureResolver(TextureCatalog catalog, ITextureReader reader)
{
    private const int RecentFileCacheSize = 32;

    private readonly Dictionary<string, IReadOnlyDictionary<uint, TextureData>> _recentFiles = new();
    private readonly Queue<string> _recentOrder = new();

    // The textures of each mesh of model (same order; for each mesh, same order as TextureNames), null when it has
    // none or it was not found.
    public ResolvedTexture?[][] Resolve(ModelLocation location, ModelData model, List<string> errors, IReadOnlyList<string>? fallbackDictionaries = null)
    {
        var result = new ResolvedTexture?[model.Meshes.Count][];
        List<string>? chain = null;
        for (int i = 0; i < result.Length; i++)
        {
            var names = model.Meshes[i].TextureNames;
            result[i] = new ResolvedTexture?[names.Count];
            for (int t = 0; t < names.Count; t++)
            {
                if (names[t] is not { } text)
                    continue;
                uint name = Joaat.Hash(text);
                if (model.EmbeddedTextures.TryGetValue(name, out var embedded))
                {
                    result[i][t] = new ResolvedTexture(new TextureKey(location.Path, name), embedded);
                    continue;
                }

                chain ??= [.. catalog.GetSearchChain(location.TextureDictionaryHash), .. fallbackDictionaries ?? []];
                foreach (var path in chain)
                {
                    if (Read(path, errors).TryGetValue(name, out var texture))
                    {
                        result[i][t] = new ResolvedTexture(new TextureKey(path, name), texture);
                        break;
                    }
                }
            }
        }
        return result;
    }

    private IReadOnlyDictionary<uint, TextureData> Read(string path, List<string> errors)
    {
        if (_recentFiles.TryGetValue(path, out var textures))
            return textures;
        try
        {
            textures = reader.ReadTextures(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any error: the models using this dictionary are still shown, without its textures.
            errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            textures = new Dictionary<uint, TextureData>(); // Remembered so it is not read again.
        }

        _recentFiles[path] = textures;
        _recentOrder.Enqueue(path);
        if (_recentOrder.Count > RecentFileCacheSize)
            _recentFiles.Remove(_recentOrder.Dequeue());
        return textures;
    }
}
