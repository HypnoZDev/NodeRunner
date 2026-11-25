using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// One model (vehicle or object) read on a background thread with its textures, then put on the graphics card: the
// model viewer's, and the car shown on a selected parking spot.
// A texture put on the graphics card for the model, with what the file says of it.
internal sealed record PreviewTexture(string Name, string Source, int Width, int Height, TextureFormat Format, int MipLevels,
    GpuTexture Texture);

internal sealed class PreviewModel : IDisposable
{
    private readonly List<GpuTexture> _textures = [];
    private Task<Loaded>? _loading;

    private sealed record Loaded(ViewerItem Item, ModelData? Model, ResolvedTexture?[][] Textures, List<string> Errors);

    public ViewerItem? Item { get; private set; }
    public bool IsLoading => _loading is not null;
    public string? Error { get; private set; }

    public GpuModel? Model { get; private set; }

    // The model as read (meshes, shaders), and its textures; the colour texture of each mesh (null if missing).
    public ModelData? Data { get; private set; }
    public IReadOnlyList<PreviewTexture> Textures { get; private set; } = [];
    public IReadOnlyList<PreviewTexture?> MeshTextures { get; private set; } = [];

    // The lighter version seen from afar (ModelSimplifier), worked out when asked for (RequestLod).
    public GpuModel? Lod { get; private set; }
    public int LodTriangleCount => Lod?.TriangleCount ?? 0;
    private Task<(ModelData Data, ModelData Lod, int[] Sources)>? _lodWork;
    private GpuTexture?[][] _meshTextures = [];

    public void RequestLod()
    {
        if (Data is not { } data || Lod is not null || _lodWork is not null)
            return;
        _lodWork = Task.Run(() =>
        {
            var (lod, sources) = ModelSimplifier.SimplifyWithSources(data, LodRatio);
            return (data, lod, sources);
        });
    }

    public const float LodRatio = 0.25f;

    private void TakeLod(ModelRenderer renderer)
    {
        if (_lodWork is not { IsCompleted: true } work)
            return;
        _lodWork = null;
        if (!work.IsCompletedSuccessfully || !ReferenceEquals(work.Result.Data, Data))
            return;
        var (_, lod, sources) = work.Result;
        var textures = sources.Select(i => i < _meshTextures.Length ? _meshTextures[i] : []).ToArray();
        Lod = renderer.Upload(lod, textures);
    }

    // Changes when another model is put on the graphics card.
    public int Version { get; private set; }
    public int TriangleCount => Model?.TriangleCount ?? 0;
    public int MeshCount { get; private set; }
    public int TextureCount { get; private set; }
    public IReadOnlyList<string> MissingTextures { get; private set; } = [];
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    public void Open(ViewerItem item, WorldLoader world) => Open(item, world.ModelReader, world.Textures, world.TextureReader);

    // With the readers of another game (GTA IV).
    public void Open(ViewerItem item, IModelReader? reader, TextureCatalog? textures, ITextureReader? textureReader)
    {
        // A model that could not be read is tried again only with other readers (map loaded again), not every frame.
        if (item == Item && (Error is null || ReferenceEquals(reader, _reader)))
            return;
        Reload(item, reader, textures, textureReader);
    }

    private IModelReader? _reader;

    // Reads the model again even when it is the one shown (its data changed); the current one stays until then.
    public void Reload(ViewerItem item, IModelReader? reader, TextureCatalog? textures, ITextureReader? textureReader)
    {
        Item = item;
        Error = null;
        _reader = reader;
        _loading = Task.Run(() => Read(item, reader, textures, textureReader));
    }

    // Forgets the model.
    public void Close()
    {
        Item = null;
        Error = null;
        Release();
    }

    private static readonly string[] SharedVehicleTextures = ["vehshare", "vehshare_truck"];

