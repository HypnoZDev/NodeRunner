using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// The sky behind the scene: a gradient from the horizon colour to the zenith colour (darker below the horizon), with
// the sun disc and its glow; or, with SceneLighting.PhysicalSky, the sky worked out from the air (Atmosphere), kept in a
// small table of directions around the sun, made again when the sun moves.
public sealed unsafe class SkyRenderer : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 InverseViewProjection;
    float4 Zenith;   // w: sky turned grey by the weather (sky from the table).
    float4 Horizon;
    float4 Sun; // xyz: direction towards the sun, w: visibility of the disc.
    float4 Disc; // x: brightness of the sun disc (much brighter in an HDR image, where it glows), y = 1: HDR image
                 // (linear colours), z = 1: sky from the table, w: share of the night colours over it.
    float4 Tint; // Colour of the sun through the air (sky from the table); making the table: worked-out sky -> image.
    float4 TrueSun; // Direction of the sun, even below the horizon (sky from the table); w = 1: clouds over the sky.
};
// Lowest height of the worked-out sky (radians): below, the same colour down to the horizon (lower, the light crosses
// hundreds of kilometres of air and turns brown).
static const float LowestSky = 0.07f;
Texture2D Table : register(t0);
Texture2D Clouds : register(t1); // CloudRenderer: rgb their light, a how much of the sky shows through.
SamplerState LinearClamp : register(s0);
struct PS_INPUT { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

PS_INPUT VS(uint id : SV_VertexID)
{
    PS_INPUT output;
    float2 p = float2((id << 1) & 2, id & 2) * 2.0f - 1.0f; // (-1,-1), (3,-1), (-1,3): covers the viewport.
    output.pos = float4(p, 0.0f, 1.0f);
    output.ndc = p;
    return output;
}

float3 WithClouds(float3 color, float2 ndc)
{
    if (TrueSun.w < 0.5f)
        return color;
    // Four taps around the pixel: the cloud image is smaller, its steps are smoothed out.
    float width, height;
    Clouds.GetDimensions(width, height);
    float2 uv = ndc * float2(0.5f, -0.5f) + 0.5f, texel = 0.5f / float2(width, height);
    float4 clouds = 0.25f * (Clouds.SampleLevel(LinearClamp, uv + float2(-texel.x, -texel.y), 0)
        + Clouds.SampleLevel(LinearClamp, uv + float2(texel.x, -texel.y), 0)
        + Clouds.SampleLevel(LinearClamp, uv + float2(-texel.x, texel.y), 0)
        + Clouds.SampleLevel(LinearClamp, uv + float2(texel.x, texel.y), 0));
    return color * clouds.a + clouds.rgb;
}

float4 PS(PS_INPUT input) : SV_Target
{
    // View ray through this pixel (reversed Z: 1 = near plane).
    float4 a = mul(InverseViewProjection, float4(input.ndc, 1.0f, 1.0f));
    float4 b = mul(InverseViewProjection, float4(input.ndc, 0.5f, 1.0f));
    float3 ray = normalize(b.xyz / b.w - a.xyz / a.w);

    float up = ray.z;
    // Linear colours are blended as display colours, for the same gradient.
    float3 horizon = Horizon.rgb, zenith = Zenith.rgb;
    if (Disc.y > 0.5f)
    {
        horizon = pow(abs(horizon), 1.0f / 2.2f);
        zenith = pow(abs(zenith), 1.0f / 2.2f);
    }
    float3 color = up >= 0.0f
        ? lerp(horizon, zenith, pow(saturate(up), 0.45f))
        : horizon * lerp(0.85f, 0.55f, saturate(-up * 4.0f));
    if (Disc.y > 0.5f)
        color = pow(color, 2.2f);

    float facing = saturate(dot(ray, Sun.xyz));
    if (Disc.z > 0.5f)
    {
        // Table: across, the angle around from the sun (0 to 180 degrees); down, the height above the horizon
        // (square root: finer near the horizon).
        float2 flat = ray.xy, sunFlat = TrueSun.xy;
        float around = dot(flat, flat) > 1e-8f && dot(sunFlat, sunFlat) > 1e-8f
            ? acos(clamp(dot(normalize(flat), normalize(sunFlat)), -1.0f, 1.0f)) / 3.14159265f : 0.0f;
        float height = sqrt(saturate((asin(saturate(up)) - LowestSky) / (1.5707963f - LowestSky)));
        float3 sky = Table.SampleLevel(LinearClamp, float2(around, height), 0).rgb;
        if (up < 0.0f)
            sky *= lerp(0.85f, 0.55f, saturate(-up * 4.0f));
        sky = lerp(sky, dot(sky, float3(0.2126f, 0.7152f, 0.0722f)), Zenith.w);
        color = lerp(sky, color, Disc.w);
        color += Sun.w * pow(facing, 2000.0f) * Disc.x * Tint.rgb;
        return float4(WithClouds(color, input.ndc), 1.0f);
    }
    color += Sun.w * (pow(facing, 2000.0f) * Disc.x * float3(1.0f, 0.95f, 0.85f)
        + pow(facing, 24.0f) * 0.25f * Horizon.rgb);
    return float4(WithClouds(color, input.ndc), 1.0f);
}

// One texel of the table: the sky's light in that direction, the sun at its height straight across (around = 0).
float4 PSTable(PS_INPUT input) : SV_Target
{
    float2 uv = input.ndc * float2(0.5f, -0.5f) + 0.5f;
    float around = uv.x * 3.14159265f;
    float height = LowestSky + uv.y * uv.y * (1.5707963f - LowestSky);
    float3 d = float3(cos(around) * cos(height), sin(around) * cos(height), sin(height));
    float sunHeight = asin(clamp(TrueSun.z, -1.0f, 1.0f));
    float3 sun = float3(cos(sunHeight), 0.0f, sin(sunHeight));
    return float4(SkyRadiance(d, sun) * Tint.rgb, 1.0f);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 InverseViewProjection;
        public Vector4 Zenith;
        public Vector4 Horizon;
        public Vector4 Sun;
        public Vector4 Disc;
        public Vector4 Tint;
        public Vector4 TrueSun;
    }

