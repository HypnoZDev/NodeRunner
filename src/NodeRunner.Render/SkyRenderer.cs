using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// The sky behind the scene: a gradient from the horizon colour to the zenith colour (darker below the horizon), with
// the sun disc and its glow.
public sealed unsafe class SkyRenderer : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 InverseViewProjection;
    float4 Zenith;
    float4 Horizon;
    float4 Sun; // xyz: direction towards the sun, w: visibility of the disc.
};
struct PS_INPUT { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

PS_INPUT VS(uint id : SV_VertexID)
{
    PS_INPUT output;
    float2 p = float2((id << 1) & 2, id & 2) * 2.0f - 1.0f; // (-1,-1), (3,-1), (-1,3): covers the viewport.
    output.pos = float4(p, 0.0f, 1.0f);
    output.ndc = p;
    return output;
}

float4 PS(PS_INPUT input) : SV_Target
{
    // View ray through this pixel (reversed Z: 1 = near plane).
    float4 a = mul(InverseViewProjection, float4(input.ndc, 1.0f, 1.0f));
    float4 b = mul(InverseViewProjection, float4(input.ndc, 0.5f, 1.0f));
    float3 ray = normalize(b.xyz / b.w - a.xyz / a.w);

    float up = ray.z;
    float3 color = up >= 0.0f
        ? lerp(Horizon.rgb, Zenith.rgb, pow(saturate(up), 0.45f))
        : Horizon.rgb * lerp(0.85f, 0.55f, saturate(-up * 4.0f));

    float facing = saturate(dot(ray, Sun.xyz));
    color += Sun.w * (pow(facing, 2000.0f) * 6.0f * float3(1.0f, 0.95f, 0.85f)
        + pow(facing, 24.0f) * 0.25f * Horizon.rgb);
    return float4(color, 1.0f);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 InverseViewProjection;
        public Vector4 Zenith;
        public Vector4 Horizon;
        public Vector4 Sun;
    }

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11BlendState _blendState;

    public SkyRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        _vertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VS", "sky", "vs_4_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "sky", "ps_4_0").Span);
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

    public void Draw(Matrix4x4 viewProjection, SceneLighting lighting)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return;
        var constants = new Constants
        {
            // HLSL reads the matrix as it is stored: the same convention as the model shader's mul(Matrix, vector).
            InverseViewProjection = inverse,
            Zenith = new Vector4(lighting.Zenith, 1f),
            Horizon = new Vector4(lighting.Horizon, 1f),
            Sun = new Vector4(lighting.SunDirection, lighting.SunDisc),
        };
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.GSSetShader(null);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.RSSetState(_rasterizerState);
        _context.Draw(3, 0);
    }

    public void Dispose()
    {
        _blendState.Dispose();
        _rasterizerState.Dispose();
        _depthState.Dispose();
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
