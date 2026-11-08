using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodeRunner.Data.Models;
using NodeRunner.Data.Textures;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Draws 3D models (ModelData uploaded once as a GpuModel) with their colour texture and simple lighting: a sun-like
// directional light, a sky/ground ambient term and fog.
public sealed unsafe class ModelRenderer : IDisposable
{
    private const string ShaderSource = @"
// Same for every object of a pass.
cbuffer Frame : register(b0)
{
    float4x4 ViewProjection;
    float4 LightDirection;
    float4 SunColor;
    float4 SkyAmbient;
    float4 GroundAmbient;
    float4 FogColor;   // a: 1 = fog on.
    float4 CameraFog;  // xyz: camera position, w: distance at which the fog hides everything.
};
cbuffer Object : register(b2)
{
    float4x4 World;
    float4 Tint;
    uint4 PickId;      // x: number of the object, for picking.
    float4 Emission;   // x: how much the mesh gives off its own light (0 = only lit by the sun).
    float4 Terrain;    // Ground layers: x = 1 to blend with the mask texture, y = 1 for the mask only.
                       // z = 1: the colour is the vertex colour (no texture).
};
Texture2D ColorTexture : register(t0);
Texture2D Layer1 : register(t1);
Texture2D Layer2 : register(t2);
Texture2D Layer3 : register(t3);
Texture2D BlendMask : register(t4);
SamplerState ColorSampler : register(s0);

// Sun shadows (see ShadowMaps): one depth image per slice of the view, seen from the sun.
cbuffer Shadows : register(b1)
{
    float4x4 ShadowMatrix[3];
    float4 ShadowSplits;   // xyz: far end of each slice (m along the view direction), w: 1 = shadows on.
    float4 ShadowTexels;   // xyz: size of a texel of each slice (m), w: 1 / image size.
    float4 ShadowForward;  // xyz: view direction of the camera.
};
Texture2DArray<float> ShadowMap : register(t5);
SamplerComparisonState ShadowSampler : register(s1);
struct VS_INPUT
{
    float3 pos : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR0;
    float4 weights : COLOR1; float2 maskUv : TEXCOORD1;
};
struct PS_INPUT
{
    float4 pos : SV_POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR0; float3 world : TEXCOORD1;
    float4 weights : COLOR1; float2 maskUv : TEXCOORD2;
};

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    float4 world = mul(World, float4(input.pos, 1.0f));
    output.pos = mul(ViewProjection, world);
    output.world = world.xyz;
    output.normal = mul((float3x3)World, input.normal);
    output.uv = input.uv;
    output.color = input.color;
    output.weights = input.weights;
    output.maskUv = input.maskUv;
    return output;
}

