using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// A light of the map at night (street lamp, lit window...): where it is, its colour times its brightness (linear), how
// far it reaches (m), how bright its glow seen from afar is, and for a spot light, where it points and the cosines of
// its cone (CosOuter below -1: lights all around).
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NightLight(Vector3 Position, float Reach, Vector3 Color, float Corona, Vector3 Direction, float CosOuter,
    float CosInner, float Spare1 = 0f, float Spare2 = 0f, float Spare3 = 0f);

// The map's lights at night in the HDR scene image (SceneImage): their light on the solid surfaces around the camera
// (from the depth, the normals and the colours of the solid pass, one patch of light per lamp, so thousands cost
// little), and the glow of every lamp seen from afar, up to the fog.
public sealed unsafe class NightLights : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 ViewProjection; // Camera-relative point -> image (with the frame's shift).
    float4x4 Unproject;      // Image (x, y, depth) -> camera-relative point.
    float4 Camera;           // xyz: camera position, w: distance up to which the lamps light the surfaces (m).
    float4 Right;            // xyz: camera's right, w: brightness of the light on the surfaces.
    float4 Up;               // xyz: camera's up, w: brightness of the glows.
    float4 Size;             // xy: image size, zw: 1 / size.
    float4 Fog;              // x: distance where the fog hides everything (0 = no fog).
};
struct Light
{
    float3 Position; float Reach; float3 Color; float Corona; float3 Direction; float CosOuter; float CosInner; float3 Spare;
};
StructuredBuffer<Light> Lights : register(t0);
Texture2D<float> Depth : register(t1);
Texture2D Normals : register(t2);
Texture2D Albedo : register(t3);

static const float4 Hidden = float4(-4.0f, -4.0f, 0.5f, 1.0f);

float2 Corner(uint vertex)
{
    return float2((vertex & 1u) != 0u ? 1.0f : -1.0f, (vertex & 2u) != 0u ? 1.0f : -1.0f);
}

struct LIGHT_OUT { float4 pos : SV_POSITION; nointerpolation uint id : LIGHT; nointerpolation float fade : FADE; };

// A square around the lamp's reach, facing the camera; the whole image when the camera is inside it.
LIGHT_OUT VSLight(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Light light = Lights[instance];
    LIGHT_OUT output;
    output.id = instance;
    float3 relative = light.Position - Camera.xyz;
    float distance = length(relative);
    output.fade = saturate((Camera.w - (distance - light.Reach)) / (0.2f * Camera.w));
    float2 corner = Corner(vertex);
    if (output.fade <= 0.0f || light.Reach <= 0.0f)
        output.pos = Hidden;
    else if (distance < light.Reach * 1.5f + 2.0f)
        output.pos = float4(corner, 0.5f, 1.0f);
    else
        output.pos = mul(ViewProjection, float4(relative + (Right.xyz * corner.x + Up.xyz * corner.y) * (light.Reach * 1.5f), 1.0f));
    return output;
}

float4 PSLight(LIGHT_OUT input) : SV_Target
{
    int3 p = int3(input.pos.xy, 0);
    float depth = Depth.Load(p);
    float4 normal = Normals.Load(p);
    if (depth <= 0.0f || dot(normal.xyz, 1.0f) <= 0.0f)
        discard; // Sky, or not a solid surface.
    float2 ndc = input.pos.xy * Size.zw * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f);
    float4 point4 = mul(Unproject, float4(ndc, depth, 1.0f));
    Light light = Lights[input.id];
    float3 toLight = (light.Position - Camera.xyz) - point4.xyz / point4.w;
    float d2 = dot(toLight, toLight);
    float r2 = light.Reach * light.Reach;
    if (d2 >= r2)
        discard;
    float d = sqrt(d2);
    float3 l = toLight / max(d, 0.0001f);
    float3 n = normalize(normal.xyz * 2.0f - 1.0f);
    float facing = saturate(dot(n, l));
    float window = 1.0f - d2 / r2;
    // Window to 0 at the reach, brighter close to the lamp.
    float strength = window * window * 3.0f / (1.0f + d2 * 0.02f);
    if (light.CosOuter > -1.5f)
        strength *= smoothstep(light.CosOuter, light.CosInner, dot(-l, light.Direction));
    float3 albedo = Albedo.Load(p).rgb;
    return float4(albedo * light.Color * (strength * facing * Right.w * input.fade), 0.0f);
}

struct CORONA_OUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; nointerpolation float3 color : COLOR0; };

// A soft dot on each lamp, bigger far away so it stays a few pixels wide, a little in front of the lamp (not hidden by
// its own glass).
CORONA_OUT VSCorona(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Light light = Lights[instance];
    CORONA_OUT output;
    float3 relative = light.Position - Camera.xyz;
    float distance = length(relative);
    float2 corner = Corner(vertex);
    output.uv = corner;
    float fog = Fog.x > 0.0f ? saturate(distance / Fog.x) : 0.0f;
    output.color = light.Color * (light.Corona * Up.w * (1.0f - fog * fog));
    if (light.Corona <= 0.0f || fog >= 1.0f || distance < 0.5f)
    {
        output.pos = Hidden;
        return output;
    }
    float size = 0.22f + distance * 0.0022f;
    float3 center = relative - relative / distance * min(1.2f, distance * 0.3f);
    output.pos = mul(ViewProjection, float4(center + (Right.xyz * corner.x + Up.xyz * corner.y) * size, 1.0f));
    return output;
}

