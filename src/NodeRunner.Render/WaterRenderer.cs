using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodeRunner.Data.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Water surfaces: flat triangles whose look is computed per pixel.
public sealed unsafe class WaterRenderer : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 ViewProjection;
    float4 CameraTime;   // xyz: camera position, w: time (s).
    float4 SunDirection; // xyz: towards the sun (or the moon), w: near plane of the camera.
    float4 SunColor;     // a: visibility of the sun disc (0 at night).
    float4 SkyAmbient;
    float4 Zenith;
    float4 Horizon;      // Also the fog colour.
    float4 Settings;     // x: 1 = fog on, y: fog distance, z: 1 = scene depth available.
};
// Water meshes of the models (pools, fountains, streams): placement and wave strength of the model drawn.
cbuffer MeshConstants : register(b1)
{
    float4x4 World;
    float4 MeshWaves; // x: wave strength.
};
Texture2D<float> SceneDepth : register(t0);

struct VS_INPUT { float3 pos : POSITION; float waves : TEXCOORD0; };
struct VS_MESH_INPUT { float3 pos : POSITION; float3 normal : NORMAL; };
struct PS_INPUT { float4 pos : SV_POSITION; float3 world : TEXCOORD0; float waves : TEXCOORD1; float3 up : TEXCOORD2; };

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(ViewProjection, float4(input.pos, 1.0f));
    output.world = input.pos;
    output.waves = input.waves;
    output.up = float3(0.0f, 0.0f, 1.0f);
    return output;
}

PS_INPUT VSMesh(VS_MESH_INPUT input)
{
    PS_INPUT output;
    float4 world = mul(World, float4(input.pos, 1.0f));
    output.pos = mul(ViewProjection, world);
    output.world = world.xyz;
    output.waves = MeshWaves.x;
    output.up = mul((float3x3)World, input.normal);
    return output;
}

// Slope of the surface: deep-water waves (speed from their length), around a main wind direction.
// Waves much smaller than a pixel fade out instead of flickering.
float2 WaveSlope(float2 p, float t, float footprint)
{
    static const float3 waves[7] =
    {
        // Direction (degrees), length (m), steepness.
        float3(20.0f, 47.0f, 0.10f),
        float3(-15.0f, 26.0f, 0.09f),
        float3(55.0f, 15.3f, 0.08f),
        float3(-40.0f, 8.7f, 0.07f),
        float3(5.0f, 4.9f, 0.06f),
        float3(75.0f, 2.7f, 0.05f),
        float3(-65.0f, 1.45f, 0.05f),
    };
    float2 slope = 0.0f;
    [unroll] for (int i = 0; i < 7; i++)
    {
        float angle = radians(waves[i].x);
        float2 d = float2(cos(angle), sin(angle));
        float k = 6.2831853f / waves[i].y;
        float phase = k * dot(d, p) - sqrt(9.81f * k) * t + i * 1.7f;
        float fade = saturate(waves[i].y / (6.0f * footprint) - 0.5f);
        slope += d * waves[i].z * cos(phase) * fade;
    }
    return slope;
}

float3 SkyColor(float3 ray)
{
    return lerp(Horizon.rgb, Zenith.rgb, pow(saturate(ray.z), 0.45f));
}

