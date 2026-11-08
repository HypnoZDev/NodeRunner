using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Off-screen colour image with its depth buffer, drawn into like the screen and then shown in the interface
// (ImGui.Image with the id from RegisterTexture of View).
public sealed class RenderTarget : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Texture2D? _color;
    private ID3D11RenderTargetView? _colorView;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;

    public RenderTarget(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public ID3D11ShaderResourceView? View { get; private set; }

    // Resizes the image if needed (returns true then: View is a new object), binds it and clears it.
    public bool Begin(int width, int height, Vector4 clearColor)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool resized = width != Width || height != Height || _color is null;
        if (resized)
            Create(width, height);

        _context.OMSetRenderTargets(_colorView!, _depthView);
        _context.RSSetViewport(new Viewport(0, 0, Width, Height));
        _context.ClearRenderTargetView(_colorView!, new Color4(clearColor));
        _context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 0f, 0); // Reversed Z: 0 = far.
        return resized;
    }

    // The image as 4 bytes per pixel (blue, green, red, alpha), top row first.
    public byte[] ReadPixels()
    {
        if (_color is null)
            return [];
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = GraphicsDevice.ColorFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _context.CopyResource(staging, _color);
        var map = _context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[Width * Height * 4];
        try
        {
            unsafe
            {
                for (int y = 0; y < Height; y++)
                    new ReadOnlySpan<byte>((byte*)map.DataPointer + (long)y * map.RowPitch, Width * 4).CopyTo(pixels.AsSpan(y * Width * 4));
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
        return pixels;
    }

    private void Create(int width, int height)
    {
        DisposeTargets();
        Width = width;
        Height = height;

        _color = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = GraphicsDevice.ColorFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _colorView = _device.CreateRenderTargetView(_color);
        View = _device.CreateShaderResourceView(_color);

        _depth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _depthView = _device.CreateDepthStencilView(_depth);
    }

    private void DisposeTargets()
    {
        View?.Dispose();
        _colorView?.Dispose();
        _color?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        View = null;
        _colorView = null;
        _color = null;
        _depthView = null;
        _depth = null;
    }

    public void Dispose() => DisposeTargets();
}
