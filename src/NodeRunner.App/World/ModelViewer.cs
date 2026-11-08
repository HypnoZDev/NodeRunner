using System.Numerics;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// The model viewer: one model (vehicle or object) read on a background thread with its textures, drawn alone into its
// own image by a camera turning around it.
internal sealed class ModelViewer : IDisposable
{
    private static readonly Vector4 Background = new(0.16f, 0.17f, 0.19f, 1f);

    // Front three-quarter view, slightly from above (a vehicle faces +Y).
    private const float DefaultYaw = -0.93f;
    private const float DefaultPitch = -0.32f;

    private readonly Camera _camera = new() { FieldOfView = MathF.PI / 4f };
    private readonly PreviewModel _model = new();
    private RenderTarget? _target;
    private IntPtr _imageId;
    private Vector3 _center;
    private float _radius = 1f;

    public ViewerItem? Item => _model.Item;
    public bool IsLoading => _model.IsLoading;

    public string? Error => _model.Error;

    public int TriangleCount => _model.TriangleCount;
    public int MeshCount => _model.MeshCount;
    public int TextureCount => _model.TextureCount;
    public int MissingTextureCount => MissingTextures.Count;

    public IReadOnlyList<string> MissingTextures => _model.MissingTextures;
    public Vector3 Size => _model.BoundsMax - _model.BoundsMin;

    public float Yaw { get; set; } = DefaultYaw;
    public float Pitch { get => _camera.Pitch; set => _camera.Pitch = value; }
    public float Distance { get; set; } = 10f;

    public Vector2 RequestedSize { get; set; }

    public IntPtr ImageId => _imageId;

    public void Open(ViewerItem item, WorldLoader world) => _model.Open(item, world);

    public void ResetView()
    {
        Yaw = DefaultYaw;
        Pitch = DefaultPitch;
        Distance = FitDistance();
    }

    public float MinDistance => _radius * 0.3f;
    public float MaxDistance => _radius * 12f;

    private float FitDistance() => _radius / MathF.Sin(_camera.FieldOfView * 0.5f) * 0.85f;

    // Takes the model once read, and draws the image if the interface shows it.
    public void Render(ModelRenderer renderer, ImGuiRenderer imgui, GraphicsDevice graphics)
    {
        if (_model.Update(renderer))
        {
            _center = (_model.BoundsMin + _model.BoundsMax) * 0.5f;
            _radius = Math.Max(0.05f, Size.Length() * 0.5f);
            ResetView();
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
        if (_model.Model is not { } model)
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
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.Begin(ModelPass.Translucent);
        renderer.Draw(model, world, viewProjection, Vector3.One);
        renderer.Lighting = sceneLighting;
        renderer.Shadows = sceneShadows;
    }

    public void Dispose()
    {
        _model.Dispose();
        _target?.Dispose();
        _target = null;
    }
}
