using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NodeRunner.Data.Textures;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Small pictures of the library's props: drawn one model at a time (only those shown), kept on disk
// (data\vignettes, redrawn when the model file changes) and on the graphics card for the most recently shown ones.
internal sealed class LibraryThumbnails(string cacheFolder) : IDisposable
{
    public const int Size = 128;
    private const int MaxTextures = 256;
    private const float Yaw = -0.93f;
    private const float Pitch = -0.32f;

    private sealed class Entry
    {
        public required ViewerItem Item;
        public required string File;
        public GpuTexture? Texture;
        public IntPtr Id;
        public long LastUse;
        public bool Failed;
        public bool SkipDisk;
        public Task<byte[]?>? DiskLoad;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly PreviewModel _model = new();
    private readonly Camera _camera = new() { FieldOfView = MathF.PI / 5f };
    private Entry? _drawing;
    private RenderTarget? _target;
    private long _frame;
    private int _textureCount;

    // The picture of a prop, or zero while it is being made (asked again at each frame it is shown).
    public IntPtr Get(ViewerItem item, WorldLoader world)
    {
        string key = Key(item, world);
        if (!_entries.TryGetValue(key, out var entry))
            _entries[key] = entry = new Entry { Item = item, File = Path.Combine(cacheFolder, FileName(item, key)) };
        entry.LastUse = _frame;
        return entry.Id;
    }

    public bool IsWorking => _drawing is not null || _entries.Values.Any(e => e.DiskLoad is not null);

    public void Render(ModelRenderer renderer, ImGuiRenderer imgui, GraphicsDevice graphics, WorldLoader world)
    {
        _frame++;
        foreach (var entry in _entries.Values)
        {
            if (entry.DiskLoad is { IsCompleted: true } load)
            {
                entry.DiskLoad = null;
                if (load.Result is { } pixels)
                    Upload(entry, pixels, imgui, graphics);
                else
                    entry.SkipDisk = true;
            }
        }

        if (_drawing is { } drawing)
        {
            _model.Update(renderer);
            if (_model.Model is { } model)
            {
                var pixels = Draw(renderer, graphics, model);
                Upload(drawing, pixels, imgui, graphics);
                string file = drawing.File;
                Task.Run(() => Save(pixels, file));
                _model.Close();
                _drawing = null;
            }
            else if (!_model.IsLoading)
            {
                drawing.Failed = true;
                _model.Close();
                _drawing = null;
            }
        }

        // Next picture: one shown at the last frame, from the disk when it is there, else drawn.
        if (_drawing is null)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Id != IntPtr.Zero || entry.Failed || entry.DiskLoad is not null || entry.LastUse < _frame - 2)
                    continue;
                if (!entry.SkipDisk && File.Exists(entry.File))
                {
                    string file = entry.File;
                    entry.DiskLoad = Task.Run(() => Load(file));
                    continue;
                }
                if (world.ModelReader is null)
                    break;
                _drawing = entry;
                _model.Open(entry.Item, world);
                break;
            }
        }

        Evict(imgui);
    }

    private byte[] Draw(ModelRenderer renderer, GraphicsDevice graphics, GpuModel model)
    {
        _target ??= new RenderTarget(graphics.Device, graphics.Context);
        _target.Begin(Size, Size, Vector4.Zero);
        var center = (_model.BoundsMin + _model.BoundsMax) * 0.5f;
        float radius = Math.Max(0.05f, (_model.BoundsMax - _model.BoundsMin).Length() * 0.5f);
        float distance = radius / MathF.Sin(_camera.FieldOfView * 0.5f) * 0.95f;
        _camera.Yaw = Yaw;
        _camera.Pitch = Pitch;
        _camera.NearPlane = Math.Max(0.01f, distance * 0.02f);
        _camera.Position = -_camera.Forward * distance;
        var viewProjection = _camera.ViewProjection(1f);
        var world = Matrix4x4.CreateTranslation(-center);

        var lighting = renderer.Lighting;
        var shadows = renderer.Shadows;
        renderer.Shadows = null;
        var light = -_camera.Forward + _camera.Right * -0.4f + Vector3.UnitZ * 0.8f;
        renderer.Lighting = SceneLighting.Neutral with { SunDirection = Vector3.Normalize(light) };
        renderer.CameraPosition = _camera.Position;
        try
        {
            renderer.Begin(ModelPass.Solid);
            renderer.Draw(model, world, viewProjection, Vector3.One);
            renderer.Begin(ModelPass.Translucent);
            renderer.Draw(model, world, viewProjection, Vector3.One);
        }
        finally
        {
            renderer.Lighting = lighting;
            renderer.Shadows = shadows;
        }
        return _target.ReadPixels();
    }

    private void Upload(Entry entry, byte[] pixels, ImGuiRenderer imgui, GraphicsDevice graphics)
    {
        if (pixels.Length != Size * Size * 4)
        {
            entry.Failed = true;
            return;
        }
        var texture = GpuTexture.Create(graphics.Device, new TextureData
        {
            NameHash = 0,
            Name = "thumbnail",
            Width = Size,
            Height = Size,
            MipLevels = 1,
            Format = TextureFormat.B8G8R8A8,
            Data = pixels,
        });
        if (texture is null)
        {
            entry.Failed = true;
            return;
        }
        entry.Texture = texture;
        entry.Id = imgui.RegisterTexture(texture);
        _textureCount++;
    }

    // Least recently shown pictures leave the graphics card (they stay on disk).
    private void Evict(ImGuiRenderer imgui)
    {
        if (_textureCount <= MaxTextures)
            return;
        foreach (var entry in _entries.Values.Where(e => e.Texture is not null && e.LastUse < _frame - 2).OrderBy(e => e.LastUse).Take(_textureCount - MaxTextures).ToList())
            Release(entry, imgui);
    }

    private void Release(Entry entry, ImGuiRenderer? imgui)
    {
        if (entry.Texture is null)
            return;
        if (entry.Id != IntPtr.Zero)
            imgui?.UnregisterTexture(entry.Id);
        entry.Texture?.Dispose();
        entry.Texture = null;
        entry.Id = IntPtr.Zero;
        _textureCount--;
    }

    // Changes with the model file, so that a new version of a pack gets new pictures.
    private static string Key(ViewerItem item, WorldLoader world)
    {
        string? disk = world.ProjectFiles?.LibraryFileOf(item.Location.Path)?.Disk;
        string stamp = "";
        try
        {
            if (disk is not null && new FileInfo(disk) is { Exists: true } info)
                stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return $"{item.Location.Path}|{item.Location.ModelHash}|{disk}|{stamp}";
    }

    private static string FileName(ViewerItem item, string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        string name = string.Concat(item.Name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        return $"{name}-{hash}.png";
    }

    private static void Save(byte[] pixels, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < Size; y++)
                    Marshal.Copy(pixels, y * Size * 4, data.Scan0 + y * data.Stride, Size * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            bitmap.Save(path + ".tmp", ImageFormat.Png);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            // Drawn again next time.
        }
    }

    private static byte[]? Load(string path)
    {
        try
        {
            using var bitmap = new Bitmap(path);
            if (bitmap.Width != Size || bitmap.Height != Size)
                return null;
            var pixels = new byte[Size * Size * 4];
            var data = bitmap.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < Size; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * Size * 4, Size * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return pixels;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            Release(entry, null);
        _entries.Clear();
        _model.Dispose();
        _target?.Dispose();
        _target = null;
    }
}
