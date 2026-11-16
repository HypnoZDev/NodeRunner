using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;
using NodeRunner.Render;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using TextureLayout = NodeRunner.Data.Textures.TextureLayout;

namespace NodeRunner.Tests;

// Direct3D's software renderer (part of Windows): runs without a graphics card, on any PC or build machine.
public sealed class SoftwareDevice : IDisposable
{
    public SoftwareDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
        if (result.Failure || device is null || context is null)
            throw new InvalidOperationException($"Direct3D 11 software device: {result}");
        Device = device;
        Context = context;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}

// Measures memory: run alone, not alongside the other tests.
[CollectionDefinition(nameof(RenderCollection), DisableParallelization = true)]
public sealed class RenderCollection : ICollectionFixture<SoftwareDevice>;

[Collection(nameof(RenderCollection))]
public class RenderTests(SoftwareDevice gpu)
{
    private const int Size = 64;

    [Fact]
    public void EveryRenderer_CompilesItsShaders()
    {
        new ModelRenderer(gpu.Device, gpu.Context).Dispose();
        new ColorRenderer(gpu.Device, gpu.Context).Dispose();
        new SkyRenderer(gpu.Device, gpu.Context).Dispose();
        new WaterRenderer(gpu.Device, gpu.Context).Dispose();
        new ShadowMaps(gpu.Device, gpu.Context).Dispose();

        var imgui = ImGui.CreateContext();
        try
        {
            ImGui.SetCurrentContext(imgui);
            ImGui.GetIO().Fonts.AddFontDefault();
            new ImGuiRenderer(gpu.Device, gpu.Context).Dispose();
        }
        finally
        {
            ImGui.DestroyContext(imgui);
        }
    }

    [Theory]
    [InlineData(TextureFormat.Bc1)]
    [InlineData(TextureFormat.Bc2)]
    [InlineData(TextureFormat.Bc3)]
    [InlineData(TextureFormat.Bc4)]
    [InlineData(TextureFormat.Bc5)]
    [InlineData(TextureFormat.Bc7)]
    [InlineData(TextureFormat.B8G8R8A8)]
    [InlineData(TextureFormat.B8G8R8X8)]
    [InlineData(TextureFormat.R8G8B8A8)]
    [InlineData(TextureFormat.B5G5R5A1)]
    [InlineData(TextureFormat.A8)]
    public void Texture_IsCreatedWithAllItsMipLevels(TextureFormat format)
    {
        var texture = Texture(format, 32, 32, mipLevels: 6);
        using var gpuTexture = GpuTexture.Create(gpu.Device, texture);

        Assert.NotNull(gpuTexture);
        Assert.Equal(texture.Data.Length, gpuTexture.ByteSize);
    }

    [Fact]
    public void Texture_ThatCannotBeShown_IsRefused()
    {
        // Compressed textures must be made of whole 4x4 blocks; L8 has no matching GPU format; no data at all.
        Assert.Null(GpuTexture.Create(gpu.Device, Texture(TextureFormat.Bc1, 6, 6, 1)));
        Assert.Null(GpuTexture.Create(gpu.Device, Texture(TextureFormat.L8, 8, 8, 1)));
        Assert.Null(GpuTexture.Create(gpu.Device, Texture(TextureFormat.Unknown, 8, 8, 1)));
        var empty = new TextureData { NameHash = 0, Name = "empty", Width = 8, Height = 8, MipLevels = 1, Format = TextureFormat.R8G8B8A8, Data = [] };
        Assert.Null(GpuTexture.Create(gpu.Device, empty));
    }

    [Fact]
    public void Model_IsDrawnWhereTheCameraLooks()
    {
        using var renderer = new ModelRenderer(gpu.Device, gpu.Context);
        using var target = new RenderTarget(gpu.Device, gpu.Context);
        using var model = renderer.Upload(Cube(), [[]]);
        Assert.NotNull(model);
        Assert.Equal(24, model.TriangleCount);

        var clear = new Vector4(0f, 0f, 0f, 1f);
        target.Begin(Size, Size, clear);
        renderer.CameraPosition = CameraPosition;
        renderer.Begin(ModelPass.Solid);
        renderer.Draw(model, Matrix4x4.Identity, ViewProjection(), Vector3.One);
        var pixels = target.ReadPixels();

        Assert.Equal(Size * Size * 4, pixels.Length);
        var centre = Pixel(pixels, Size / 2, Size / 2);
        Assert.True(centre.B + centre.G + centre.R > 30, $"centre pixel {centre}: the cube is not drawn");
        Assert.Equal((0, 0, 0), (Pixel(pixels, 1, 1).B, Pixel(pixels, 1, 1).G, Pixel(pixels, 1, 1).R));
    }

