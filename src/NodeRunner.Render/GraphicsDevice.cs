using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Owns the Direct3D 11 device, the swap chain attached to a window, its back buffer and depth buffer.
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

    public static Format ColorFormat => BackBufferFormat;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public FeatureLevel FeatureLevel { get; }

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

        var (device, context, featureLevel, software) = CreateDevice(flags);
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
        IsSoftware = software;

        using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        {
            AdapterName = adapter.Description.Description;
            // The swap chain must come from the factory of the device's own adapter, which differs after the
            // fallbacks (default adapter, WARP).
            var parent = adapter.GetParent<IDXGIFactory2>();
            _factory.Dispose();
            _factory = parent;
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

        try
        {
            _swapChain = _factory.CreateSwapChainForHwnd(Device, windowHandle, swapChainDesc);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Flip model refused (old driver, remote desktop, some virtual machines): the older presentation model.
            swapChainDesc.SwapEffect = SwapEffect.Discard;
            swapChainDesc.Scaling = Scaling.Stretch;
            _swapChain = _factory.CreateSwapChainForHwnd(Device, windowHandle, swapChainDesc);
        }
        // We handle fullscreen ourselves; disable DXGI's Alt+Enter behaviour.
        _factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        CreateBackBufferView();
        CreateDepthBuffer();
    }

    // True when no graphics card offers Direct3D 11 and Windows draws in software (WARP): works, but slowly.
    public bool IsSoftware { get; }

    // The default adapter is not always usable (laptops with two GPUs, missing driver, remote desktop): every adapter
    // is tried, the high-performance one first, then the software renderer.
    private (ID3D11Device, ID3D11DeviceContext, FeatureLevel, bool) CreateDevice(DeviceCreationFlags flags)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var adapters = new List<IDXGIAdapter1>();
        using (var factory6 = _factory.QueryInterfaceOrNull<IDXGIFactory6>())
        {
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1? adapter = null;
                bool found = factory6 is not null
                    ? factory6.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out adapter).Success
                    : _factory.EnumAdapters1(i, out adapter).Success;
                if (!found || adapter is null)
                    break;
                adapters.Add(adapter);
            }
        }

        Exception? lastError = null;
        try
        {
            foreach (var adapter in adapters)
            {
                if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                    continue;
                var result = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels,
                    out ID3D11Device? device, out FeatureLevel level, out ID3D11DeviceContext? context);
                if (result.Success && device is not null && context is not null)
                    return (device, context, level, false);
                lastError = new InvalidOperationException($"{adapter.Description1.Description} : {result}");
            }

            // Retry without the debug layer, then in software.
            foreach (var driver in new[] { DriverType.Hardware, DriverType.Warp })
            {
                var result = D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.BgraSupport, levels,
                    out ID3D11Device? device, out FeatureLevel level, out ID3D11DeviceContext? context);
                if (result.Success && device is not null && context is not null)
                    return (device, context, level, driver == DriverType.Warp);
            }
        }
        finally
        {
            foreach (var adapter in adapters)
                adapter.Dispose();
        }
        throw new InvalidOperationException(
            "Aucune carte graphique compatible DirectX 11 n'a été trouvée (pilote graphique à installer ou à mettre à jour ?).", lastError);
    }

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

    public void BeginFrame(Vector4 clearColor)
    {
        Context.OMSetRenderTargets(_backBufferView!, _depthView);
        Context.RSSetViewport(new Viewport(0, 0, Width, Height));
        Context.ClearRenderTargetView(_backBufferView!, new Color4(clearColor));
        Context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 0f, 0); // Reversed Z: 0 = far.
    }

    public void CopyBackBuffer(ID3D11Texture2D destination)
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        Context.CopyResource(destination, backBuffer);
    }

    public void BindBackBuffer()
    {
        Context.OMSetRenderTargets(_backBufferView!, _depthView);
        Context.RSSetViewport(new Viewport(0, 0, Width, Height));
    }

    public void SetViewport(int x, int y, int width, int height)
    {
        Context.RSSetViewport(new Viewport(x, y, width, height));
    }

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

    // Copies the depth drawn so far into a texture that shaders can read (one float per pixel of the window, reversed
    // Z), and returns it.
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