// 1 in the sun, 0 in the shade of something closer to the sun; 1 beyond the shadow distance (faded in).
float SunShadow(float3 world, float3 n)
{
    float depth = dot(world - CameraFog.xyz, ShadowForward.xyz);
    if (ShadowSplits.w < 0.5f || depth >= ShadowSplits.z)
        return 1.0f;
    int c = depth < ShadowSplits.x ? 0 : depth < ShadowSplits.y ? 1 : 2;
    float texel = c == 0 ? ShadowTexels.x : c == 1 ? ShadowTexels.y : ShadowTexels.z;

    // Moved off the surface along its normal (and towards the sun), so it doesn't shade itself.
    float4 p = mul(ShadowMatrix[c], float4(world + n * (1.5f * texel), 1.0f));
    float2 uv = p.xy * float2(0.5f, -0.5f) + 0.5f;
    float reference = p.z - 1.5f * ShadowTexels.w;

    // 3 x 3 filtered samples: soft edges.
    float lit = 0.0f;
    [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
            lit += ShadowMap.SampleCmpLevelZero(ShadowSampler, float3(uv + float2(x, y) * ShadowTexels.w, c), reference);
    lit /= 9.0f;

    float fade = saturate((ShadowSplits.z - depth) / (0.15f * ShadowSplits.z));
    return lerp(1.0f, lit, fade);
}

// Lit colour; alpha straight from the colour.
float4 Shade(PS_INPUT input, float4 color)
{
    float3 n = input.normal;
    float len = length(n);
    n = len > 0.0001f ? n / len : float3(0, 0, 1);
    float sun = saturate(dot(n, LightDirection.xyz));
    if (sun > 0.0f)
        sun *= SunShadow(input.world, n);
    float sky = 0.5f + 0.5f * n.z;
    float3 light = lerp(GroundAmbient.rgb, SkyAmbient.rgb, sky) + SunColor.rgb * sun;
    // Own light: at least the texture colour, brighter than the surroundings at night.
    float3 lit = color.rgb * Tint.rgb * max(light, Emission.x * 1.4f);

    // Distance fog, thicker towards the horizon colour; none on the first quarter of the distance.
    float distance = length(input.world - CameraFog.xyz);
    float fog = FogColor.a * saturate((distance - 0.25f * CameraFog.w) / (0.75f * CameraFog.w));
    fog = fog * fog * (3.0f - 2.0f * fog);
    return float4(lerp(lit, FogColor.rgb, fog), color.a);
}

float4 Shade(PS_INPUT input)
{
    float4 color = ColorTexture.Sample(ColorSampler, input.uv);
    if (Terrain.z > 0.5f)
        color.rgb *= input.color.rgb;
    if (Terrain.w > 0.5f)
        color.rgb *= Layer1.Sample(ColorSampler, input.maskUv).rgb;
    return Shade(input, color);
}

// Solid: the texture alpha may be something else than transparency (e.g. a tint mask), ignore it.
float4 PSOpaque(PS_INPUT input) : SV_Target
{
    return float4(Shade(input).rgb, 1.0f);
}

// Ground: four layers blended by weights from the second vertex colour and / or the mask texture
// (blue: layer 0 to 1 and 2 to 3, green: layers 0-1 to 2-3). The first vertex colour's alpha chooses
// between the mask (0) and the vertex colour (1).
float4 PSTerrain(PS_INPUT input) : SV_Target
{
    float4 w = input.weights;
    if (Terrain.x > 0.5f)
    {
        float4 m = BlendMask.Sample(ColorSampler, input.maskUv);
        w = Terrain.y > 0.5f ? m : lerp(m, w, input.color.a);
    }
    float4 c0 = ColorTexture.Sample(ColorSampler, input.uv);
    float4 c1 = Layer1.Sample(ColorSampler, input.uv);
    float4 c2 = Layer2.Sample(ColorSampler, input.uv);
    float4 c3 = Layer3.Sample(ColorSampler, input.uv);
    float4 color = lerp(lerp(c0, c1, w.b), lerp(c2, c3, w.b), w.g);
    return float4(Shade(input, color).rgb, 1.0f);
}

float4 PSCutout(PS_INPUT input) : SV_Target
{
    float4 color = Shade(input);
    clip(color.a - 0.33f);
    return float4(color.rgb, 1.0f);
}

// The vertex alpha fades decals in and out (e.g. grime fading towards the top of a wall).
float4 PSTranslucent(PS_INPUT input) : SV_Target
{
    float4 color = Shade(input);
    color.a *= input.color.a;
    clip(color.a - 0.01f);
    return color;
}

// Shadow images: depth only, with the holes of foliage and fences.
void PSShadowCutout(PS_INPUT input)
{
    clip(ColorTexture.Sample(ColorSampler, input.uv).a - 0.33f);
}

// Picking: the object's number instead of its colour, with the same holes as on screen.
uint PSPickOpaque(PS_INPUT input) : SV_Target
{
    return PickId.x;
}

uint PSPickCutout(PS_INPUT input) : SV_Target
{
    clip(ColorTexture.Sample(ColorSampler, input.uv).a - 0.33f);
    return PickId.x;
}

uint PSPickTranslucent(PS_INPUT input) : SV_Target
{
    clip(ColorTexture.Sample(ColorSampler, input.uv).a * input.color.a - 0.1f);
    return PickId.x;
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 LightDirection;
        public Vector4 SunColor;
        public Vector4 SkyAmbient;
        public Vector4 GroundAmbient;
        public Vector4 FogColor;
        public Vector4 CameraFog;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectConstants
    {
        public Matrix4x4 World;
        public Vector4 Tint;
        public uint PickId;
        private readonly uint _padding1, _padding2, _padding3;
        public Vector4 Emission;
        public Vector4 Terrain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowConstants
    {
        public Matrix4x4 Cascade0, Cascade1, Cascade2;
        public Vector4 Splits;
        public Vector4 Texels;
        public Vector4 Forward;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ModelVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 color, Vector4 weights, Vector2 maskTexCoord)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Normal = normal;
        public readonly Vector2 TexCoord = texCoord;
        public readonly uint Color = Pack(color);

        public readonly uint Weights = Pack(weights);
        public readonly Vector2 MaskTexCoord = maskTexCoord;

        private static uint Pack(Vector4 c)
        {
            static uint Byte(float v) => (uint)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
            return Byte(c.X) | Byte(c.Y) << 8 | Byte(c.Z) << 16 | Byte(c.W) << 24;
        }
    }

    internal static int VertexSize => sizeof(ModelVertex);

    public SceneLighting Lighting { get; set; } = SceneLighting.Neutral;

    public Vector3 CameraPosition { get; set; }

    public ShadowMaps? Shadows { get; set; }

    // Colours of the vehicle being drawn, by VehiclePaintSlot (null: parts keep their texture colour).
    public Vector3[]? VehiclePaint { get; set; }

    // Model viewer: meshes (by number in the ModelData) not drawn, and one drawn tinted.
    public IReadOnlySet<int>? HiddenMeshes { get; set; }
    public int HighlightedMesh { get; set; } = -1;
    public Vector3 HighlightColor { get; set; } = new(1.6f, 1.25f, 0.6f);

    // Model viewer: triangle edges only (drawn slightly in front, to lay them over the model), and solid parts
    // without their texture.
    public bool Wireframe { get; set; }
    public bool Untextured { get; set; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _opaquePixelShader;
    private readonly ID3D11PixelShader _terrainPixelShader;
    private readonly ID3D11PixelShader _cutoutPixelShader;
    private readonly ID3D11PixelShader _translucentPixelShader;
    private readonly ID3D11PixelShader _shadowCutoutPixelShader;
    private readonly ID3D11PixelShader _pickOpaquePixelShader;
    private readonly ID3D11PixelShader _pickCutoutPixelShader;
    private readonly ID3D11PixelShader _pickTranslucentPixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _frameBuffer;
    private readonly ID3D11Buffer _objectBuffer;
    private readonly ID3D11Buffer _shadowConstantBuffer;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11RasterizerState _shadowRasterizerState;
    private readonly ID3D11DepthStencilState _shadowDepthStencilState;
    private readonly ID3D11RasterizerState _doubleSidedRasterizerState;
    private readonly ID3D11RasterizerState _wireframeRasterizerState;
    private readonly ID3D11DepthStencilState _depthStencilState;
    private readonly ID3D11DepthStencilState _translucentDepthStencilState;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11BlendState _translucentBlendState;
    private ID3D11SamplerState _samplerState;
    private int _anisotropy = 8;
    private readonly GpuTexture _untextured;
    private readonly GpuTexture _white;
    private readonly GpuTexture _untexturedTranslucent;
    private ModelPass _pass;

    public ModelRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        ReadOnlyMemory<byte> vsBytecode = Compiler.Compile(ShaderSource, "VS", "models", "vs_5_0");
        _vertexShader = device.CreateVertexShader(vsBytecode.Span);
        _opaquePixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSOpaque", "models", "ps_5_0").Span);
        _terrainPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSTerrain", "models", "ps_5_0").Span);
        _cutoutPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSCutout", "models", "ps_5_0").Span);
        _translucentPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSTranslucent", "models", "ps_5_0").Span);
        _shadowCutoutPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSShadowCutout", "models", "ps_5_0").Span);
        _pickOpaquePixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSPickOpaque", "models", "ps_5_0").Span);
        _pickCutoutPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSPickCutout", "models", "ps_5_0").Span);
        _pickTranslucentPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSPickTranslucent", "models", "ps_5_0").Span);

        InputElementDescription[] layout =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new("COLOR", 0, Format.R8G8B8A8_UNorm, 32, 0),
            new("COLOR", 1, Format.R8G8B8A8_UNorm, 36, 0),
            new("TEXCOORD", 1, Format.R32G32_Float, 40, 0),
        ];
        _inputLayout = device.CreateInputLayout(layout, vsBytecode.Span);

        _frameBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(FrameConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _objectBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(ObjectConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _shadowConstantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(ShadowConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        // Solid meshes only show their front faces, as in the game: some are only meant to be seen from one
        // side (e.g. the fake outside view above an opening of a metro tunnel, seen from below).
        // Cut-out and translucent meshes (foliage, fences, glass) are single planes seen from both sides.
        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
        });
        _doubleSidedRasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });

        _wireframeRasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Wireframe,
            CullMode = CullMode.None,
            DepthClipEnable = true,
            DepthBias = 2000,
            SlopeScaledDepthBias = 1f,
            AntialiasedLineEnable = true,
        });

        // Shadow images: both faces (a wall seen from behind by the sun still casts its shadow), pushed away from the
        // sun on slopes so surfaces don't shade themselves; things between the sun and the image are flattened onto
        // it instead of being cut off.
        _shadowRasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = false,
            DepthBias = 16,
            SlopeScaledDepthBias = 2f,
            DepthBiasClamp = 0.01f,
        });

        // Shadow images use ordinary depth: 0 towards the sun, cleared to 1.
        _shadowDepthStencilState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual,
        });

        // Reversed Z (see GraphicsDevice).
        _depthStencilState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.GreaterEqual,
        });

        // Translucent meshes are tested against the depth of the solid ones but don't hide each other.
        _translucentDepthStencilState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual,
        });

        // Opaque: an empty description would also disable colour writes.
        _blendState = device.CreateBlendState(BlendDescription.Opaque);

        var translucentBlend = new BlendDescription();
        translucentBlend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _translucentBlendState = device.CreateBlendState(translucentBlend);

        _samplerState = CreateSampler(device, _anisotropy);

        // Light grey, for meshes without (or with a missing) texture.
        _untextured = GpuTexture.CreateSolid(device, 190, 190, 190, 255);
        _white = GpuTexture.CreateSolid(device, 255, 255, 255, 255);
        _untexturedTranslucent = GpuTexture.CreateSolid(device, 190, 200, 210, 64);
    }

    // Texture filtering at grazing angles: 1 = plain (fastest), up to 16 (ground textures stay sharp in the
    // distance).
    public int Anisotropy
    {
        get => _anisotropy;
        set
        {
            value = Math.Clamp(value, 1, 16);
            if (value == _anisotropy)
                return;
            _anisotropy = value;
            _samplerState.Dispose();
            _samplerState = CreateSampler(_device, value);
        }
    }

    private static ID3D11SamplerState CreateSampler(ID3D11Device device, int anisotropy) =>
        device.CreateSamplerState(new SamplerDescription
        {
            Filter = anisotropy > 1 ? Filter.Anisotropic : Filter.MinMagMipLinear,
            MaxAnisotropy = (uint)anisotropy,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });

    public GpuTexture? UploadTexture(TextureData texture) => GpuTexture.Create(_device, texture);

    // Copies a model to the GPU (all its meshes in one vertex and one index buffer).
    public GpuModel? Upload(ModelData model, IReadOnlyList<GpuTexture?[]> meshTextures) =>
        Prepare(model) is { } prepared ? Finish(prepared, meshTextures) : null;

    // The GPU buffers of a model, without its textures. Safe on any thread (the device is free-threaded): the heavy
    // part of an upload, kept off the drawing loop.
    public PreparedModel? Prepare(ModelData model)
    {
        int vertexCount = 0, indexCount = 0;
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Indices.Length == 0 || mesh.Positions.Length == 0)
                continue;
            vertexCount += mesh.Positions.Length;
            indexCount += mesh.Indices.Length;
        }
        if (indexCount == 0)
            return null;

        var vertices = new ModelVertex[vertexCount];
        var indices = new ushort[indexCount];
        var ranges = new List<PreparedModel.Range>();
        int v = 0, x = 0;
        for (int m = 0; m < model.Meshes.Count; m++)
        {
            var mesh = model.Meshes[m];
            if (mesh.Indices.Length == 0 || mesh.Positions.Length == 0)
                continue;
            var terrain = mesh.Terrain;
            ranges.Add(new PreparedModel.Range(m, x, mesh.Indices.Length, v, mesh.BlendMode, mesh.Emission, terrain is not null,
                terrain?.Mode ?? TerrainBlendMode.VertexOnly, mesh.VertexColored, VehiclePaints.SlotOf(mesh.DiffuseColor),
                terrain is null && mesh.TintTextureName is not null));
            for (int i = 0; i < mesh.Positions.Length; i++)
            {
                vertices[v++] = new ModelVertex(mesh.Positions[i], mesh.Normals[i], mesh.TexCoords[i], mesh.Colors[i],
                    terrain?.Weights[i] ?? Vector4.Zero, terrain?.MaskTexCoords[i] ?? mesh.TintTexCoords?[i] ?? Vector2.Zero);
            }
            mesh.Indices.CopyTo(indices, x);
            x += mesh.Indices.Length;
        }

        var vertexBuffer = _device.CreateBuffer(vertices,
            new BufferDescription((uint)(vertices.Length * sizeof(ModelVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        ID3D11Buffer indexBuffer;
        try
        {
            indexBuffer = _device.CreateBuffer(indices,
                new BufferDescription((uint)(indices.Length * sizeof(ushort)), BindFlags.IndexBuffer, ResourceUsage.Immutable));
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
        return new PreparedModel(vertexBuffer, indexBuffer, ranges.ToArray(), indexCount / 3,
            (long)vertexCount * sizeof(ModelVertex) + indexCount * 2L);
    }

    // Puts the textures on a prepared model (on the drawing thread); the model then owns its buffers.
    public GpuModel Finish(PreparedModel prepared, IReadOnlyList<GpuTexture?[]> meshTextures)
    {
        var parts = new GpuModel.Part[prepared.Ranges.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var r = prepared.Ranges[i];
            var textures = r.Mesh < meshTextures.Count ? meshTextures[r.Mesh] : [];
            var texture = textures.Length > 0 ? textures[0] : null;
            GpuTexture?[]? layers = r.Terrain ? textures.Skip(1).Concat(new GpuTexture?[4]).Take(4).ToArray() : null;
            parts[i] = new GpuModel.Part(r.FirstIndex, r.IndexCount, r.BaseVertex, texture, r.BlendMode, r.Emission, layers,
                r.TerrainMode, r.VertexColored, r.PaintSlot, r.Mesh, r.Tinted && textures.Length > 1 ? textures[1] : null);
        }
        return new GpuModel(prepared.TakeVertexBuffer(), prepared.TakeIndexBuffer(), parts, prepared.TriangleCount, prepared.ByteSize);
    }

    // Sets the shaders and states shared by every model.
    public void Begin(ModelPass pass)
    {
        _pass = pass;
        bool translucent = pass == ModelPass.Translucent;
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        BindConstants();
        _context.PSSetSampler(0, _samplerState);
        _context.GSSetShader(null);
        _context.OMSetBlendState(translucent ? _translucentBlendState : _blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(translucent ? _translucentDepthStencilState : _depthStencilState, 0);
        _context.RSSetState(_rasterizerState);
        SetShadows(Shadows);
    }

    // Frame constants written again at the first model of the pass (the lighting or camera may have changed).
    private void BindConstants()
    {
        _frameWritten = false;
        _context.VSSetConstantBuffer(0, _frameBuffer);
        _context.PSSetConstantBuffer(0, _frameBuffer);
        _context.VSSetConstantBuffer(2, _objectBuffer);
        _context.PSSetConstantBuffer(2, _objectBuffer);
    }

    private void SetShadows(ShadowMaps? shadows)
    {
        var constants = new ShadowConstants();
        if (shadows is not null)
        {
            constants.Cascade0 = shadows.Cascades[0];
            constants.Cascade1 = shadows.Cascades[1];
            constants.Cascade2 = shadows.Cascades[2];
            constants.Splits = new Vector4(shadows.Splits[0], shadows.Splits[1], shadows.Splits[2], 1f);
            constants.Texels = new Vector4(shadows.TexelSizes[0], shadows.TexelSizes[1], shadows.TexelSizes[2], 1f / ShadowMaps.Size);
            constants.Forward = new Vector4(shadows.CameraForward, 0f);
        }
        var map = _context.Map(_shadowConstantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_shadowConstantBuffer, 0);
        _context.PSSetConstantBuffer(1, _shadowConstantBuffer);
        if (shadows is null)
        {
            _context.PSUnsetShaderResource(5);
        }
        else
        {
            _context.PSSetShaderResource(5, shadows.View);
            _context.PSSetSampler(1, shadows.Sampler);
        }
    }

    // Sets the shaders and states for drawing into the shadow image bound by BeginCascade, before a series of
    // DrawShadow.
    public void BeginShadow()
    {
        _context.PSUnsetShaderResource(5); // The shadow images are being drawn.
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        BindConstants();
        _context.PSSetSampler(0, _samplerState);
        _context.GSSetShader(null);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_shadowDepthStencilState, 0);
        _context.RSSetState(_shadowRasterizerState);
    }

    // Draws the solid and cut-out meshes of a placed model into the shadow image seen through lightViewProjection.
    public void DrawShadow(GpuModel model, Matrix4x4 world, Matrix4x4 lightViewProjection)
    {
        SetModel(model, world, lightViewProjection, Vector3.One, pickId: 0);
        foreach (var part in model.Parts)
        {
            if (part.BlendMode is MeshBlendMode.Translucent or MeshBlendMode.Water)
                continue; // Glass, decals and water let the light through.
            if (part.BlendMode == MeshBlendMode.Cutout)
            {
                _context.PSSetShader(_shadowCutoutPixelShader);
                _context.PSSetShaderResource(0, (part.Texture ?? _untextured).View);
            }
            else
            {
                _context.PSSetShader(null);
            }
            _context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        }
    }

    public void Draw(GpuModel model, Matrix4x4 world, Matrix4x4 viewProjection, Vector3 tint)
    {
        if (_pass == ModelPass.Translucent && !model.HasTranslucentParts)
            return;
        SetModel(model, world, viewProjection, tint, pickId: 0);
        foreach (var part in model.Parts)
        {
            bool translucent = part.BlendMode == MeshBlendMode.Translucent;
            if (translucent != (_pass == ModelPass.Translucent) || part.BlendMode == MeshBlendMode.Water)
                continue; // Water: drawn by WaterRenderer.
            bool terrain = part.Layers is not null && part.BlendMode == MeshBlendMode.Opaque;
            _context.PSSetShader(part.BlendMode switch
            {
                _ when terrain => _terrainPixelShader,
                MeshBlendMode.Cutout => _cutoutPixelShader,
                MeshBlendMode.Translucent => _translucentPixelShader,
                _ => _opaquePixelShader,
            });
            _context.RSSetState(Wireframe ? _wireframeRasterizerState
                : part.BlendMode == MeshBlendMode.Opaque ? _rasterizerState : _doubleSidedRasterizerState);
            float emission = part.Emission switch
            {
                MeshEmission.Always => 1f,
                MeshEmission.Night => Lighting.NightLights,
                _ => 0f,
            };
            // Vehicle paint: the part's colour slot, over a white base if its texture is missing.
            var paint = VehiclePaint is { } colors && part.PaintSlot != VehiclePaintSlot.None && (int)part.PaintSlot < colors.Length
                ? colors[(int)part.PaintSlot] : Vector3.One;
            bool painted = VehiclePaint is not null && part.PaintSlot != VehiclePaintSlot.None;
            if (HiddenMeshes is { } hidden && hidden.Contains(part.Mesh))
                continue;
            var highlight = HighlightedMesh == part.Mesh ? HighlightColor : Vector3.One;
            SetTint(tint * paint * highlight);
            var baseTexture = Untextured && part.BlendMode == MeshBlendMode.Opaque ? _white
                : part.Texture ?? (part.VertexColored || painted ? _white : translucent ? _untexturedTranslucent : _untextured);
            if (terrain)
            {
                // A missing layer shows layer 0 instead; without the mask, the vertex weights alone.
                var mask = part.Layers![3];
                bool useMask = mask is not null && part.TerrainMode != TerrainBlendMode.VertexOnly;
                SetMesh(emission, new Vector4(useMask ? 1f : 0f, part.TerrainMode == TerrainBlendMode.MaskOnly ? 1f : 0f, 0f, 0f));
                for (int l = 0; l < 3; l++)
                    _context.PSSetShaderResource((uint)l + 1, (part.Layers[l] ?? baseTexture).View);
                _context.PSSetShaderResource(4, (mask ?? baseTexture).View);
            }
            else
            {
                SetMesh(emission, new Vector4(0f, 0f, part.VertexColored ? 1f : 0f, part.TintTexture is null ? 0f : 1f));
                if (part.TintTexture is not null)
                    _context.PSSetShaderResource(1, part.TintTexture.View);
            }
            _context.PSSetShaderResource(0, baseTexture.View);
            _context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        }
    }

    // Sets the shaders and states for drawing object numbers into a PickBuffer (already bound), before a series of
    // DrawPick.
    public void BeginPick()
    {
        Begin(ModelPass.Solid);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue); // No blending on numbers.
    }

    public void DrawPick(GpuModel model, Matrix4x4 world, Matrix4x4 viewProjection, uint pickId)
    {
        SetModel(model, world, viewProjection, Vector3.One, pickId);
        foreach (var part in model.Parts)
        {
            _context.PSSetShader(part.BlendMode switch
            {
                MeshBlendMode.Cutout => _pickCutoutPixelShader,
                MeshBlendMode.Translucent => _pickTranslucentPixelShader,
                _ => _pickOpaquePixelShader,
            });
            _context.RSSetState(part.BlendMode == MeshBlendMode.Opaque ? _rasterizerState : _doubleSidedRasterizerState);
            _context.PSSetShaderResource(0, (part.Texture ?? _untextured).View);
            _context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        }
    }

    private void SetModel(GpuModel model, Matrix4x4 world, Matrix4x4 viewProjection, Vector3 tint, uint pickId)
    {
        if (!_frameWritten || viewProjection != _frameViewProjection)
        {
            var frame = new FrameConstants
            {
                ViewProjection = viewProjection,
                LightDirection = new Vector4(Lighting.SunDirection, 0f),
                SunColor = new Vector4(Lighting.SunColor, 0f),
                SkyAmbient = new Vector4(Lighting.SkyAmbient, 0f),
                GroundAmbient = new Vector4(Lighting.GroundAmbient, 0f),
                FogColor = new Vector4(Lighting.Horizon, Lighting.FogDistance > 0f ? 1f : 0f),
                CameraFog = new Vector4(CameraPosition, MathF.Max(Lighting.FogDistance, 1f)),
            };
            var frameMap = _context.Map(_frameBuffer, 0, MapMode.WriteDiscard);
            Unsafe.Write((void*)frameMap.DataPointer, frame);
            _context.Unmap(_frameBuffer, 0);
            _frameViewProjection = viewProjection;
            _frameWritten = true;
        }

        _constants = new ObjectConstants { World = world, Tint = new Vector4(tint, 1f), PickId = pickId };
        WriteConstants();

        _context.IASetVertexBuffer(0, model.VertexBuffer, (uint)sizeof(ModelVertex));
        _context.IASetIndexBuffer(model.IndexBuffer, Format.R16_UInt, 0);
    }

    private bool _frameWritten;
    private Matrix4x4 _frameViewProjection;

    // Constants of the model being drawn: only the emission and the terrain settings change from one mesh to the
    // next.
    private ObjectConstants _constants;

    private void SetTint(Vector3 tint)
    {
        var value = new Vector4(tint, 1f);
        if (_constants.Tint == value)
            return;
        _constants.Tint = value;
        WriteConstants();
    }

    private void SetMesh(float emission, Vector4 terrain)
    {
        if (_constants.Emission.X == emission && _constants.Terrain == terrain)
            return;
        _constants.Emission = new Vector4(emission, 0f, 0f, 0f);
        _constants.Terrain = terrain;
        WriteConstants();
    }

    private void WriteConstants()
    {
        var map = _context.Map(_objectBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, _constants);
        _context.Unmap(_objectBuffer, 0);
    }

    public void Dispose()
    {
        _untextured.Dispose();
        _white.Dispose();
        _untexturedTranslucent.Dispose();
        _samplerState.Dispose();
        _translucentBlendState.Dispose();
        _blendState.Dispose();
        _translucentDepthStencilState.Dispose();
        _depthStencilState.Dispose();
        _rasterizerState.Dispose();
        _doubleSidedRasterizerState.Dispose();
        _wireframeRasterizerState.Dispose();
        _frameBuffer.Dispose();
        _objectBuffer.Dispose();
        _shadowConstantBuffer.Dispose();
        _shadowRasterizerState.Dispose();
        _shadowDepthStencilState.Dispose();
        _shadowCutoutPixelShader.Dispose();
        _inputLayout.Dispose();
        _pickTranslucentPixelShader.Dispose();
        _pickCutoutPixelShader.Dispose();
        _pickOpaquePixelShader.Dispose();
        _translucentPixelShader.Dispose();
        _cutoutPixelShader.Dispose();
        _opaquePixelShader.Dispose();
        _terrainPixelShader.Dispose();
        _vertexShader.Dispose();
    }
}

// The two passes of ModelRenderer, in drawing order.
public enum ModelPass
{
    Solid,

    Translucent,
}

public sealed class GpuModel : IDisposable
{
    // Only see-through parts: a decal or a painted shadow laid on another surface, or a pane of glass.
    public bool IsOverlay { get; }

    // Layers: ground layers 1 to 3, then the blend mask (terrain only, else null).
    internal readonly record struct Part(int FirstIndex, int IndexCount, int BaseVertex, GpuTexture? Texture, MeshBlendMode BlendMode,
        MeshEmission Emission, GpuTexture?[]? Layers, TerrainBlendMode TerrainMode, bool VertexColored, VehiclePaintSlot PaintSlot,
        int Mesh, GpuTexture? TintTexture);

    internal GpuModel(ID3D11Buffer vertexBuffer, ID3D11Buffer indexBuffer, Part[] parts, int triangleCount, long byteSize)
    {
        VertexBuffer = vertexBuffer;
        IndexBuffer = indexBuffer;
        Parts = parts;
        HasTranslucentParts = parts.Any(p => p.BlendMode == MeshBlendMode.Translucent);
        IsOverlay = parts.Length > 0 && parts.All(p => p.BlendMode == MeshBlendMode.Translucent);
        HasWaterParts = parts.Any(p => p.BlendMode == MeshBlendMode.Water);
        TriangleCount = triangleCount;
        ByteSize = byteSize;
    }

    internal ID3D11Buffer VertexBuffer { get; }
    internal ID3D11Buffer IndexBuffer { get; }
    internal Part[] Parts { get; }
    internal bool HasTranslucentParts { get; }

    public bool HasWaterParts { get; }

    public int TriangleCount { get; }

    public long ByteSize { get; }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
    }
}

// A model's GPU buffers made off the drawing thread (ModelRenderer.Prepare), waiting for its textures. Disposed if
// it is dropped before ModelRenderer.Finish.
public sealed class PreparedModel : IDisposable
{
    internal readonly record struct Range(int Mesh, int FirstIndex, int IndexCount, int BaseVertex, MeshBlendMode BlendMode,
        MeshEmission Emission, bool Terrain, TerrainBlendMode TerrainMode, bool VertexColored, VehiclePaintSlot PaintSlot, bool Tinted);

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;

    internal PreparedModel(ID3D11Buffer vertexBuffer, ID3D11Buffer indexBuffer, Range[] ranges, int triangleCount, long byteSize)
    {
        _vertexBuffer = vertexBuffer;
        _indexBuffer = indexBuffer;
        Ranges = ranges;
        TriangleCount = triangleCount;
        ByteSize = byteSize;
    }

    internal Range[] Ranges { get; }

    public int TriangleCount { get; }

    public long ByteSize { get; }

    internal ID3D11Buffer TakeVertexBuffer() => Interlocked.Exchange(ref _vertexBuffer, null)
        ?? throw new InvalidOperationException("Model already finished.");

    internal ID3D11Buffer TakeIndexBuffer() => Interlocked.Exchange(ref _indexBuffer, null)
        ?? throw new InvalidOperationException("Model already finished.");

    public void Dispose()
    {
        Interlocked.Exchange(ref _vertexBuffer, null)?.Dispose();
        Interlocked.Exchange(ref _indexBuffer, null)?.Dispose();
    }
}