    [Fact]
    public void Click_FindsTheObjectUnderThePixel()
    {
        using var renderer = new ModelRenderer(gpu.Device, gpu.Context);
        using var pick = new PickBuffer(gpu.Device, gpu.Context);
        using var model = renderer.Upload(Cube(), [[]])!;

        pick.Begin(Size, Size);
        renderer.BeginPick();
        renderer.DrawPick(model, Matrix4x4.Identity, ViewProjection(), 42);

        Assert.Equal(42u, pick.Read(Size / 2, Size / 2));
        Assert.Equal(0u, pick.Read(1, 1));
        Assert.True(pick.ReadDepth(Size / 2, Size / 2) > 0f);
        Assert.Equal(0f, pick.ReadDepth(1, 1));
        Assert.Equal(0u, pick.Read(-1, Size)); // Outside the image.
    }

    [Fact]
    public void RenderTarget_FollowsTheViewSize()
    {
        using var target = new RenderTarget(gpu.Device, gpu.Context);

        Assert.True(target.Begin(32, 16, Vector4.One));
        Assert.False(target.Begin(32, 16, Vector4.One));
        Assert.True(target.Begin(8, 8, Vector4.One));
        Assert.Equal(8 * 8 * 4, target.ReadPixels().Length);
        Assert.True(target.Begin(0, 0, Vector4.One)); // A minimised window still gets a 1x1 image.
        Assert.Equal((1, 1), (target.Width, target.Height));
    }

    [Fact]
    public void LoadingAndFreeingModels_DoesNotKeepMemory()
    {
        // Software renderer: GPU objects live in the process's memory, so a leak shows there. 400 rounds of 1 MB of
        // texture and a model each: 400 MB would stay if nothing were freed.
        using var renderer = new ModelRenderer(gpu.Device, gpu.Context);
        var texture = Texture(TextureFormat.R8G8B8A8, 512, 512, 1);
        var cube = Cube();

        void Round()
        {
            using var gpuTexture = renderer.UploadTexture(texture)!;
            using var prepared = renderer.Prepare(cube)!;
            using var model = renderer.Finish(prepared, [[gpuTexture]]);
        }
        void Frame()
        {
            Round();
            gpu.Context.Flush(); // Direct3D destroys freed objects when work is sent to the GPU, as each frame does.
        }

        for (int i = 0; i < 20; i++)
            Frame();
        long before = PrivateMemory();
        for (int i = 0; i < 400; i++)
            Frame();
        long growth = PrivateMemory() - before;

        Assert.True(growth < 100L << 20, $"memory grew by {growth >> 20} MB");
    }

    private static readonly Vector3 CameraPosition = new(-6f, 0f, 0f);

    private static Matrix4x4 ViewProjection()
    {
        var camera = new Camera { Position = CameraPosition };
        camera.LookAt(Vector3.Zero);
        return camera.ViewProjection(1f);
    }

    private static long PrivateMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] pixels, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    private static TextureData Texture(TextureFormat format, int width, int height, int mipLevels)
    {
        long length = 0;
        for (int i = 0; i < mipLevels; i++)
            length += TextureLayout.Pitch(format, width >> i, height >> i).SlicePitch;
        var data = new byte[Math.Max(length, 0)];
        new Random(1).NextBytes(data);
        return new TextureData { NameHash = 0, Name = "test", Width = width, Height = height, MipLevels = mipLevels, Format = format, Data = data };
    }

    // A 2 m cube, white, with each face in both windings so that it shows whichever side faces the camera.
    private static ModelData Cube()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<ushort>();
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var u = MathF.Abs(normal.Z) > 0.5f ? Vector3.UnitX : Vector3.UnitZ;
            var v = Vector3.Cross(normal, u);
            int first = positions.Count;
            foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                positions.Add(normal + u * a + v * b);
                normals.Add(normal);
            }
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 })
                indices.Add((ushort)(first + i));
        }
        var mesh = new ModelMesh
        {
            Positions = [.. positions],
            Normals = [.. normals],
            TexCoords = new Vector2[positions.Count],
            Colors = Enumerable.Repeat(Vector4.One, positions.Count).ToArray(),
            Indices = [.. indices],
            VertexColored = true,
        };
        return new ModelData { NameHash = 1, BoundsMin = -Vector3.One, BoundsMax = Vector3.One, Meshes = [mesh] };
    }
}
