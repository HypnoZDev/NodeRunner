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

    // Smoothing of the edges (TAA): each frame is drawn shifted by a fraction of a pixel and blended with the previous
    // ones, moved back to where the camera saw them.
    public bool Antialiasing { get; init; }

    // Ambient occlusion (nooks and the ground under objects darker): 0 none, 1 normal (half the pixels), 2 high.
    public int AmbientOcclusion { get; init; }

    public float OcclusionStrength { get; init; }

    // Glow around very bright pixels, HDR only (0 none to 1 strong).
    public float Bloom { get; init; }

    // True when the scene needs its own images; otherwise it is drawn straight into the window, as on small PCs.
    public bool IsUsed => Hdr || Antialiasing || AmbientOcclusion > 0 || Scale < 0.999f;
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
    float4 Effects;   // x: share of the glow.
};
Texture2D SceneColor : register(t0);
Texture2D<float> SceneDepth : register(t1);
Texture2D Glow : register(t2);
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
    if (Effects.x > 0.0f)
        c = lerp(c, Glow.SampleLevel(LinearClamp, uv, 0).rgb, Effects.x);
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

    // Glow (bloom): the HDR image shrunk step by step to 1/64 of its size, then enlarged back with each step added, so
    // very bright pixels (sun, lamps, lit windows) spill a soft light around them.
    private const string BloomSource = @"
cbuffer Constants : register(b0)
{
    float4 Texel; // xy: 1 / size of the image read, z = 1: first step (very bright pixels weigh less, no flicker).
};
Texture2D Source : register(t0);
SamplerState LinearClamp : register(s0);

struct VS_OUTPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

float3 Read(float2 uv, float2 offset)
{
    return max(Source.SampleLevel(LinearClamp, uv + offset * Texel.xy, 0).rgb, 0.0f);
}

float Weight(float3 c)
{
    return Texel.z > 0.5f ? 1.0f / (1.0f + max(max(c.r, c.g), c.b)) : 1.0f;
}

// Half the size: 13 reads around the pixel, blended.
float4 PSDown(VS_OUTPUT input) : SV_Target
{
    static const float2 offsets[13] =
    {
        float2(-2.0f, -2.0f), float2(0.0f, -2.0f), float2(2.0f, -2.0f), float2(-2.0f, 0.0f), float2(0.0f, 0.0f), float2(2.0f, 0.0f),
        float2(-2.0f, 2.0f), float2(0.0f, 2.0f), float2(2.0f, 2.0f), float2(-1.0f, -1.0f), float2(1.0f, -1.0f), float2(-1.0f, 1.0f),
        float2(1.0f, 1.0f),
    };
    static const float shares[13] =
    {
        0.03125f, 0.0625f, 0.03125f, 0.0625f, 0.125f, 0.0625f, 0.03125f, 0.0625f, 0.03125f, 0.125f, 0.125f, 0.125f, 0.125f,
    };
    float3 sum = 0.0f;
    float weights = 0.0f;
    [unroll] for (int i = 0; i < 13; i++)
    {
        float3 c = Read(input.uv, offsets[i]);
        float w = shares[i] * Weight(c);
        sum += c * w;
        weights += w;
    }
    return float4(sum / max(weights, 0.0001f), 1.0f);
}