float4 PSCorona(CORONA_OUT input) : SV_Target
{
    float r2 = dot(input.uv, input.uv);
    if (r2 >= 1.0f)
        discard;
    float glow = exp(-r2 * 7.0f) + 0.35f * exp(-r2 * 30.0f);
    return float4(input.color * glow, 0.0f);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 Unproject;
        public Vector4 Camera;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Size;
        public Vector4 Fog;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _lightVertexShader;
    private readonly ID3D11PixelShader _lightPixelShader;
    private readonly ID3D11VertexShader _coronaVertexShader;
    private readonly ID3D11PixelShader _coronaPixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11BlendState _add;
    private readonly ID3D11DepthStencilState _noDepth;
    private readonly ID3D11DepthStencilState _testDepth;
    private readonly ID3D11RasterizerState _rasterizerState;
    private ID3D11Buffer? _lights;
    private ID3D11ShaderResourceView? _lightsView;
    private int _count;

    public NightLights(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _lightVertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VSLight", "nightlights", "vs_5_0").Span);
        _lightPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSLight", "nightlights", "ps_5_0").Span);
        _coronaVertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VSCorona", "nightlights", "vs_5_0").Span);
        _coronaPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSCorona", "nightlights", "ps_5_0").Span);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        var add = new BlendDescription();
        add.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.One,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _add = device.CreateBlendState(add);
        _noDepth = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
        });
        // Reversed Z: hidden behind what is in front, without hiding anything.
        _testDepth = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual,
        });
        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });
    }

    public int Count => _count;

    // Replaces the lights (a few tens of thousands at most: the whole map's).
    public void SetLights(ReadOnlySpan<NightLight> lights)
    {
        _lightsView?.Dispose();
        _lights?.Dispose();
        _lightsView = null;
        _lights = null;
        _count = lights.Length;
        if (lights.Length == 0)
            return;
        fixed (NightLight* data = lights)
        {
            var description = new BufferDescription((uint)(lights.Length * sizeof(NightLight)), BindFlags.ShaderResource, ResourceUsage.Immutable,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, (uint)sizeof(NightLight));
            _lights = _device.CreateBuffer(description, new SubresourceData(data));
        }
        _lightsView = _device.CreateShaderResourceView(_lights, new ShaderResourceViewDescription(_lights, Format.Unknown, 0, (uint)lights.Length));
    }

    // Distance up to which the lamps light the surfaces (m).
    public const float LitDistance = 300f;

    // Adds the lamps' light to the solid surfaces drawn so far into the image (its normals and colours given by the solid
    // pass, see SceneImage.Bind), then binds its colour image alone again. viewProjection: the one the scene is drawn
    // with; strength: 0 (none) to 1.
    public void LightSurfaces(SceneImage image, Camera camera, Matrix4x4 viewProjection, float strength)
    {
        if (_lightsView is null || strength <= 0f || image.NormalsRead is null)
        {
            image.Bind(normals: false);
            return;
        }
        WriteConstants(image, camera, viewProjection, strength, 0f, 0f);
        _context.OMSetRenderTargets(image.ColorWrite!);
        _context.RSSetViewport(new Viewport(0, 0, image.Width, image.Height));
        Prepare(_lightVertexShader, _lightPixelShader, _noDepth);
        _context.PSSetShaderResource(1, image.DepthRead!);
        _context.PSSetShaderResource(2, image.NormalsRead);
        _context.PSSetShaderResource(3, image.AlbedoRead!);
        _context.DrawInstanced(4, (uint)_count, 0, 0);
        for (uint slot = 0; slot < 4; slot++)
        {
            _context.VSUnsetShaderResource(slot);
            _context.PSUnsetShaderResource(slot);
        }
        image.Bind(normals: false);
    }

    // Draws the lamps' glows into the bound image, behind what is in front of them. fogDistance: 0 without fog.
    public void DrawCoronas(SceneImage image, Camera camera, Matrix4x4 viewProjection, float strength, float fogDistance)
    {
        if (_lightsView is null || strength <= 0f)
            return;
        WriteConstants(image, camera, viewProjection, 0f, strength, fogDistance);
        Prepare(_coronaVertexShader, _coronaPixelShader, _testDepth);
        _context.DrawInstanced(4, (uint)_count, 0, 0);
        _context.VSUnsetShaderResource(0);
    }

    private void Prepare(ID3D11VertexShader vertexShader, ID3D11PixelShader pixelShader, ID3D11DepthStencilState depth)
    {
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(vertexShader);
        _context.GSSetShader(null);
        _context.PSSetShader(pixelShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.VSSetShaderResource(0, _lightsView!);
        _context.PSSetShaderResource(0, _lightsView!);
        _context.OMSetBlendState(_add, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(depth, 0);
        _context.RSSetState(_rasterizerState);
    }

    private void WriteConstants(SceneImage image, Camera camera, Matrix4x4 viewProjection, float light, float corona, float fogDistance)
    {
        // Camera-relative: the shaders never handle the map's large coordinates.
        var relative = Matrix4x4.CreateTranslation(camera.Position) * viewProjection;
        Matrix4x4.Invert(relative, out var unproject);
        var forward = Vector3.Normalize(camera.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Cross(right, forward);
        var constants = new Constants
        {
            ViewProjection = relative,
            Unproject = unproject,
            Camera = new Vector4(camera.Position, LitDistance),
            Right = new Vector4(right, light),
            Up = new Vector4(up, corona),
            Size = new Vector4(image.Width, image.Height, 1f / image.Width, 1f / image.Height),
            Fog = new Vector4(fogDistance, 0f, 0f, 0f),
        };
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);
    }

    public void Dispose()
    {
        _lightsView?.Dispose();
        _lights?.Dispose();
        _rasterizerState.Dispose();
        _testDepth.Dispose();
        _noDepth.Dispose();
        _add.Dispose();
        _constantBuffer.Dispose();
        _coronaPixelShader.Dispose();
        _coronaVertexShader.Dispose();
        _lightPixelShader.Dispose();
        _lightVertexShader.Dispose();
    }
}
