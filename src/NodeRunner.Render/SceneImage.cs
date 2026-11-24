using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// What the scene image does (see SceneImage), from the display settings.
public readonly record struct SceneImageOptions
{
    // Light beyond white (sun, sky, lamps), brought back to the screen by a filmic curve.
    public bool Hdr { get; init; }

    // Share of the view's width and height drawn (0.5 to 1): fewer pixels to light, the image is then stretched.
    public float Scale { get; init; }

    // Brightness of the HDR image (1 = as chosen for the game's light).
    public float Exposure { get; init; }

    // Brightening for the light of the hour (SceneLighting.Adaptation).
    public float Adaptation { get; init; }

    // Sharpening of the stretched or smoothed image (0 to 1).
    public float Sharpness { get; init; }

    // True when the scene needs its own images; otherwise it is drawn straight into the window, as on small PCs.
    public bool IsUsed => Hdr || Scale < 0.999f;
}

// The 3D scene drawn into images of its own instead of straight into the window: light beyond white (HDR) brought back
// to the screen by a filmic curve, fewer pixels than the window if asked. The finished image is drawn into the
// window's 3D view with its depth, so the editor's lines drawn afterwards are still hidden behind the models.
public sealed unsafe class SceneImage : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4 Source;    // xy: size of the scene image (pixels), zw: 1 / size.
    float4 Settings;  // x: exposure, y: 1 = HDR (tone mapping), z: sharpening, w: saturation.
};
Texture2D SceneColor : register(t0);
Texture2D<float> SceneDepth : register(t1);
SamplerState LinearClamp : register(s0);

struct VS_OUTPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