// Twice the size, softly (3 x 3 tent), added to the step below.
float4 PSUp(VS_OUTPUT input) : SV_Target
{
    float3 c = Read(input.uv, float2(0.0f, 0.0f)) * 4.0f
        + (Read(input.uv, float2(-1.0f, 0.0f)) + Read(input.uv, float2(1.0f, 0.0f)) + Read(input.uv, float2(0.0f, -1.0f)) + Read(input.uv, float2(0.0f, 1.0f))) * 2.0f
        + Read(input.uv, float2(-1.0f, -1.0f)) + Read(input.uv, float2(1.0f, -1.0f)) + Read(input.uv, float2(-1.0f, 1.0f)) + Read(input.uv, float2(1.0f, 1.0f));
    return float4(c / 16.0f, 1.0f);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct BloomConstants
    {
        public Vector4 Texel;
    }

    private const int BloomSteps = 6;

    // Smoothing of the edges (TAA): this frame's image blended with the previous result, found again where the camera
    // saw it then (only the camera's movement is followed: what moves by itself is held back by the colours around).
    private const string ResolveSource = @"
cbuffer Constants : register(b0)
{
    float4x4 Reprojection; // Position on this frame's image (without the shift) -> position on the previous one.
    float4 Size;           // xy: size of the images (pixels), zw: 1 / size.
    float4 Jitter;         // xy: shift of this frame (-1 to 1 coordinates), z = 1: previous result usable,
                           // w: share of this frame in the blend.
};
Texture2D Current : register(t0);
Texture2D History : register(t1);
Texture2D<float> Depth : register(t2);
SamplerState LinearClamp : register(s0);

struct VS_OUTPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

// Bright colours squeezed below 1 (reversibly), so one very bright pixel does not flicker.
float3 Compress(float3 c) { return c / (1.0f + max(max(c.r, c.g), c.b)); }
float3 Expand(float3 c) { return c / max(1.0f - max(max(c.r, c.g), c.b), 0.0001f); }

float3 ToYCoCg(float3 c)
{
    return float3(0.25f * c.r + 0.5f * c.g + 0.25f * c.b, 0.5f * c.r - 0.5f * c.b, -0.25f * c.r + 0.5f * c.g - 0.25f * c.b);
}

float3 FromYCoCg(float3 c)
{
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// The previous result at uv, read with a sharp (Catmull-Rom) filter in 5 reads.
float3 SampleHistory(float2 uv)
{
    float2 position = uv * Size.xy;
    float2 center = floor(position - 0.5f) + 0.5f;
    float2 f = position - center;
    float2 w0 = f * (-0.5f + f * (1.0f - 0.5f * f));
    float2 w1 = 1.0f + f * f * (-2.5f + 1.5f * f);
    float2 w2 = f * (0.5f + f * (2.0f - 1.5f * f));
    float2 w3 = f * f * (-0.5f + 0.5f * f);
    float2 w12 = w1 + w2;
    float2 tc12 = (center + w2 / w12) * Size.zw;
    float2 tc0 = (center - 1.0f) * Size.zw;
    float2 tc3 = (center + 2.0f) * Size.zw;
    float3 sum = History.SampleLevel(LinearClamp, float2(tc12.x, tc0.y), 0).rgb * (w12.x * w0.y)
        + History.SampleLevel(LinearClamp, float2(tc0.x, tc12.y), 0).rgb * (w0.x * w12.y)
        + History.SampleLevel(LinearClamp, tc12, 0).rgb * (w12.x * w12.y)
        + History.SampleLevel(LinearClamp, float2(tc3.x, tc12.y), 0).rgb * (w3.x * w12.y)
        + History.SampleLevel(LinearClamp, float2(tc12.x, tc3.y), 0).rgb * (w12.x * w3.y);
    float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return max(sum / weight, 0.0f);
}

float4 PSResolve(VS_OUTPUT input) : SV_Target
{
    int2 p = int2(input.pos.xy);
    int2 last = int2(Size.xy) - 1;
    // Colours around the pixel (mean and spread), and the nearest depth (edges follow the object in front).
    float3 m1 = 0.0f, m2 = 0.0f, center = 0.0f;
    float nearest = 0.0f;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            int2 q = clamp(p + int2(x, y), 0, last);
            float3 c = ToYCoCg(Compress(max(Current.Load(int3(q, 0)).rgb, 0.0f)));
            m1 += c;
            m2 += c * c;
            if (x == 0 && y == 0)
                center = c;
            nearest = max(nearest, Depth.Load(int3(q, 0)));
        }
    }
    m1 /= 9.0f;
    float3 spread = sqrt(max(m2 / 9.0f - m1 * m1, 0.0f));
    float3 low = m1 - 1.25f * spread, high = m1 + 1.25f * spread;

    float3 result = center;
    float2 ndc = (input.uv * 2.0f - 1.0f) * float2(1.0f, -1.0f) - Jitter.xy;
    float4 previous = mul(Reprojection, float4(ndc, nearest, 1.0f));
    if (Jitter.z > 0.5f && previous.w > 0.0f)
    {
        float2 uv = previous.xy / previous.w * float2(0.5f, -0.5f) + 0.5f;
        if (all(uv > 0.0f) && all(uv < 1.0f))
        {
            float3 history = ToYCoCg(Compress(SampleHistory(uv)));
            // Brought back towards the colours around the pixel when it does not match them (something moved).
            float3 offset = history - m1;
            float3 extent = max(high - m1, 0.0001f);
            float3 ratio = abs(offset / extent);
            float largest = max(ratio.x, max(ratio.y, ratio.z));
            if (largest > 1.0f)
                history = m1 + offset / largest;
            // A little more of this frame when the image moves fast: less blur behind moving edges.
            float moved = length((uv - input.uv) * Size.xy);
            float share = lerp(Jitter.w, 0.4f, saturate(moved / 12.0f));
            result = lerp(history, center, share);
        }
    }
    return float4(Expand(FromYCoCg(result)), 1.0f);
}";

    // Ambient occlusion: how much of the sky's light the surroundings hide from each pixel (nooks, the ground under
    // objects, the foot of walls), from the depth and the normals of the solid pass; blurred while keeping the edges,
    // then multiplied into the light coming from the surroundings only (the sun's own light is not darkened).
    private const string OcclusionSource = @"
