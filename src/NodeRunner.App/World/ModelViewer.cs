using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using NodeRunner.Render;

namespace NodeRunner.App.World;

internal enum ViewerShading { Textured, Clay, Wireframe }

// The model viewer: one model (vehicle or object) read on a background thread with its textures, drawn alone into its
// own image by a camera turning around it, with display options (shading, edges, ground, box), the vehicle's paint,
// parts hidden or highlighted, and image captures.
internal sealed class ModelViewer : IDisposable
{
    public static readonly (string Name, Vector4 Color)[] Backgrounds =
    [
        ("Sombre", new(0.16f, 0.17f, 0.19f, 1f)),
        ("Gris", new(0.38f, 0.39f, 0.41f, 1f)),
        ("Clair", new(0.82f, 0.83f, 0.85f, 1f)),
        ("Noir", new(0.02f, 0.02f, 0.02f, 1f)),
        ("Vert (incrustation)", new(0f, 0.69f, 0.25f, 1f)),
    ];

    // Front three-quarter view, slightly from above (a vehicle faces +Y).
    private const float DefaultYaw = -0.93f;
    private const float DefaultPitch = -0.32f;

    private readonly Camera _camera = new() { FieldOfView = MathF.PI / 4f };
    private readonly PreviewModel _model = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private RenderTarget? _target;
    private RenderTarget? _capture;
    private IntPtr _imageId;
    private Vector3 _center;
    private float _radius = 1f;
    private double _lastTime;
    private (string Path, int Scale, bool Transparent)? _captureRequest;

    // Thumbnails of the model's textures, for the interface (ImGui ids).
    private readonly Dictionary<GpuTexture, IntPtr> _thumbnails = new();
    private int _thumbnailsVersion = -1;

    public ViewerItem? Item => _model.Item;
    public bool IsLoading => _model.IsLoading;
    public string? Error => _model.Error;
    public PreviewModel Model => _model;

    public int TriangleCount => _model.TriangleCount;
    public int MeshCount => _model.MeshCount;
    public int TextureCount => _model.TextureCount;
    public int MissingTextureCount => MissingTextures.Count;
    public IReadOnlyList<string> MissingTextures => _model.MissingTextures;
    public Vector3 Size => _model.BoundsMax - _model.BoundsMin;

    public float Yaw { get; set; } = DefaultYaw;
    public float Pitch { get => _camera.Pitch; set => _camera.Pitch = value; }
    public float Distance { get; set; } = 10f;
    public float FieldOfViewDegrees { get; set; } = 45f;

    // Where the camera looks, from the middle of the model (moved by panning).
    public Vector3 Pan { get; set; }

    public ViewerShading Shading { get; set; }
    public bool Edges { get; set; }
    public bool ShowBounds { get; set; }
    public bool ShowGround { get; set; } = true;
    public bool AutoRotate { get; set; }
    public int Background { get; set; }

    // Light: turns with the camera (always lights the side seen), or fixed at LightAngle around the model.
    public bool LightFollowsCamera { get; set; } = true;
    public float LightAngle { get; set; } = 0.6f;
    public float LightHeight { get; set; } = 0.8f;

    // Colours by VehiclePaintSlot (null: parts keep their texture colour).
    public Vector3[]? PaintColors { get; set; }

    public HashSet<int> HiddenMeshes { get; } = new();
    public int HighlightedMesh { get; set; } = -1;

    public Vector2 RequestedSize { get; set; }
    public IntPtr ImageId => _imageId;

    // Result of the last capture (path of the image, or the error), for a message.
    public (string Text, bool Error)? CaptureResult { get; set; }

    public void Open(ViewerItem item, WorldLoader world)
    {
        if (item != _model.Item)
        {
            HiddenMeshes.Clear();
            HighlightedMesh = -1;
        }
        _model.Open(item, world);
    }

    public void ResetView()
    {
        Yaw = DefaultYaw;
        Pitch = DefaultPitch;
        Pan = Vector3.Zero;
        Distance = FitDistance();
    }