// One triangle covering the viewport, uv 0 to 1 from its top-left corner.
VS_OUTPUT VSFull(uint id : SV_VertexID)
{
    VS_OUTPUT output;
    float2 p = float2((id << 1) & 2, id & 2);
    output.pos = float4(p * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    output.uv = p;
    return output;
}

// Filmic curve (fit of the ACES reference rendering): bright colours roll off towards white instead of being cut.
float3 ToneMap(float3 c)
{
    static const float3x3 toAces = { 0.59719f, 0.35458f, 0.04823f, 0.07600f, 0.90834f, 0.01566f, 0.02840f, 0.13383f, 0.83777f };
    static const float3x3 fromAces = { 1.60475f, -0.53108f, -0.07367f, -0.10208f, 1.10813f, -0.00605f, -0.00327f, -0.07276f, 1.07602f };
    c = mul(toAces, c);
    float3 a = c * (c + 0.0245786f) - 0.000090537f;
    float3 b = c * (0.983729f * c + 0.4329510f) + 0.238081f;
    return saturate(mul(fromAces, a / b));
}

float3 ToDisplay(float3 c)
{
    return c <= 0.0031308f ? c * 12.92f : 1.055f * pow(abs(c), 1.0f / 2.4f) - 0.055f;
}

float3 Shown(float2 uv)
{
    float3 c = max(SceneColor.SampleLevel(LinearClamp, uv, 0).rgb, 0.0f);
    if (Settings.y < 0.5f)
        return c;
    c = ToneMap(c * Settings.x);
    c = max(lerp(dot(c, float3(0.2126f, 0.7152f, 0.0722f)), c, Settings.w), 0.0f);
    return ToDisplay(c);
}

struct PS_COMPOSITE { float4 color : SV_Target; float depth : SV_Depth; };

PS_COMPOSITE PSComposite(VS_OUTPUT input)
{
    float2 uv = input.uv;
    float3 c = Shown(uv);
    if (Settings.z > 0.0f)
    {
        // Sharpening: the difference with the four neighbours added back, limited near edges already contrasted.
        float3 n = Shown(uv + float2(0.0f, -Source.w)), s = Shown(uv + float2(0.0f, Source.w));
        float3 e = Shown(uv + float2(Source.z, 0.0f)), w = Shown(uv + float2(-Source.z, 0.0f));
        float3 low = min(c, min(min(n, s), min(e, w))), high = max(c, max(max(n, s), max(e, w)));
        float contrast = saturate(dot(high - low, 0.333f) * 4.0f);
        c = saturate(c + (4.0f * c - n - s - e - w) * (Settings.z * 0.3f * (1.0f - 0.7f * contrast)));
    }
    // A little noise against the bands of the smooth gradients (sky) once in 8 bits.
    float noise = frac(52.9829189f * frac(dot(input.pos.xy, float2(0.06711056f, 0.00583715f))));
    PS_COMPOSITE output;
    output.color = float4(c + (noise - 0.5f) / 255.0f, 1.0f);
    output.depth = SceneDepth.Load(int3(min(uv * Source.xy, Source.xy - 1.0f), 0));
    return output;
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Source;
        public Vector4 Settings;
    }

    private const Format ColorFormat = Format.R16G16B16A16_Float;
    private const Format NormalFormat = Format.R8G8B8A8_UNorm;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _fullVertexShader;
    private readonly ID3D11PixelShader _compositePixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11DepthStencilState _writeDepth;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11BlendState _opaque;

    private Texture? _color;
    private Texture? _normals;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11ShaderResourceView? _depthRead;
    private Texture? _depthCopy;
    private Texture? _colorCopy;

    public SceneImage(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _fullVertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VSFull", "scene", "vs_5_0").Span);
        _compositePixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSComposite", "scene", "ps_5_0").Span);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _linearClamp = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
        _writeDepth = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Always,
        });
        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = false,
        });
        _opaque = device.CreateBlendState(BlendDescription.Opaque);
    }

    // Size of the images, in pixels.
    public int Width { get; private set; }
    public int Height { get; private set; }

    public SceneImageOptions Options { get; private set; }

    // A texture with the views it is used through.
    private sealed class Texture(ID3D11Texture2D texture, ID3D11ShaderResourceView read, ID3D11RenderTargetView? write) : IDisposable
    {
        public ID3D11Texture2D Resource { get; } = texture;
        public ID3D11ShaderResourceView Read { get; } = read;
        public ID3D11RenderTargetView? Write { get; } = write;

        public void Dispose()
        {
            Write?.Dispose();
            Read.Dispose();
            Resource.Dispose();
        }
    }

    private Texture CreateTexture(int width, int height, Format format, bool target)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = target ? BindFlags.RenderTarget | BindFlags.ShaderResource : BindFlags.ShaderResource,
        });
        return new Texture(texture, _device.CreateShaderResourceView(texture), target ? _device.CreateRenderTargetView(texture) : null);
    }

    // Starts the frame's scene for a 3D view of viewWidth x viewHeight pixels: the images (resized if needed) are
    // cleared and bound.
    public void Begin(SceneImageOptions options, int viewWidth, int viewHeight, Vector4 clearColor)
    {
        Options = options;
        float scale = Math.Clamp(options.Scale, 0.25f, 1f);
        int width = Math.Max(1, (int)MathF.Round(viewWidth * scale));
        int height = Math.Max(1, (int)MathF.Round(viewHeight * scale));
        if (width != Width || height != Height || _color is null)
            Create(width, height);

        _context.ClearRenderTargetView(_color!.Write!, new Color4(clearColor));
        _context.ClearRenderTargetView(_normals!.Write!, new Color4(0f, 0f, 0f, 0f));
        _context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 0f, 0); // Reversed Z: 0 = far.
        Bind(normals: false);
    }

    // Binds the colour image (and the normals' image for the solid passes) with the depth, over the whole image.
    public void Bind(bool normals)
    {
        if (normals)
            _context.OMSetRenderTargets([_color!.Write!, _normals!.Write!], _depthView);
        else
            _context.OMSetRenderTargets(_color!.Write!, _depthView);
        _context.RSSetViewport(new Viewport(0, 0, Width, Height));
    }

    // Copy of the depth drawn so far (one float per pixel, reversed Z), for the water.
    public ID3D11ShaderResourceView CopyDepth()
    {
        _depthCopy ??= CreateTexture(Width, Height, Format.R32_Float, target: false);
        _context.CopyResource(_depthCopy.Resource, _depth!);
        return _depthCopy.Read;
    }

    // Copy of the picture drawn so far, for the water's reflections.
    public ID3D11ShaderResourceView CopyColor()
    {
        _colorCopy ??= CreateTexture(Width, Height, ColorFormat, target: false);
        _context.CopyResource(_colorCopy.Resource, _color!.Resource);
        return _colorCopy.Read;
    }

    // Draws the finished image into the render target and viewport bound by the caller (the window's 3D view), with
    // its depth.
    public void Finish()
    {
        var options = Options;
        WriteConstants(new Constants
        {
            Source = new Vector4(Width, Height, 1f / Width, 1f / Height),
            Settings = new Vector4(MathF.Max(options.Exposure, 0.01f) * MathF.Max(options.Adaptation, 1f) * Exposure, options.Hdr ? 1f : 0f,
                Math.Clamp(options.Sharpness, 0f, 1f), Saturation),
        });
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullVertexShader);
        _context.GSSetShader(null);
        _context.PSSetShader(_compositePixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetShaderResource(0, _color!.Read);
        _context.PSSetShaderResource(1, _depthRead!);
        _context.OMSetBlendState(_opaque, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_writeDepth, 0);
        _context.RSSetState(_rasterizerState);
        _context.Draw(3, 0);
        _context.PSUnsetShaderResource(0);
        _context.PSUnsetShaderResource(1);
    }

    // Brightness of the HDR image for an exposure of 1.
    private const float Exposure = 1.2f;
    private const float Saturation = 1.12f;

    private void WriteConstants(Constants constants)
    {
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);
    }

    private void Create(int width, int height)
    {
        DisposeImages();
        Width = width;
        Height = height;
        _color = CreateTexture(width, height, ColorFormat, target: true);
        _normals = CreateTexture(width, height, NormalFormat, target: true);
        _depth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        _depthView = _device.CreateDepthStencilView(_depth, new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, Format.D32_Float));
        _depthRead = _device.CreateShaderResourceView(_depth, new ShaderResourceViewDescription(_depth, ShaderResourceViewDimension.Texture2D, Format.R32_Float));
    }

    private void DisposeImages()
    {
        _color?.Dispose();
        _color = null;
        _normals?.Dispose();
        _normals = null;
        _depthCopy?.Dispose();
        _depthCopy = null;
        _colorCopy?.Dispose();
        _colorCopy = null;
        _depthRead?.Dispose();
        _depthRead = null;
        _depthView?.Dispose();
        _depthView = null;
        _depth?.Dispose();
        _depth = null;
        Width = Height = 0;
    }

    public void Dispose()
    {
        DisposeImages();
        _opaque.Dispose();
        _rasterizerState.Dispose();
        _writeDepth.Dispose();
        _linearClamp.Dispose();
        _constantBuffer.Dispose();
        _compositePixelShader.Dispose();
        _fullVertexShader.Dispose();
    }
}
