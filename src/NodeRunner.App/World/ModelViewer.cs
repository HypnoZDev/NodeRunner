using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;
using NodeRunner.Render;

namespace NodeRunner.App.World;

/// <summary>
/// The model viewer: one model (vehicle or object) read on a background thread with its textures, drawn alone into
/// its own image by a camera turning around it. The interface (<c>ViewerPanel</c>) shows the image and moves the camera.
/// </summary>
internal sealed class ModelViewer : IDisposable
{
    private static readonly Vector4 Background = new(0.16f, 0.17f, 0.19f, 1f);

    // Front three-quarter view, slightly from above (a vehicle faces +Y).
    private const float DefaultYaw = -0.93f;
    private const float DefaultPitch = -0.32f;

    private readonly Camera _camera = new() { FieldOfView = MathF.PI / 4f };
    private readonly List<GpuTexture> _textures = [];
    private RenderTarget? _target;
    private IntPtr _imageId;
    private Task<Loaded>? _loading;
    private GpuModel? _model;
    private Vector3 _center;
    private float _radius = 1f;

    private sealed record Loaded(ViewerItem Item, ModelData? Model, ResolvedTexture?[][] Textures, List<string> Errors);

    public ViewerItem? Item { get; private set; }
    public bool IsLoading => _loading is not null;

    /// <summary>Why the model could not be shown, null if it is.</summary>
    public string? Error { get; private set; }

    public int TriangleCount => _model?.TriangleCount ?? 0;
    public int MeshCount { get; private set; }
    public int TextureCount { get; private set; }
    public int MissingTextureCount => MissingTextures.Count;

    /// <summary>Names of the colour textures not found in the model, its .ytd or their parents.</summary>
    public IReadOnlyList<string> MissingTextures { get; private set; } = [];
    public Vector3 Size { get; private set; }

    /// <summary>Camera angles (radians) and distance from the model's centre (metres).</summary>
    public float Yaw { get; set; } = DefaultYaw;
    public float Pitch { get => _camera.Pitch; set => _camera.Pitch = value; }
    public float Distance { get; set; } = 10f;

    /// <summary>Size of the image wanted by the interface, in pixels; 0 = not shown (nothing is drawn).</summary>
    public Vector2 RequestedSize { get; set; }

    /// <summary>Id of the image for ImGui.Image (zero until the first image is drawn).</summary>
    public IntPtr ImageId => _imageId;

    /// <summary>Starts reading <paramref name="item"/> (the previous model stays shown until it arrives).</summary>
    public void Open(ViewerItem item, WorldLoader world)
    {
        if (item == Item && Error is null)
            return;
        Item = item;
        Error = null;
        var (reader, textures, textureReader) = (world.ModelReader, world.Textures, world.TextureReader);
        _loading = Task.Run(() => Read(item, reader, textures, textureReader));
    }

    private static Loaded Read(ViewerItem item, IModelReader? reader, Data.Textures.TextureCatalog? catalog, Data.Textures.ITextureReader? textureReader)
    {
        var errors = new List<string>();
        if (reader is null)
            return new Loaded(item, null, [], ["aucune carte chargée"]);
        try
        {
            var model = reader.ReadModels(item.Location.Path).GetValueOrDefault(item.Location.ModelHash);
            if (model is null)
                return new Loaded(item, null, [], ["modèle introuvable dans le fichier"]);
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

    /// <summary>Back to the default view, the whole model in the image.</summary>
    public void ResetView()
    {
        Yaw = DefaultYaw;
        Pitch = DefaultPitch;
        Distance = FitDistance();
    }

    public float MinDistance => _radius * 0.3f;
    public float MaxDistance => _radius * 12f;

    private float FitDistance() => _radius / MathF.Sin(_camera.FieldOfView * 0.5f) * 0.85f;

    /// <summary>
    /// Takes the model once read, and draws the image if the interface shows it. Call once per frame, before the
    /// interface is built and before the back buffer is bound (this binds the viewer's image).
    /// </summary>
    public void Render(ModelRenderer renderer, ImGuiRenderer imgui, GraphicsDevice graphics)
    {
        if (_loading is { IsCompleted: true } finished)
        {
            _loading = null;
            Upload(renderer, finished.Result);
        }

        int width = (int)RequestedSize.X, height = (int)RequestedSize.Y;
        if (width < 2 || height < 2)
            return;
        _target ??= new RenderTarget(graphics.Device, graphics.Context);
        if (_target.Begin(width, height, Background) || _imageId == IntPtr.Zero)
        {
            if (_imageId != IntPtr.Zero)
                imgui.UnregisterTexture(_imageId);
            _imageId = imgui.RegisterTexture(_target.View!);
        }
        if (_model is null)
            return;

        _camera.Yaw = Yaw;
        _camera.NearPlane = Math.Max(0.01f, Distance * 0.02f);
        _camera.Position = -_camera.Forward * Distance;
        var viewProjection = _camera.ViewProjection((float)width / height);
        var world = Matrix4x4.CreateTranslation(-_center);
        // Studio light: from above, behind the camera's left shoulder, so the side seen is always lit.
        var sceneLighting = renderer.Lighting;
        var sceneShadows = renderer.Shadows;
        renderer.Shadows = null;
        renderer.Lighting = SceneLighting.Neutral with
        {
            SunDirection = Vector3.Normalize(-_camera.Forward + _camera.Right * -0.4f + Vector3.UnitZ * 0.8f),
        };
        renderer.Begin(ModelPass.Solid);
        renderer.Draw(_model, world, viewProjection, Vector3.One);
        renderer.Begin(ModelPass.Translucent);
        renderer.Draw(_model, world, viewProjection, Vector3.One);
        renderer.Lighting = sceneLighting;
        renderer.Shadows = sceneShadows;
    }

    private void Upload(ModelRenderer renderer, Loaded loaded)
    {
        if (loaded.Item != Item)
            return; // Another model was asked for meanwhile.
        if (loaded.Model is not { } data)
        {
            Error = string.Join(" ; ", loaded.Errors);
            ReleaseModel();
            return;
        }

        ReleaseModel();
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
                    uploaded[resolved.Key] = texture = renderer.UploadTexture(resolved.Data);
                    if (texture is not null)
                        _textures.Add(texture);
                }
                meshTextures[i][t] = texture;
            }
        }
        _model = renderer.Upload(data, meshTextures);
        if (_model is null)
        {
            Error = "modèle vide";
            return;
        }

        MeshCount = data.Meshes.Count;
        TextureCount = _textures.Count;
        MissingTextures = data.Meshes
            .Where((m, i) => m.DiffuseTextureName is not null && (i >= loaded.Textures.Length || loaded.Textures[i][0] is null))
            .Select(m => m.DiffuseTextureName!)
            .Distinct()
            .ToList();
        Size = data.BoundsMax - data.BoundsMin;
        _center = (data.BoundsMin + data.BoundsMax) * 0.5f;
        _radius = Math.Max(0.05f, Size.Length() * 0.5f);
        ResetView();
    }

    private void ReleaseModel()
    {
        _model?.Dispose();
        _model = null;
        foreach (var texture in _textures)
            texture.Dispose();
        _textures.Clear();
        MeshCount = TextureCount = 0;
        MissingTextures = [];
        Size = Vector3.Zero;
    }

    public void Dispose()
    {
        ReleaseModel();
        _target?.Dispose();
        _target = null;
    }
}
