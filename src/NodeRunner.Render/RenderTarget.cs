using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

/// <summary>
/// Off-screen colour image with its depth buffer, drawn into like the screen and then shown in the interface
/// (ImGui.Image with the id from <see cref="ImGuiRenderer.RegisterTexture"/> of <see cref="View"/>).
/// </summary>
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

    /// <summary>The image, for the interface. Changes when the size changes (see <see cref="Begin"/>).</summary>
    public ID3D11ShaderResourceView? View { get; private set; }

    /// <summary>
    /// Resizes the image if needed (returns true then: <see cref="View"/> is a new object), binds it and clears it.
    /// Rebind the back buffer afterwards.
    /// </summary>
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