float4 PS(PS_INPUT input) : SV_Target
{
    float3 camera = CameraTime.xyz;
    float3 toCamera = camera - input.world;
    float distance = length(toCamera);
    float3 v = toCamera / max(distance, 0.001f);
    float len = length(input.up);
    float3 up = len > 0.0001f ? input.up / len : float3(0.0f, 0.0f, 1.0f);
    bool below = dot(toCamera, up) < 0.0f;

    float footprint = max(length(fwidth(input.world.xy)), 0.001f);
    float2 slope = WaveSlope(input.world.xy, CameraTime.w, footprint) * input.waves;
    float3 n = normalize(up + float3(-slope, 0.0f));

    // Light received by the water body: sky all around, plus the sun from above.
    float3 light = SkyAmbient.rgb * 1.3f + SunColor.rgb * saturate(SunDirection.z) * 0.9f;
    float3 deep = float3(0.012f, 0.060f, 0.080f) * light;
    float3 shallow = float3(0.050f, 0.190f, 0.180f) * light;

    if (below)
        return float4(deep * 1.5f, 0.85f);

    // Water thickness along the view ray, from the depth of what is behind (reversed Z: depth = near / view depth).
    float thickness = 1000.0f;
    if (Settings.z > 0.5f)
    {
        float sceneDepth = SceneDepth.Load(int3(input.pos.xy, 0));
        float near = SunDirection.w;
        float waterView = near / max(input.pos.z, 1e-9f);
        float sceneView = sceneDepth > 0.0f ? near / sceneDepth : 1e7f;
        thickness = max(sceneView - waterView, 0.0f) * distance / max(waterView, 0.001f);
    }
    float vertical = thickness * abs(dot(v, up)); // Depth below the surface, roughly.
    float edge = saturate(vertical * 4.0f);

    // See-through when shallow, then the water's own colour.
    float opacity = 1.0f - exp(-thickness * 0.30f);
    float3 body = lerp(shallow, deep, 1.0f - exp(-thickness * 0.12f));

    // Reflection of the sky, stronger at grazing angles; the sun or moon glints on the waves.
    float fresnel = (0.02f + 0.98f * pow(1.0f - saturate(dot(n, v)), 5.0f)) * edge;
    float3 r = reflect(-v, n);
    r.z = abs(r.z);
    float3 reflection = SkyColor(r);
    float facing = saturate(dot(r, SunDirection.xyz));
    float3 glint = SunColor.rgb * (pow(facing, 900.0f) * (2.0f + 10.0f * SunColor.a) + pow(facing, 60.0f) * 0.25f);

    // Foam where the water meets the shore, broken up by the waves.
    float foam = (1.0f - smoothstep(0.05f, 0.7f, vertical)) * edge * saturate(0.5f + 3.0f * (slope.x + slope.y)) * input.waves;
    float3 foamColor = light * 0.85f;

    // Premultiplied alpha: the reflection is opaque, the body as opaque as the water is thick.
    float3 color = reflection * fresnel + body * (1.0f - fresnel) * opacity + glint * edge + foamColor * foam;
    float alpha = saturate(fresnel + (1.0f - fresnel) * opacity + foam);

    // Same fog as the models.
    float fog = Settings.x * saturate((distance - 0.25f * Settings.y) / (0.75f * Settings.y));
    fog = fog * fog * (3.0f - 2.0f * fog);
    return float4(lerp(color, Horizon.rgb * alpha, fog), alpha);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraTime;
        public Vector4 SunDirection;
        public Vector4 SunColor;
        public Vector4 SkyAmbient;
        public Vector4 Zenith;
        public Vector4 Horizon;
        public Vector4 Settings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshConstants
    {
        public Matrix4x4 World;
        public Vector4 Waves;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WaterVertex(Vector3 position, float waves)
    {
        public readonly Vector3 Position = position;
        public readonly float Waves = waves;
    }

    private const float OceanReach = 40_000f;

    private const float InlandWaves = 0.35f;

    public const float MeshWaves = 0.2f;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11VertexShader _meshVertexShader;
    private readonly ID3D11InputLayout _meshInputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _meshConstantBuffer;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;
    private ID3D11Buffer? _vertexBuffer;
    private int _vertexCount;

    public WaterRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        ReadOnlyMemory<byte> vsBytecode = Compiler.Compile(ShaderSource, "VS", "water", "vs_4_0");
        _vertexShader = device.CreateVertexShader(vsBytecode.Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "water", "ps_4_0").Span);
        _inputLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Vortice.DXGI.Format.R32_Float, 12, 0),
        ], vsBytecode.Span);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        // Model meshes: only the position and the normal of the model vertices are read.
        ReadOnlyMemory<byte> meshBytecode = Compiler.Compile(ShaderSource, "VSMesh", "water", "vs_4_0");
        _meshVertexShader = device.CreateVertexShader(meshBytecode.Span);
        _meshInputLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Vortice.DXGI.Format.R32G32B32_Float, 12, 0),
        ], meshBytecode.Span);
        _meshConstantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(MeshConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        // Seen from above and from below (under water).
        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });

        // Reversed Z, strictly in front: where two surfaces overlap at the same height, only the first is drawn.
        _depthState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Greater,
        });

        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One, // Premultiplied alpha.
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = device.CreateBlendState(blend);
    }

    public bool HasSurfaces => _vertexCount > 0;

    // Replaces the water drawn: triangles (three corners each) and the open ocean at height 0 all around oceanBounds
    // (min X, min Y, max X, max Y; nothing if empty).
    public void SetSurfaces(IEnumerable<Vector3> triangles, Vector4 oceanBounds)
    {
        var vertices = new List<WaterVertex>();
        foreach (var corner in triangles)
            vertices.Add(new WaterVertex(corner, MathF.Abs(corner.Z) < 0.5f ? 1f : InlandWaves));

        // Open ocean: four bands around the map's water, drawn after it (the map's water wins where they overlap).
        if (oceanBounds.Z > oceanBounds.X && oceanBounds.W > oceanBounds.Y)
        {
            float far = OceanReach;
            AddRectangle(vertices, -far, oceanBounds.Y, oceanBounds.X, oceanBounds.W); // West.
            AddRectangle(vertices, oceanBounds.Z, oceanBounds.Y, far, oceanBounds.W); // East.
            AddRectangle(vertices, -far, -far, far, oceanBounds.Y); // South.
            AddRectangle(vertices, -far, oceanBounds.W, far, far); // North.
        }

        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _vertexCount = vertices.Count;
        if (vertices.Count > 0)
        {
            _vertexBuffer = _device.CreateBuffer(CollectionsMarshal.AsSpan(vertices),
                new BufferDescription((uint)(vertices.Count * sizeof(WaterVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        }
    }

    private static void AddRectangle(List<WaterVertex> vertices, float minX, float minY, float maxX, float maxY)
    {
        var a = new WaterVertex(new Vector3(minX, minY, 0f), 1f);
        var b = new WaterVertex(new Vector3(maxX, minY, 0f), 1f);
        var c = new WaterVertex(new Vector3(minX, maxY, 0f), 1f);
        var d = new WaterVertex(new Vector3(maxX, maxY, 0f), 1f);
        vertices.AddRange([a, b, c, c, b, d]);
    }

    // Starts drawing water over the frame, before DrawSurfaces and DrawMesh, then End. sceneDepth: copy of the depth
    // buffer after the solid models (null: the water is opaque everywhere). time moves the waves (seconds).
    public void Begin(Matrix4x4 viewProjection, Camera camera, SceneLighting lighting, float time, ID3D11ShaderResourceView? sceneDepth)
    {
        var constants = new Constants
        {
            ViewProjection = viewProjection,
            CameraTime = new Vector4(camera.Position, time),
            SunDirection = new Vector4(lighting.SunDirection, camera.NearPlane),
            SunColor = new Vector4(lighting.SunColor, lighting.SunDisc),
            SkyAmbient = new Vector4(lighting.SkyAmbient, 0f),
            Zenith = new Vector4(lighting.Zenith, 0f),
            Horizon = new Vector4(lighting.Horizon, 0f),
            Settings = new Vector4(lighting.FogDistance > 0f ? 1f : 0f, MathF.Max(lighting.FogDistance, 1f), sceneDepth is null ? 0f : 1f, 0f),
        };
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.VSSetConstantBuffer(1, _meshConstantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        if (sceneDepth is null)
            _context.PSUnsetShaderResource(0);
        else
            _context.PSSetShaderResource(0, sceneDepth);
        _context.GSSetShader(null);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.RSSetState(_rasterizerState);
    }

    public void DrawSurfaces()
    {
        if (_vertexBuffer is null)
            return;
        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, _vertexBuffer, (uint)sizeof(WaterVertex));
        _context.VSSetShader(_vertexShader);
        _context.Draw((uint)_vertexCount, 0);
    }

    public void DrawMesh(GpuModel model, Matrix4x4 world, float waves = MeshWaves)
    {
        if (!model.HasWaterParts)
            return;
        var constants = new MeshConstants { World = world, Waves = new Vector4(waves, 0f, 0f, 0f) };
        var map = _context.Map(_meshConstantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_meshConstantBuffer, 0);

        _context.IASetInputLayout(_meshInputLayout);
        _context.IASetVertexBuffer(0, model.VertexBuffer, (uint)ModelRenderer.VertexSize);
        _context.IASetIndexBuffer(model.IndexBuffer, Vortice.DXGI.Format.R16_UInt, 0);
        _context.VSSetShader(_meshVertexShader);
        foreach (var part in model.Parts)
        {
            if (part.BlendMode == MeshBlendMode.Water)
                _context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        }
    }

    public void End() => _context.PSUnsetShaderResource(0); // The depth copy is written again next frame.

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _blendState.Dispose();
        _depthState.Dispose();
        _rasterizerState.Dispose();
        _constantBuffer.Dispose();
        _meshConstantBuffer.Dispose();
        _meshInputLayout.Dispose();
        _meshVertexShader.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