    private const int TableWidth = 64, TableHeight = 96;

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11PixelShader _tablePixelShader;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11Texture2D _table;
    private readonly ID3D11ShaderResourceView _tableRead;
    private readonly ID3D11RenderTargetView _tableWrite;
    private float _tableSunHeight = float.NaN;

    public SkyRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        string source = Atmosphere.Source + ShaderSource;
        _vertexShader = device.CreateVertexShader(Compiler.Compile(source, "VS", "sky", "vs_5_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(source, "PS", "sky", "ps_5_0").Span);
        _tablePixelShader = device.CreatePixelShader(Compiler.Compile(source, "PSTable", "sky", "ps_5_0").Span);
        _linearClamp = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
        _table = device.CreateTexture2D(new Texture2DDescription
        {
            Width = TableWidth,
            Height = TableHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.R16G16B16A16_Float,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _tableRead = device.CreateShaderResourceView(_table);
        _tableWrite = device.CreateRenderTargetView(_table);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _depthState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
        });
        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = false,
        });
        _blendState = device.CreateBlendState(BlendDescription.Opaque);
    }

    // Makes the table of the worked-out sky again if the sun moved (binds its own image: call it before the scene's
    // image is bound).
    public void Prepare(SceneLighting lighting)
    {
        if (!lighting.PhysicalSky)
            return;
        float sunHeight = MathF.Asin(Math.Clamp(lighting.Sun.Z, -1f, 1f));
        if (MathF.Abs(sunHeight - _tableSunHeight) < 0.002f)
            return;
        _tableSunHeight = sunHeight;
        Write(new Constants
        {
            Tint = new Vector4(SceneLighting.SkyBalance, 1f),
            TrueSun = new Vector4(lighting.Sun, 0f),
        });
        _context.PSUnsetShaderResource(0);
        _context.OMSetRenderTargets(_tableWrite);
        _context.RSSetViewport(new Viewport(0, 0, TableWidth, TableHeight));
        SetStates(_tablePixelShader);
        _context.Draw(3, 0);
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
    }

    // clouds: CloudRenderer.Render's image, drawn over the sky.
    public void Draw(Matrix4x4 viewProjection, SceneLighting lighting, ID3D11ShaderResourceView? clouds = null)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return;
        Write(new Constants
        {
            // HLSL reads the matrix as it is stored: the same convention as the model shader's mul(Matrix, vector).
            InverseViewProjection = inverse,
            Zenith = new Vector4(lighting.Zenith, lighting.Grey),
            Horizon = new Vector4(lighting.Horizon, 1f),
            Sun = new Vector4(lighting.SunDirection, lighting.SunDisc),
            Disc = new Vector4(lighting.IsHdr ? 60f : 6f, lighting.IsHdr ? 1f : 0f, lighting.PhysicalSky ? 1f : 0f, lighting.NightLights),
            Tint = new Vector4(lighting.SunTint, 0f),
            TrueSun = new Vector4(lighting.Sun, clouds is null ? 0f : 1f),
        });
        SetStates(_pixelShader);
        if (lighting.PhysicalSky)
            _context.PSSetShaderResource(0, _tableRead);
        if (clouds is not null)
            _context.PSSetShaderResource(1, clouds);
        _context.Draw(3, 0);
        _context.PSUnsetShaderResource(0);
        _context.PSUnsetShaderResource(1);
    }

    private void Write(Constants constants)
    {
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);
    }

    private void SetStates(ID3D11PixelShader pixelShader)
    {
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(0, _linearClamp);
        _context.GSSetShader(null);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.RSSetState(_rasterizerState);
    }

    public void Dispose()
    {
        _tableWrite.Dispose();
        _tableRead.Dispose();
        _table.Dispose();
        _linearClamp.Dispose();
        _tablePixelShader.Dispose();
        _blendState.Dispose();
        _rasterizerState.Dispose();
        _depthState.Dispose();
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
