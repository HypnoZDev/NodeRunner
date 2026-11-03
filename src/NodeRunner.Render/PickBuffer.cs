using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

/// <summary>
/// Off-screen image in which each object is drawn with its number instead of its colour (0 = nothing),
/// to find the object under the mouse exactly as it is seen: through the holes of foliage, never behind a wall.
/// Only used when the user clicks: <see cref="Begin"/>, draw the objects
/// (<see cref="ModelRenderer.BeginPick"/>), then <see cref="Read"/> the number under the mouse.
/// </summary>
public sealed unsafe class PickBuffer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _staging;
    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11Texture2D? _depthStaging;
    private int _width;
    private int _height;

    public PickBuffer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        // One pixel the CPU can read.
        _staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    /// <summary>Binds the number image (same size as the view) and clears it. Rebind the back buffer afterwards.</summary>
    public void Begin(int width, int height)
    {
        if (width != _width || height != _height || _target is null)
            Create(Math.Max(1, width), Math.Max(1, height));

        _context.OMSetRenderTargets(_targetView!, _depthView);
        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.ClearRenderTargetView(_targetView!, new Color4(0f, 0f, 0f, 0f));
        _context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 0f, 0); // Reversed Z: 0 = far.
    }

    /// <summary>The number drawn at pixel (<paramref name="x"/>, <paramref name="y"/>), 0 if none. Waits for the GPU.</summary>
    public uint Read(int x, int y)
    {
        if (_target is null || x < 0 || y < 0 || x >= _width || y >= _height)
            return 0;

        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _target, 0, new Box(x, y, 0, x + 1, y + 1, 1));
        var map = _context.Map(_staging, 0, MapMode.Read);
        uint value = *(uint*)map.DataPointer;
        _context.Unmap(_staging, 0);
        return value;
    }

    /// <summary>
    /// Depth drawn at pixel (<paramref name="x"/>, <paramref name="y"/>) by the last pick drawing (reversed Z: 0 = nothing,
    /// up to 1 = nearest), to find the 3D point under a pixel. Waits for the GPU; copies the whole depth image
    /// (depth images can only be copied whole), so only call it on demand.
    /// </summary>
    public float ReadDepth(int x, int y)
    {
        if (_depth is null || x < 0 || y < 0 || x >= _width || y >= _height)
            return 0f;
        _depthStaging ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _context.CopyResource(_depthStaging, _depth);
        var map = _context.Map(_depthStaging, 0, MapMode.Read);
        float value = *(float*)((byte*)map.DataPointer + (long)y * map.RowPitch + x * 4);
        _context.Unmap(_depthStaging, 0);
        return value;
    }

    private void Create(int width, int height)
    {
        DisposeTargets();
        _width = width;
        _height = height;

        _target = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _targetView = _device.CreateRenderTargetView(_target);

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
        _targetView?.Dispose();
        _target?.Dispose();
        _depthView?.Dispose();
        _depth?.Dispose();
        _depthStaging?.Dispose();
        _depthStaging = null;
        _targetView = null;
        _target = null;
        _depthView = null;
        _depth = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        _staging.Dispose();
    }
}
