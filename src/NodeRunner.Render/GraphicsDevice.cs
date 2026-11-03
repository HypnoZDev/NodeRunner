using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

/// <summary>
/// Owns the Direct3D 11 device, the swap chain attached to a window, its back buffer and depth buffer.
/// </summary>
/// <remarks>
/// The depth buffer uses "reversed Z": 1 = near, 0 = far, cleared to 0, compared with Greater.
/// With a 32-bit float depth buffer this keeps good precision across the whole map (several km).
/// </remarks>
public sealed class GraphicsDevice : IDisposable
{
    private const Format BackBufferFormat = Format.B8G8R8A8_UNorm;
    private const int BackBufferCount = 2;
    private const Format DepthFormat = Format.D32_Float;
    // Typeless storage, so the depth can be copied into a texture that shaders read (see CopyDepth).
    private const Format DepthStorageFormat = Format.R32_Typeless;

    private readonly IDXGIFactory2 _factory;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _backBufferView;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11Texture2D? _depthCopy;
    private ID3D11ShaderResourceView? _depthCopyView;

    /// <summary>Pixel format of the frame, for textures that receive a copy of it.</summary>
    public static Format ColorFormat => BackBufferFormat;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Feature level actually obtained (11_0 or better).</summary>
    public FeatureLevel FeatureLevel { get; }

    /// <summary>Name of the graphics card, for the status panel.</summary>
    public string AdapterName { get; }

    public GraphicsDevice(IntPtr windowHandle, int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        // The debug layer is only present when the Windows "Graphics Tools" optional feature is installed.
        if (D3D11.SdkLayersAvailable())
            flags |= DeviceCreationFlags.Debug;
#endif

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            levels,
            out ID3D11Device device,
            out FeatureLevel featureLevel,
            out ID3D11DeviceContext context).CheckError();

        Device = device;
        Context = context;
        FeatureLevel = featureLevel;

        using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        {
            AdapterName = adapter.Description.Description;
        }

        var swapChainDesc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = BackBufferFormat,
            BufferCount = BackBufferCount,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };

        _swapChain = _factory.CreateSwapChainForHwnd(Device, windowHandle, swapChainDesc);
        // We handle fullscreen ourselves; disable DXGI's Alt+Enter behaviour.
        _factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        CreateBackBufferView();
        CreateDepthBuffer();
    }

    /// <summary>Call when the window size changes.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
            return;

        Width = width;
        Height = height;

        Context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        _backBufferView?.Dispose();
        _backBufferView = null;
        DisposeDepthBuffer();

        _swapChain.ResizeBuffers(BackBufferCount, (uint)Width, (uint)Height, BackBufferFormat, SwapChainFlags.None).CheckError();
        CreateBackBufferView();
        CreateDepthBuffer();
    }

    /// <summary>Binds the back buffer and depth buffer, and clears them.</summary>
    public void BeginFrame(Vector4 clearColor)
    {
        Context.OMSetRenderTargets(_backBufferView!, _depthView);
        Context.RSSetViewport(new Viewport(0, 0, Width, Height));
        Context.ClearRenderTargetView(_backBufferView!, new Color4(clearColor));
        Context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 0f, 0); // Reversed Z: 0 = far.
    }

    /// <summary>Copies the frame drawn so far into <paramref name="destination"/> (same size and <see cref="ColorFormat"/>).</summary>
    public void CopyBackBuffer(ID3D11Texture2D destination)
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        Context.CopyResource(destination, backBuffer);
    }

    /// <summary>Draws into the frame again (whole window) after drawing into another texture.</summary>
    public void BindBackBuffer()
    {
        Context.OMSetRenderTargets(_backBufferView!, _depthView);
        Context.RSSetViewport(new Viewport(0, 0, Width, Height));
    }

    /// <summary>Restricts the following drawing to a part of the window (the 3D view between the interface panels).</summary>
    public void SetViewport(int x, int y, int width, int height)
    {
        Context.RSSetViewport(new Viewport(x, y, width, height));
    }

    /// <summary>Shows the frame on screen. VSync on by default.</summary>
    public void Present(bool vsync = true)
    {
        _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);
    }

    private void CreateBackBufferView()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = Device.CreateRenderTargetView(backBuffer);
    }

    private void CreateDepthBuffer()
    {
        _depthTexture = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DepthStorageFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _depthView = Device.CreateDepthStencilView(_depthTexture,
            new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, DepthFormat));
    }

    /// <summary>
    /// Copies the depth drawn so far into a texture that shaders can read (one float per pixel of the window, reversed Z),
    /// and returns it. The texture is kept and written again at the next call.
    /// </summary>
    public ID3D11ShaderResourceView CopyDepth()
    {
        if (_depthCopy is null)
        {
            _depthCopy = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DepthStorageFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            _depthCopyView = Device.CreateShaderResourceView(_depthCopy,
                new ShaderResourceViewDescription(_depthCopy, ShaderResourceViewDimension.Texture2D, Format.R32_Float));
        }
        Context.CopyResource(_depthCopy, _depthTexture!);
        return _depthCopyView!;
    }

    private void DisposeDepthBuffer()
    {
        _depthCopyView?.Dispose();
        _depthCopyView = null;
        _depthCopy?.Dispose();
        _depthCopy = null;
        _depthView?.Dispose();
        _depthView = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        _backBufferView?.Dispose();
        DisposeDepthBuffer();
        _swapChain.Dispose();
        Context.Dispose();
        Device.Dispose();
        _factory.Dispose();
    }
}