cbuffer Constants : register(b0)
{
    float4x4 ViewRotation; // World directions -> camera's (rotation only).
    float4 Projection;     // x, y: scale of the projection, z: near plane (m), w: radius of the search (m).
    float4 Size;           // xy: size of the occlusion image, zw: size of the scene images.
    float4 Settings;       // x: strength, y: frame number (turns the samples), z: samples, w: distance where it fades out.
    float4 Direction;      // xy: blur direction (pixels).
};
Texture2D<float> Depth : register(t0);
Texture2D Normals : register(t1);
Texture2D<float> Occlusion : register(t2);
SamplerState LinearClamp : register(s0);

struct VS_OUTPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

// Point seen at uv, in the camera's space (looking along -z), from the reversed depth.
float3 ViewPoint(float2 uv, float depth)
{
    float distance = Projection.z / max(depth, 1e-7f);
    float2 ndc = (uv * 2.0f - 1.0f) * float2(1.0f, -1.0f);
    return float3(ndc.x * distance / Projection.x, ndc.y * distance / Projection.y, -distance);
}

float DepthAt(float2 uv)
{
    return Depth.Load(int3(clamp(uv * Size.zw, 0.0f, Size.zw - 1.0f), 0));
}

float PSOcclusion(VS_OUTPUT input) : SV_Target
{
    float depth = DepthAt(input.uv);
    float4 normal = Normals.Load(int3(clamp(input.uv * Size.zw, 0.0f, Size.zw - 1.0f), 0));
    if (depth <= 0.0f || normal.a <= 0.0f)
        return 1.0f; // Sky, or no surface lit by its surroundings.
    float3 p = ViewPoint(input.uv, depth);
    float3 n = normalize(mul(ViewRotation, float4(normal.xyz * 2.0f - 1.0f, 0.0f)).xyz);
    float3 tangent = normalize(abs(n.z) < 0.9f ? cross(n, float3(0.0f, 0.0f, 1.0f)) : cross(n, float3(1.0f, 0.0f, 0.0f)));
    float3 bitangent = cross(n, tangent);

    // Different turn of the samples for each pixel (and each frame with the smoothing, which blends them).
    float noise = frac(52.9829189f * frac(dot(input.pos.xy + Settings.y * 5.588238f, float2(0.06711056f, 0.00583715f))));
    float angle = noise * 6.2831853f;
    int count = (int)Settings.z;
    float radius = Projection.w;
    float hidden = 0.0f;
    [loop] for (int i = 0; i < count; i++)
    {
        float f = (i + 0.5f) / count;
        float r = sqrt(f);
        float theta = i * 2.3999632f + angle;
        float3 direction = tangent * (cos(theta) * r) + bitangent * (sin(theta) * r) + n * sqrt(1.0f - f);
        float reach = lerp(0.15f, 1.0f, frac(f + noise * 0.61803f));
        float3 s = p + direction * (radius * reach * reach);
        if (s.z >= -Projection.z)
            continue;
        float2 ndc = float2(s.x * Projection.x, s.y * Projection.y) / -s.z;
        float2 uv = ndc * float2(0.5f, -0.5f) + 0.5f;
        if (any(uv < 0.0f) || any(uv > 1.0f))
            continue;
        float sceneDistance = Projection.z / max(DepthAt(uv), 1e-7f);
        float sampleDistance = -s.z;
        // Hidden when the surface seen there is in front of the sample; not by something far in front of it.
        float inFront = sampleDistance - sceneDistance;
        if (inFront > 0.02f * radius)
            hidden += saturate(radius / max(inFront, 0.001f) - 0.5f);
    }
    float occlusion = 1.0f - hidden / max(count, 1);
    float fade = saturate(-p.z / Settings.w);
    return lerp(occlusion, 1.0f, fade * fade);
}

