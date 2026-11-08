using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// One model (vehicle or object) read on a background thread with its textures, then put on the graphics card: the
// model viewer's, and the car shown on a selected parking spot.
internal sealed class PreviewModel : IDisposable
{
    private readonly List<GpuTexture> _textures = [];
    private Task<Loaded>? _loading;

    private sealed record Loaded(ViewerItem Item, ModelData? Model, ResolvedTexture?[][] Textures, List<string> Errors);

    public ViewerItem? Item { get; private set; }
    public bool IsLoading => _loading is not null;
    public string? Error { get; private set; }

    public GpuModel? Model { get; private set; }
    public int TriangleCount => Model?.TriangleCount ?? 0;
    public int MeshCount { get; private set; }
    public int TextureCount { get; private set; }
    public IReadOnlyList<string> MissingTextures { get; private set; } = [];
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    public void Open(ViewerItem item, WorldLoader world)
    {
        if (item == Item && Error is null)
            return;
        Item = item;
        Error = null;
        var (reader, textures, textureReader) = (world.ModelReader, world.Textures, world.TextureReader);
        _loading = Task.Run(() => Read(item, reader, textures, textureReader));
    }

    // Forgets the model.
    public void Close()
    {
        Item = null;
        Error = null;
        Release();
    }

    private static Loaded Read(ViewerItem item, IModelReader? reader, Data.Textures.TextureCatalog? catalog, Data.Textures.ITextureReader? textureReader)
    {
        var errors = new List<string>();
        if (reader is null)
            return new Loaded(item, null, [], [L("aucune carte chargée")]);
        try
        {
            var model = reader.ReadModels(item.Location.Path).GetValueOrDefault(item.Location.ModelHash);
            if (model is null)
                return new Loaded(item, null, [], [L("modèle introuvable dans le fichier")]);
            ResolvedTexture?[][] textures = [];
            if (catalog is not null && textureReader is not null)
            {
                // Vehicles also take textures from the dictionary shared by all vehicles (lights, badges, interior...).
                var fallback = item.Kind == ViewerKind.Vehicle && catalog.Locate(Joaat.Hash("vehshare")) is { } shared ? [shared] : (string[])[];
                textures = new TextureResolver(catalog, textureReader).Resolve(item.Location, model, errors, fallback);
            }
            return new Loaded(item, model, textures, errors);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return new Loaded(item, null, [], [ex.Message]);
        }
    }

    // Takes the model once read; true when a new model was just put on the graphics card.
    public bool Update(ModelRenderer renderer)
    {
        if (_loading is not { IsCompleted: true } finished)
            return false;
        _loading = null;
        var loaded = finished.Result;
        if (loaded.Item != Item)
            return false; // Another model was asked for meanwhile.
        Release();
        if (loaded.Model is not { } data)
        {
            Error = string.Join(" ; ", loaded.Errors);
            return false;
        }

        var uploaded = new Dictionary<TextureKey, GpuTexture?>();
        var meshTextures = new GpuTexture?[loaded.Textures.Length][];
        for (int i = 0; i < meshTextures.Length; i++)
        {
            meshTextures[i] = new GpuTexture?[loaded.Textures[i].Length];
            for (int t = 0; t < meshTextures[i].Length; t++)
            {
                if (loaded.Textures[i][t] is not { } resolved)
                    continue;
                if (!uploaded.TryGetValue(resolved.Key, out var texture))
                {
                    uploaded[resolved.Key] = texture = renderer.UploadTexture(resolved.Data!);
                    if (texture is not null)
                        _textures.Add(texture);
                }
                meshTextures[i][t] = texture;
            }
        }
        Model = renderer.Upload(data, meshTextures);
        if (Model is null)
        {
            Error = L("modèle vide");
            return false;
        }

        MeshCount = data.Meshes.Count;
        TextureCount = _textures.Count;
        MissingTextures = data.Meshes
            .Where((m, i) => m.DiffuseTextureName is not null && (i >= loaded.Textures.Length || loaded.Textures[i][0] is null))
            .Select(m => m.DiffuseTextureName!)
            .Distinct()
            .ToList();
        BoundsMin = data.BoundsMin;
        BoundsMax = data.BoundsMax;
        return true;
    }

    private void Release()
    {
        Model?.Dispose();
        Model = null;
        foreach (var texture in _textures)
            texture.Dispose();
        _textures.Clear();
        MeshCount = TextureCount = 0;
        MissingTextures = [];
        BoundsMin = BoundsMax = Vector3.Zero;
    }

    public void Dispose() => Release();
}
