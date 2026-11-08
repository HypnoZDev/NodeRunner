using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Swap chain of an extra window (an interface panel dragged out of the main window), without depth buffer.
public sealed class WindowSwapChain : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _view;

    public int Width { get; private set; }
    public int Height { get; private set; }

    internal WindowSwapChain(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory, IntPtr window, int width, int height)
    {
        _device = device;
        _context = context;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var description = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = GraphicsDevice.ColorFormat,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };
        try
        {
            _swapChain = factory.CreateSwapChainForHwnd(device, window, description);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            description.SwapEffect = SwapEffect.Discard; // Old drivers, remote desktop.
            _swapChain = factory.CreateSwapChainForHwnd(device, window, description);
        }
        factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
        CreateView();
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
            return;
        Width = width;
        Height = height;
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        _view?.Dispose();
        _view = null;
        if (_swapChain.ResizeBuffers(2, (uint)Width, (uint)Height, GraphicsDevice.ColorFormat, SwapChainFlags.None).Failure)
            return;
        CreateView();
    }

    public void Begin(Vector4 clearColor)
    {
        if (_view is null)
            return;
        _context.OMSetRenderTargets(_view);
        _context.RSSetViewport(new Viewport(0, 0, Width, Height));
        _context.ClearRenderTargetView(_view, new Color4(clearColor));
    }

    // Never waits for the screen: the main window already does.
    public void Present() => _swapChain.Present(0, PresentFlags.None);

    private void CreateView()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _view = _device.CreateRenderTargetView(backBuffer);
    }

    public void Dispose()
    {
        _view?.Dispose();
        _swapChain.Dispose();
    }
}