    public void SetView(float yaw, float pitch)
    {
        Yaw = yaw;
        Pitch = pitch;
        Pan = Vector3.Zero;
        Distance = FitDistance();
    }

    public float MinDistance => _radius * 0.1f;
    public float MaxDistance => _radius * 12f;
    public float Radius => _radius;

    private float FitDistance() => _radius / MathF.Sin(_camera.FieldOfView * 0.5f) * 0.85f;

    // Pan by a mouse move (pixels of the image).
    public void PanBy(Vector2 delta, float imageHeight)
    {
        float perPixel = 2f * Distance * MathF.Tan(_camera.FieldOfView * 0.5f) / Math.Max(1f, imageHeight);
        _camera.Yaw = Yaw;
        Pan += (-_camera.Right * delta.X + Vector3.Cross(_camera.Right, _camera.Forward) * delta.Y) * perPixel;
    }

    public void RequestCapture(string path, int scale, bool transparent) => _captureRequest = (path, scale, transparent);

    public IntPtr Thumbnail(GpuTexture texture) => _thumbnails.GetValueOrDefault(texture);

    // Takes the model once read, and draws the image if the interface shows it.
    public void Render(ModelRenderer renderer, ImGuiRenderer imgui, GraphicsDevice graphics, ColorRenderer? lines)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float delta = (float)Math.Min(0.1, now - _lastTime);
        _lastTime = now;
        if (_model.Update(renderer))
        {
            _center = (_model.BoundsMin + _model.BoundsMax) * 0.5f;
            _radius = Math.Max(0.05f, Size.Length() * 0.5f);
            ResetView();
        }
        UpdateThumbnails(imgui);

        int width = (int)RequestedSize.X, height = (int)RequestedSize.Y;
        if (width < 2 || height < 2)
            return;
        if (AutoRotate)
            Yaw += delta * 0.45f;
        _target ??= new RenderTarget(graphics.Device, graphics.Context);
        var background = Backgrounds[Math.Clamp(Background, 0, Backgrounds.Length - 1)].Color;
        if (_target.Begin(width, height, background) || _imageId == IntPtr.Zero)
        {
            if (_imageId != IntPtr.Zero)
                imgui.UnregisterTexture(_imageId);
            _imageId = imgui.RegisterTexture(_target.View!);
        }
        if (_model.Model is not { } model)
            return;
        DrawScene(renderer, lines, model, (float)width / height);