// Blur along Direction, only between pixels at about the same distance (the edges of objects stay sharp).
float PSBlur(VS_OUTPUT input) : SV_Target
{
    float center = Projection.z / max(DepthAt(input.uv), 1e-7f);
    float sum = 0.0f, weights = 0.0f;
    [unroll] for (int i = -4; i <= 4; i++)
    {
        float2 uv = input.uv + Direction.xy * i / Size.xy;
        float distance = Projection.z / max(DepthAt(uv), 1e-7f);
        float w = exp(-i * i / 8.0f) * saturate(1.0f - abs(distance - center) / (0.03f * center + 0.05f));
        sum += Occlusion.SampleLevel(LinearClamp, uv, 0) * w;
        weights += w;
    }
    return sum / max(weights, 0.0001f);
}

// Multiplies the scene's colours: only the share of the light that came from the surroundings is darkened.
float4 PSApply(VS_OUTPUT input) : SV_Target
{
    float occlusion = Occlusion.SampleLevel(LinearClamp, input.uv, 0);
    float share = Normals.Load(int3(input.pos.xy, 0)).a;
    float k = saturate(1.0f - (1.0f - occlusion) * share * Settings.x);
    return float4(k, k, k, 1.0f);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct OcclusionConstants
    {
        public Matrix4x4 ViewRotation;
        public Vector4 Projection;
        public Vector4 Size;
        public Vector4 Settings;
        public Vector4 Direction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResolveConstants
    {
        public Matrix4x4 Reprojection;
        public Vector4 Size;
        public Vector4 Jitter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Source;
        public Vector4 Settings;
        public Vector4 Effects;
    }

    private const Format ColorFormat = Format.R16G16B16A16_Float;
    private const Format NormalFormat = Format.R8G8B8A8_UNorm;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _fullVertexShader;
    private readonly ID3D11PixelShader _compositePixelShader;
    private readonly ID3D11PixelShader _resolvePixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _resolveBuffer;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11DepthStencilState _writeDepth;
    private readonly ID3D11DepthStencilState _noDepth;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11BlendState _multiply;
    private readonly ID3D11BlendState _add;
    private readonly ID3D11PixelShader _bloomDownPixelShader;
    private readonly ID3D11PixelShader _bloomUpPixelShader;
    private readonly ID3D11Buffer _bloomBuffer;
    private readonly Texture?[] _bloom = new Texture?[BloomSteps];
    private bool _bloomShown;
    private readonly ID3D11PixelShader _occlusionPixelShader;
    private readonly ID3D11PixelShader _blurPixelShader;
    private readonly ID3D11PixelShader _applyPixelShader;
    private readonly ID3D11Buffer _occlusionBuffer;
    private Texture? _occlusion;
    private Texture? _occlusionBlur;

    private Texture? _color;
    private Texture? _normals;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11ShaderResourceView? _depthRead;
    private Texture? _depthCopy;
    private Texture? _colorCopy;

    // TAA: the last two results (one read, the other written), the shift of this frame, the camera of this frame and
    // the previous one (rotation and projection only, and position).
    private readonly Texture?[] _history = new Texture?[2];
    private int _historyIndex;
    private bool _historyValid;
    private int _frame;
    private Vector2 _jitter;
    private Matrix4x4 _rotationProjection, _previousRotationProjection;
    private Vector3 _cameraPosition, _previousCameraPosition;
    private bool _cameraSet;

    public SceneImage(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _fullVertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VSFull", "scene", "vs_5_0").Span);
        _compositePixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSComposite", "scene", "ps_5_0").Span);
        _resolvePixelShader = device.CreatePixelShader(Compiler.Compile(ResolveSource, "PSResolve", "resolve", "ps_5_0").Span);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _resolveBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(ResolveConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
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
        _noDepth = device.CreateDepthStencilState(new DepthStencilDescription
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
        _opaque = device.CreateBlendState(BlendDescription.Opaque);
        var multiply = new BlendDescription();
        multiply.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.Zero,
            DestinationBlend = Blend.SourceColor,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _multiply = device.CreateBlendState(multiply);
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
        _bloomDownPixelShader = device.CreatePixelShader(Compiler.Compile(BloomSource, "PSDown", "bloom", "ps_5_0").Span);
        _bloomUpPixelShader = device.CreatePixelShader(Compiler.Compile(BloomSource, "PSUp", "bloom", "ps_5_0").Span);
        _bloomBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(BloomConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _occlusionPixelShader = device.CreatePixelShader(Compiler.Compile(OcclusionSource, "PSOcclusion", "occlusion", "ps_5_0").Span);
        _blurPixelShader = device.CreatePixelShader(Compiler.Compile(OcclusionSource, "PSBlur", "occlusion", "ps_5_0").Span);
        _applyPixelShader = device.CreatePixelShader(Compiler.Compile(OcclusionSource, "PSApply", "occlusion", "ps_5_0").Span);
        _occlusionBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(OcclusionConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
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
        if (options.Antialiasing != Options.Antialiasing || options.Hdr != Options.Hdr)
            _historyValid = false;
        Options = options;
        _shown = null;
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

    // The view-projection to draw this frame's scene with (after Begin): with TAA, shifted by a fraction of a pixel,
    // a different one each frame, so the frames together see more detail than one.
    public Matrix4x4 ViewProjection(Camera camera, float aspectRatio)
    {
        var projection = camera.Projection(aspectRatio);
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, camera.Forward, Vector3.UnitZ);
        _previousRotationProjection = _rotationProjection;
        _previousCameraPosition = _cameraPosition;
        _rotationProjection = rotation * projection;
        _cameraPosition = camera.Position;
        // A jump (teleport, bookmark, other map): the previous frames show something else.
        if (!_cameraSet || Vector3.Distance(_cameraPosition, _previousCameraPosition) > 50f)
            _historyValid = false;
        _cameraSet = true;

        var viewProjection = camera.ViewProjection(aspectRatio);
        if (!Options.Antialiasing)
        {
            _jitter = Vector2.Zero;
            return viewProjection;
        }
        _frame = (_frame + 1) % JitterCount;
        _jitter = new Vector2((Halton(_frame + 1, 2) - 0.5f) * 2f / Width, -(Halton(_frame + 1, 3) - 0.5f) * 2f / Height);
        var shift = Matrix4x4.Identity;
        shift.M41 = _jitter.X;
        shift.M42 = _jitter.Y;
        return viewProjection * shift;
    }

    private const int JitterCount = 8;

    private static float Halton(int index, int radix)
    {
        float result = 0f, fraction = 1f / radix;
        for (; index > 0; index /= radix, fraction /= radix)
            result += fraction * (index % radix);
        return result;
    }

    // Darkens the nooks of the solid models drawn so far (with the normals' image bound, see Bind), then binds the
    // colour image alone again. camera: the camera of ViewProjection.
    public void ApplyOcclusion(Camera camera, float aspectRatio)
    {
        var options = Options;
        if (options.AmbientOcclusion <= 0)
        {
            Bind(normals: false);
            return;
        }
        bool high = options.AmbientOcclusion >= 2;
        int width = high ? Width : Math.Max(1, Width / 2), height = high ? Height : Math.Max(1, Height / 2);
        if (_occlusion is null || _occlusionWidth != width || _occlusionHeight != height)
        {
            _occlusion?.Dispose();
            _occlusionBlur?.Dispose();
            _occlusion = CreateTexture(width, height, Format.R8_UNorm, target: true);
            _occlusionBlur = CreateTexture(width, height, Format.R8_UNorm, target: true);
            (_occlusionWidth, _occlusionHeight) = (width, height);
        }

        var projection = camera.Projection(aspectRatio);
        var constants = new OcclusionConstants
        {
            ViewRotation = Matrix4x4.CreateLookAt(Vector3.Zero, camera.Forward, Vector3.UnitZ),
            Projection = new Vector4(projection.M11, projection.M22, camera.NearPlane, OcclusionRadius),
            Size = new Vector4(width, height, Width, Height),
            Settings = new Vector4(Math.Clamp(options.OcclusionStrength, 0f, 2f), options.Antialiasing ? _frame : 0f, high ? 16f : 10f, OcclusionDistance),
        };
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullVertexShader);
        _context.GSSetShader(null);
        _context.PSSetSampler(0, _linearClamp);
        _context.OMSetDepthStencilState(_noDepth, 0);
        _context.RSSetState(_rasterizerState);
        _context.PSSetShaderResource(0, _depthRead!);
        _context.PSSetShaderResource(1, _normals!.Read);

        // Occlusion, then blurred across and down.
        WriteOcclusion(constants);
        _context.OMSetBlendState(_opaque, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetRenderTargets(_occlusion.Write!);
        _context.RSSetViewport(new Viewport(0, 0, width, height));
        _context.PSSetShader(_occlusionPixelShader);
        _context.Draw(3, 0);

        _context.PSSetShader(_blurPixelShader);
        foreach (var (from, to, direction) in new[] { (_occlusion, _occlusionBlur!, Vector2.UnitX), (_occlusionBlur!, _occlusion, Vector2.UnitY) })
        {
            WriteOcclusion(constants with { Direction = new Vector4(direction, 0f, 0f) });
            _context.OMSetRenderTargets(to.Write!);
            _context.PSSetShaderResource(2, from.Read);
            _context.Draw(3, 0);
            _context.PSUnsetShaderResource(2);
        }

        // Into the colours.
        _context.OMSetRenderTargets(_color!.Write!);
        _context.RSSetViewport(new Viewport(0, 0, Width, Height));
        _context.PSSetShaderResource(2, _occlusion.Read);
        _context.PSSetShader(_applyPixelShader);
        _context.OMSetBlendState(_multiply, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.Draw(3, 0);
        for (uint slot = 0; slot < 3; slot++)
            _context.PSUnsetShaderResource(slot);
        Bind(normals: false);
    }

    private int _occlusionWidth, _occlusionHeight;

    // Metres around a point where something can hide the sky from it; distance beyond which the occlusion fades out.
    private const float OcclusionRadius = 1.6f;
    private const float OcclusionDistance = 300f;

    private void WriteOcclusion(OcclusionConstants constants)
    {
        var map = _context.Map(_occlusionBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_occlusionBuffer, 0);
        _context.PSSetConstantBuffer(0, _occlusionBuffer);
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

    // Finishes the image once the scene is drawn (smoothing), before Show.
    public void Process()
    {
        _shown = Options.Antialiasing ? Resolve() : _color;
        _bloomShown = Options.Hdr && Options.Bloom > 0f && Width >= 64 && Height >= 64;
        if (_bloomShown)
            Glow(_shown!);
    }

    // Glow: the image shrunk step by step, then enlarged back with each step added (the result is in _bloom[0]).
    private void Glow(Texture source)
    {
        int width = Width, height = Height;
        for (int i = 0; i < BloomSteps; i++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            if (_bloom[i] is null || _bloomSizes[i] != (width, height))
            {
                _bloom[i]?.Dispose();
                _bloom[i] = CreateTexture(width, height, Format.R11G11B10_Float, target: true);
                _bloomSizes[i] = (width, height);
            }
        }
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullVertexShader);
        _context.GSSetShader(null);
        _context.PSSetSampler(0, _linearClamp);
        _context.OMSetDepthStencilState(_noDepth, 0);
        _context.RSSetState(_rasterizerState);
        _context.PSSetConstantBuffer(0, _bloomBuffer);

        _context.PSSetShader(_bloomDownPixelShader);
        _context.OMSetBlendState(_opaque, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        var from = source;
        int fromWidth = Width, fromHeight = Height;
        for (int i = 0; i < BloomSteps; i++)
        {
            var to = _bloom[i]!;
            WriteBloom(new Vector4(1f / fromWidth, 1f / fromHeight, i == 0 ? 1f : 0f, 0f));
            _context.OMSetRenderTargets(to.Write!);
            _context.RSSetViewport(new Viewport(0, 0, _bloomSizes[i].Width, _bloomSizes[i].Height));
            _context.PSSetShaderResource(0, from.Read);
            _context.Draw(3, 0);
            _context.PSUnsetShaderResource(0);
            from = to;
            (fromWidth, fromHeight) = _bloomSizes[i];
        }

        _context.PSSetShader(_bloomUpPixelShader);
        _context.OMSetBlendState(_add, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        for (int i = BloomSteps - 2; i >= 0; i--)
        {
            var (smallWidth, smallHeight) = _bloomSizes[i + 1];
            WriteBloom(new Vector4(1f / smallWidth, 1f / smallHeight, 0f, 0f));
            _context.OMSetRenderTargets(_bloom[i]!.Write!);
            _context.RSSetViewport(new Viewport(0, 0, _bloomSizes[i].Width, _bloomSizes[i].Height));
            _context.PSSetShaderResource(0, _bloom[i + 1]!.Read);
            _context.Draw(3, 0);
            _context.PSUnsetShaderResource(0);
        }
    }

    private readonly (int Width, int Height)[] _bloomSizes = new (int, int)[BloomSteps];

    private void WriteBloom(Vector4 texel)
    {
        var map = _context.Map(_bloomBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, new BloomConstants { Texel = texel });
        _context.Unmap(_bloomBuffer, 0);
    }

    private Texture? _shown;

    // Draws the finished image into the render target and viewport bound by the caller (the window's 3D view), with
    // its depth.
    public void Show()
    {
        var options = Options;
        var source = _shown ?? _color!;
        WriteConstants(new Constants
        {
            Source = new Vector4(Width, Height, 1f / Width, 1f / Height),
            Settings = new Vector4(MathF.Max(options.Exposure, 0.01f) * MathF.Max(options.Adaptation, 1f) * Exposure, options.Hdr ? 1f : 0f,
                Math.Clamp(options.Sharpness, 0f, 1f), Saturation),
            // The steps added together hold about BloomSteps times the light.
            Effects = new Vector4(_bloomShown ? Math.Clamp(options.Bloom, 0f, 1f) * MaxGlow / BloomSteps : 0f, 0f, 0f, 0f),
        });
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullVertexShader);
        _context.GSSetShader(null);
        _context.PSSetShader(_compositePixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetShaderResource(0, source.Read);
        _context.PSSetShaderResource(1, _depthRead!);
        if (_bloomShown)
            _context.PSSetShaderResource(2, _bloom[0]!.Read);
        _context.OMSetBlendState(_opaque, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_writeDepth, 0);
        _context.RSSetState(_rasterizerState);
        _context.Draw(3, 0);
        _context.PSUnsetShaderResource(0);
        _context.PSUnsetShaderResource(1);
        _context.PSUnsetShaderResource(2);
    }

    // Share of the glow in the image at full strength.
    private const float MaxGlow = 0.2f;

    // TAA: blends this frame into the previous result; returns the new result (kept for the next frame).
    private Texture Resolve()
    {
        _history[0] ??= CreateTexture(Width, Height, ColorFormat, target: true);
        _history[1] ??= CreateTexture(Width, Height, ColorFormat, target: true);
        var previous = _history[_historyIndex]!;
        _historyIndex ^= 1;
        var next = _history[_historyIndex]!;

        // This frame's position without the shift -> camera-relative point -> the previous camera's image.
        Matrix4x4.Invert(_rotationProjection, out var unproject);
        var reprojection = unproject * Matrix4x4.CreateTranslation(_cameraPosition - _previousCameraPosition) * _previousRotationProjection;
        var constants = new ResolveConstants
        {
            Reprojection = reprojection,
            Size = new Vector4(Width, Height, 1f / Width, 1f / Height),
            Jitter = new Vector4(_jitter, _historyValid ? 1f : 0f, 0.1f),
        };
        var map = _context.Map(_resolveBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_resolveBuffer, 0);

        _context.OMSetRenderTargets(next.Write!);
        _context.RSSetViewport(new Viewport(0, 0, Width, Height));
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_fullVertexShader);
        _context.GSSetShader(null);
        _context.PSSetShader(_resolvePixelShader);
        _context.PSSetConstantBuffer(0, _resolveBuffer);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetShaderResource(0, _color!.Read);
        _context.PSSetShaderResource(1, previous.Read);
        _context.PSSetShaderResource(2, _depthRead!);
        _context.OMSetBlendState(_opaque, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_noDepth, 0);
        _context.RSSetState(_rasterizerState);
        _context.Draw(3, 0);
        _context.PSUnsetShaderResource(0);
        _context.PSUnsetShaderResource(1);
        _context.PSUnsetShaderResource(2);
        _historyValid = true;
        return next;
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
        for (int i = 0; i < _history.Length; i++)
        {
            _history[i]?.Dispose();
            _history[i] = null;
        }
        _occlusion?.Dispose();
        _occlusion = null;
        _occlusionBlur?.Dispose();
        _occlusionBlur = null;
        for (int i = 0; i < _bloom.Length; i++)
        {
            _bloom[i]?.Dispose();
            _bloom[i] = null;
        }
        _historyValid = false;
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
        _noDepth.Dispose();
        _linearClamp.Dispose();
        _constantBuffer.Dispose();
        _resolveBuffer.Dispose();
        _multiply.Dispose();
        _add.Dispose();
        _bloomDownPixelShader.Dispose();
        _bloomUpPixelShader.Dispose();
        _bloomBuffer.Dispose();
        _occlusionPixelShader.Dispose();
        _blurPixelShader.Dispose();
        _applyPixelShader.Dispose();
        _occlusionBuffer.Dispose();
        _resolvePixelShader.Dispose();
        _compositePixelShader.Dispose();
        _fullVertexShader.Dispose();
    }
}