    private static Loaded Read(ViewerItem item, IModelReader? reader, TextureCatalog? catalog, ITextureReader? textureReader)
    {
        var errors = new List<string>();
        if (reader is null)
            return new Loaded(item, null, [], [L("aucune carte chargée")]);
        try
        {
            var model = item.Ped is { } ped
                ? ReadPed(reader, ped, item.Location.ModelHash)
                : reader.ReadModels(item.Location.Path, item.OwnBones ? item.Location.Path : null).GetValueOrDefault(item.Location.ModelHash);
            if (model is null)
                return new Loaded(item, null, [], [L("modèle introuvable dans le fichier")]);
            ResolvedTexture?[][] textures = [];
            if (catalog is not null && textureReader is not null)
            {
                // Vehicles also take textures from the dictionary shared by all vehicles (lights, badges, interior...);
                // peds stored one part per file have a texture dictionary per texture.
                IReadOnlyList<string> fallback = item.Kind == ViewerKind.Vehicle
                    ? SharedVehicleTextures.Select(n => catalog.Locate(Joaat.Hash(n))).OfType<string>().ToArray()
                    : [];
                textures = new TextureResolver(catalog, textureReader).Resolve(item.Location, model, errors, fallback,
                    namedDictionaries: item.Ped?.Textures);
            }
            return new Loaded(item, model, textures, errors);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return new Loaded(item, null, [], [ex.Message]);
        }
    }

    private static ModelData? ReadPed(IModelReader reader, PedFiles ped, uint pedHash)
    {
        var parts = ped.Dictionary is { } dictionary ? reader.ReadModels(dictionary, ped.Skeleton) : new Dictionary<uint, ModelData>();
        // One file per part, named after it: only the parts chosen are read.
        var files = ped.Parts.ToDictionary(p => Joaat.Hash(Path.GetFileNameWithoutExtension(p)));
        var read = new Dictionary<uint, ModelData?>();
        ModelData? Get(uint hash)
        {
            if (parts.TryGetValue(hash, out var part))
                return part;
            if (!read.TryGetValue(hash, out part))
                read[hash] = part = files.TryGetValue(hash, out var file) ? reader.ReadModels(file, ped.Skeleton).GetValueOrDefault(hash) : null;
            return part;
        }
        var textures = ped.Textures.Count == 0 ? null
            : ped.Textures.Select(t => Path.GetFileNameWithoutExtension(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PedModels.Compose(h => parts.ContainsKey(h) || files.ContainsKey(h), Get, pedHash, ped.Variant, ped.Outfit,
            textures is null ? null : textures.Contains);
    }

    // Takes the model once read; true when a new model was just put on the graphics card.
    public bool Update(ModelRenderer renderer)
    {
        TakeLod(renderer);
        if (_loading is not { IsCompleted: true } finished)
            return false;
        _loading = null;
        if (!finished.IsCompletedSuccessfully)
        {
            // An unexpected error while reading (damaged file): shown instead of stopping the program.
            Release();
            Error = finished.Exception?.GetBaseException().Message ?? L("Lecture impossible.");
            return false;
        }
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
        var infos = new Dictionary<GpuTexture, PreviewTexture>();
        var meshInfos = new PreviewTexture?[data.Meshes.Count];
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
                    {
                        _textures.Add(texture);
                        var d = resolved.Data!;
                        infos[texture] = new PreviewTexture(d.Name, resolved.Key.Source, d.Width, d.Height, d.Format, d.MipLevels, texture);
                    }
                }
                meshTextures[i][t] = texture;
                if (t == 0 && texture is not null && i < meshInfos.Length)
                    meshInfos[i] = infos[texture];
            }
        }
        Model = renderer.Upload(data, meshTextures);
        _meshTextures = meshTextures;
        if (Model is null)
        {
            Error = L("modèle vide");
            return false;
        }

        Data = data;
        Textures = infos.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        MeshTextures = meshInfos;
        Version++;
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
        Lod?.Dispose();
        Lod = null;
        _lodWork = null;
        _meshTextures = [];
        Data = null;
        Textures = [];
        MeshTextures = [];
        Version++;
        foreach (var texture in _textures)
            texture.Dispose();
        _textures.Clear();
        MeshCount = TextureCount = 0;
        MissingTextures = [];
        BoundsMin = BoundsMax = Vector3.Zero;
    }

    public void Dispose() => Release();
}