        if (_captureRequest is { } request)
        {
            _captureRequest = null;
            Capture(renderer, graphics, lines, model, width, height, background, request);
        }
    }

    private void DrawScene(ModelRenderer renderer, ColorRenderer? lines, GpuModel model, float aspect)
    {
        _camera.FieldOfView = Math.Clamp(FieldOfViewDegrees, 10f, 100f) * MathF.PI / 180f;
        _camera.Yaw = Yaw;
        _camera.NearPlane = Math.Max(0.01f, Distance * 0.02f);
        _camera.Position = -_camera.Forward * Distance;
        var viewProjection = _camera.ViewProjection(aspect);
        var offset = -_center - Pan;
        var world = Matrix4x4.CreateTranslation(offset);

        var light = LightFollowsCamera
            ? -_camera.Forward + _camera.Right * -0.4f + Vector3.UnitZ * LightHeight
            : new Vector3(MathF.Cos(LightAngle), MathF.Sin(LightAngle), LightHeight);
        var sceneLighting = renderer.Lighting;
        var sceneShadows = renderer.Shadows;
        renderer.Shadows = null;
        renderer.Lighting = SceneLighting.Neutral with { SunDirection = Vector3.Normalize(light) };
        renderer.CameraPosition = _camera.Position;
        renderer.VehiclePaint = PaintColors;
        renderer.HiddenMeshes = HiddenMeshes.Count > 0 ? HiddenMeshes : null;
        renderer.HighlightedMesh = HighlightedMesh;
        try
        {
            renderer.Wireframe = Shading == ViewerShading.Wireframe;
            renderer.Untextured = Shading == ViewerShading.Clay;
            renderer.Begin(ModelPass.Solid);
            renderer.Draw(model, world, viewProjection, Vector3.One);
            renderer.Begin(ModelPass.Translucent);
            renderer.Draw(model, world, viewProjection, Vector3.One);
            if (Edges && Shading != ViewerShading.Wireframe)
            {
                // Edges over the model: dark lines, slightly in front.
                renderer.Wireframe = true;
                renderer.Untextured = true;
                renderer.VehiclePaint = null;
                renderer.Begin(ModelPass.Solid);
                renderer.Draw(model, world, viewProjection, new Vector3(0.06f));
            }
        }
        finally
        {
            renderer.Wireframe = false;
            renderer.Untextured = false;
            renderer.VehiclePaint = null;
            renderer.HiddenMeshes = null;
            renderer.HighlightedMesh = -1;
            renderer.Lighting = sceneLighting;
            renderer.Shadows = sceneShadows;
        }

        if (lines is null)
            return;
        var min = _model.BoundsMin + offset;
        var max = _model.BoundsMax + offset;
        if (ShowGround)
            AddGround(lines, min, max);
        if (ShowBounds)
            lines.AddBox(min, max, Matrix4x4.Identity, new Vector4(1f, 0.75f, 0.2f, 0.9f));
        lines.Flush(viewProjection, _camera.Position);
    }

    // Grid under the model: 1 m squares (5 m for big models), one line in two brighter.
    private void AddGround(ColorRenderer lines, Vector3 min, Vector3 max)
    {
        float step = _radius > 12f ? 5f : _radius > 4f ? 1f : 0.25f;
        float extent = MathF.Ceiling(_radius * 1.6f / step) * step;
        var middle = (min + max) * 0.5f;
        float cx = MathF.Round(middle.X / step) * step, cy = MathF.Round(middle.Y / step) * step;
        float z = min.Z;
        int count = (int)(extent / step);
        for (int i = -count; i <= count; i++)
        {
            float alpha = i == 0 ? 0.35f : i % 5 == 0 ? 0.2f : 0.09f;
            var color = new Vector4(1f, 1f, 1f, alpha);
            lines.AddLine(new Vector3(cx + i * step, cy - extent, z), new Vector3(cx + i * step, cy + extent, z), color);
            lines.AddLine(new Vector3(cx - extent, cy + i * step, z), new Vector3(cx + extent, cy + i * step, z), color);
        }
    }

    private void Capture(ModelRenderer renderer, GraphicsDevice graphics, ColorRenderer? lines, GpuModel model, int width, int height,
        Vector4 background, (string Path, int Scale, bool Transparent) request)
    {
        try
        {
            int scale = Math.Clamp(request.Scale, 1, 4);
            int w = Math.Min(width * scale, 8192), h = Math.Min(height * scale, 8192);
            _capture ??= new RenderTarget(graphics.Device, graphics.Context);
            _capture.Begin(w, h, request.Transparent ? background with { W = 0f } : background);
            bool ground = ShowGround;
            if (request.Transparent)
                ShowGround = false;
            DrawScene(renderer, lines, model, (float)w / h);
            ShowGround = ground;
            var pixels = _capture.ReadPixels();
            string path = request.Path;
            Task.Run(() =>
            {
                try
                {
                    SavePng(pixels, w, h, path);
                    CaptureResult = (path, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
                {
                    CaptureResult = (ex.Message, true);
                }
            });
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or OutOfMemoryException)
        {
            CaptureResult = (ex.Message, true);
        }
    }

    private static void SavePng(byte[] pixels, int width, int height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    private void UpdateThumbnails(ImGuiRenderer imgui)
    {
        if (_thumbnailsVersion == _model.Version)
            return;
        _thumbnailsVersion = _model.Version;
        foreach (var id in _thumbnails.Values)
            imgui.UnregisterTexture(id);
        _thumbnails.Clear();
        foreach (var texture in _model.Textures)
            _thumbnails[texture.Texture] = imgui.RegisterTexture(texture.Texture);
    }

    public void Dispose()
    {
        _model.Dispose();
        _target?.Dispose();
        _target = null;
        _capture?.Dispose();
        _capture = null;
    }
}
