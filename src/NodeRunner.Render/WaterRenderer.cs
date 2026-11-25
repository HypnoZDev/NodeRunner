using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NodeRunner.Data.Models;
using NodeRunner.Data.Water;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Water surfaces. The swell of the open sea follows the game's water file: smaller in its calm zones (harbours,
// marinas), with waves rolling in towards the shore in its wave zones. With 3D waves, the open water is a grid around
// the camera whose points move with the waves (kept to where the map has open water); the other surfaces (lakes,
// rivers, pools) stay flat, their look computed per pixel. With the game's patterns (GTA V), its swell, ripples and
// foam are used; otherwise sums of simple waves.
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
    float4 Settings;     // x: 1 = fog on, y: fog distance, z: 1 = scene depth available, w: 1 = scene colour available.
    float4 Viewport;     // xy: top-left pixel of the 3D view, zw: its size.
    float4 Options;      // x = 1: linear colours of an HDR image, y = 1: the open water moves (3D waves), z: its height,
                         // w = 1: ray-traced reflections (RayReflection).
    float4 WaveArea;     // xy: south-west corner of the image of the wave zones, zw: 1 / its size (m); z = 0: none.
    float4 MaskArea;     // The same for the image of where the open water is inside the map.
    float4 OceanBounds;  // The map's water file (min x, min y, max x, max y): open ocean all around; empty: none.
    float4 GridCenter;   // xy: centre of the grid of the open water.
    float4 ColorArea;    // xy: north-west corner of the game's water colour image, zw: 1 / its size (m, y negative); z = 0: none.
    float4 IslandArea;   // The same for Cayo Perico's image.
    float4 Caustics;     // x = 1: light pattern on the bottom available.
    float4 Patterns;     // x = 1: the game's wave patterns (swell, waves along the shore, ripples, foam) available.
};
// Water meshes of the models (pools, fountains, streams): placement and wave strength of the model drawn.
cbuffer MeshConstants : register(b1)
{
    float4x4 World;
    float4 MeshWaves; // x: wave strength.
};
Texture2D<float> SceneDepth : register(t0);
Texture2D SceneColor : register(t1);
Texture2D WaveTexture : register(t2);
Texture2D<float> WaterMask : register(t3);
Texture2D WaterColors : register(t4);
Texture2D IslandColors : register(t5);
Texture2D<float> CausticPattern : register(t6);
Texture2D<float> OceanWaves : register(t7);
Texture2D<float> ShoreWaves : register(t8);
Texture2D<float2> RipplePattern : register(t9);
Texture2D<float> FoamPattern : register(t10);
Texture2D RayReflection : register(t11);
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap : register(s1);

struct VS_INPUT { float3 pos : POSITION; float waves : TEXCOORD0; };
struct VS_MESH_INPUT { float3 pos : POSITION; float3 normal : NORMAL; };
struct VS_GRID_INPUT { float2 offset : POSITION; float spacing : TEXCOORD0; };
// world: the surface where it is drawn; rest: where it would be without the waves. kind.x = 1: open water's grid
// (only where the map has open water), kind.y = 1: swell of the open water.
struct PS_INPUT
{
    float4 pos : SV_POSITION; float3 world : TEXCOORD0; float waves : TEXCOORD1; float3 up : TEXCOORD2; float3 rest : TEXCOORD3;
    float2 kind : TEXCOORD4;
};

// The wave zones at a point: how calm (1 = open sea), size and way of the waves rolling in.
struct WaveState { float calm; float amplitude; float2 direction; };

WaveState WaveAt(float2 p)
{
    WaveState w;
    w.calm = 1.0f;
    w.amplitude = 0.0f;
    w.direction = float2(1.0f, 0.0f);
    if (WaveArea.z > 0.0f)
    {
        float2 uv = (p - WaveArea.xy) * WaveArea.zw;
        if (all(uv >= 0.0f) && all(uv <= 1.0f))
        {
            float4 t = WaveTexture.SampleLevel(LinearClamp, uv, 0);
            w.calm = t.r;
            w.amplitude = t.g * 2.5f;
            float2 d = t.ba * 2.0f - 1.0f;
            w.direction = dot(d, d) > 1e-4f ? normalize(d) : float2(1.0f, 0.0f);
        }
    }
    return w;
}

// One wave of the sum (Gerstner): its displacement of the point p, and how it changes along x and y (for the normal).
// steepness: how pointed its crests are (horizontal move times the wave number).
void AddWave(float2 p, float2 d, float length, float height, float steepness, inout float3 offset, inout float3 tx, inout float3 ty)
{
    float k = 6.2831853f / length;
    float phase = k * dot(d, p) - sqrt(9.81f * k) * CameraTime.w;
    float s, c;
    sincos(phase, s, c);
    offset += float3(d * (steepness / k) * c, height * s);
    tx += float3(-d.x * d.x * steepness * s, -d.x * d.y * steepness * s, d.x * k * height * c);
    ty += float3(-d.x * d.y * steepness * s, -d.y * d.y * steepness * s, d.y * k * height * c);
}

// Moves a pattern a whole number of times per hour (cycles), so the hourly wrap of the time does not show.
float Drift(float cycles)
{
    return CameraTime.w * cycles / 3600.0f;
}

// Mip level of a pattern of 128 texels over tile metres, seen with spacing metres between samples.
float PatternLevel(float tile, float spacing)
{
    return log2(max(spacing * 128.0f / tile, 1.0f));
}

// Height of the open sea from the game's swell pattern: three sizes drifting different ways, so its shape keeps
// changing. Direction (degrees), tile (m), cycles per hour, height (m).
static const float4 SwellLayers[3] =
{
    float4(20.0f, 150.0f, 96.0f, 0.85f), float4(62.0f, 61.0f, 150.0f, 0.32f), float4(-28.0f, 23.0f, 250.0f, 0.12f),
};

float SwellHeight(float2 p, float spacing)
{
    float h = 0.0f;
    [unroll] for (int i = 0; i < 3; i++)
    {
        float angle = radians(SwellLayers[i].x);
        float2 d = float2(cos(angle), sin(angle));
        float2 uv = float2(dot(p, d), dot(p, float2(-d.y, d.x))) / SwellLayers[i].y - float2(Drift(SwellLayers[i].z), 0.0f);
        h += (OceanWaves.SampleLevel(LinearWrap, uv, PatternLevel(SwellLayers[i].y, spacing)) - 0.5f) * 2.0f * SwellLayers[i].w;
    }
    return h;
}

// How big the waves rolling in are along their crest, from the game's pattern (0.75 to 1.5): they break unevenly.
float ShoreStrength(float2 p, float2 direction, float spacing)
{
    float2 side = float2(-direction.y, direction.x);
    float2 uv = float2(dot(p, side) / 70.0f, dot(p, direction) / 350.0f + Drift(12.0f));
    float s = ShoreWaves.SampleLevel(LinearWrap, uv, PatternLevel(70.0f, spacing));
    return 0.75f + 0.75f * saturate((s - 0.42f) / 0.58f);
}

// The open water's swell at p: waves of the open sea (smaller in calm zones) and waves rolling in towards the shore.
// Waves too small for spacing (m between the points that show them) fade out instead of flickering. shore: crest of
// the waves rolling in (0 to 1), for their foam.
void Swell(float2 p, WaveState w, float strength, float spacing, out float3 offset, out float3 tx, out float3 ty, out float shore)
{
    if (Patterns.x > 0.5f)
    {
        float sea = strength * w.calm;
        float e = max(spacing, 0.25f);
        float h = SwellHeight(p, spacing);
        float2 slope = float2(SwellHeight(p + float2(e, 0.0f), spacing) - h, SwellHeight(p + float2(0.0f, e), spacing) - h) / e;
        offset = float3(0.0f, 0.0f, h * sea);
        tx = float3(0.0f, 0.0f, slope.x * sea);
        ty = float3(0.0f, 0.0f, slope.y * sea);
        shore = 0.0f;
        float rolling = strength * w.amplitude;
        if (rolling > 0.01f)
        {
            rolling *= ShoreStrength(p, w.direction, spacing);
            float2 side = float2(-w.direction.y, w.direction.x);
            float fade1 = saturate(22.0f / (4.0f * spacing) - 0.5f), fade2 = saturate(12.0f / (4.0f * spacing) - 0.5f);
            float sharp = saturate(rolling);
            AddWave(p, w.direction, 22.0f, 0.28f * rolling * fade1, 0.45f * sharp * fade1, offset, tx, ty);
            AddWave(p, normalize(w.direction + side * 0.25f), 12.0f, 0.1f * rolling * fade2, 0.3f * sharp * fade2, offset, tx, ty);
            float k = 6.2831853f / 22.0f;
            shore = saturate(sin(k * dot(w.direction, p) - sqrt(9.81f * k) * CameraTime.w) * 4.0f - 3.0f) * saturate(rolling - 0.3f);
        }
        return;
    }
    // Direction (degrees), length (m), height (m), steepness.
    static const float4 waves[6] =
    {
        float4(20.0f, 64.0f, 0.30f, 0.30f), float4(-12.0f, 41.0f, 0.19f, 0.28f), float4(48.0f, 26.0f, 0.11f, 0.24f),
        float4(-38.0f, 16.0f, 0.065f, 0.2f), float4(75.0f, 10.0f, 0.038f, 0.16f), float4(-3.0f, 6.5f, 0.022f, 0.12f),
    };
    offset = 0.0f;
    tx = 0.0f;
    ty = 0.0f;
    shore = 0.0f;
    float sea = strength * w.calm;
    [unroll] for (int i = 0; i < 6; i++)
    {
        float angle = radians(waves[i].x);
        float fade = saturate(waves[i].y / (4.0f * spacing) - 0.5f) * sea;
        AddWave(p, float2(cos(angle), sin(angle)), waves[i].y, waves[i].z * fade, waves[i].w * fade, offset, tx, ty);
    }
    float rolling = strength * w.amplitude;
    if (rolling > 0.01f)
    {
        float2 side = float2(-w.direction.y, w.direction.x);
        float fade1 = saturate(22.0f / (4.0f * spacing) - 0.5f), fade2 = saturate(12.0f / (4.0f * spacing) - 0.5f);
        float sharp = saturate(rolling);
        AddWave(p, w.direction, 22.0f, 0.28f * rolling * fade1, 0.45f * sharp * fade1, offset, tx, ty);
        AddWave(p, normalize(w.direction + side * 0.25f), 12.0f, 0.1f * rolling * fade2, 0.3f * sharp * fade2, offset, tx, ty);
        float k = 6.2831853f / 22.0f;
        shore = saturate(sin(k * dot(w.direction, p) - sqrt(9.81f * k) * CameraTime.w) * 4.0f - 3.0f) * saturate(rolling - 0.3f);
    }
}

// Inside the map, open water only where its file has a surface at the sea's height; open ocean all around.
bool IsOpenWater(float2 p)
{
    if (OceanBounds.z > OceanBounds.x && (any(p < OceanBounds.xy) || any(p > OceanBounds.zw)))
        return true;
    if (MaskArea.z <= 0.0f)
        return false;
    float2 uv = (p - MaskArea.xy) * MaskArea.zw;
    if (any(uv < 0.0f) || any(uv > 1.0f))
        return false;
    return WaterMask.SampleLevel(LinearClamp, uv, 0) > 0.5f;
}

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(ViewProjection, float4(input.pos, 1.0f));
    output.world = input.pos;
    output.rest = input.pos;
    output.waves = input.waves;
    output.up = float3(0.0f, 0.0f, 1.0f);
    output.kind = float2(0.0f, 1.0f);
    return output;
}

// The open water's grid around the camera, moved by the swell (less and less far away).
PS_INPUT VSGrid(VS_GRID_INPUT input)
{
    PS_INPUT output;
    float3 rest = float3(GridCenter.xy + input.offset, Options.z);
    float3 world = rest;
    float distance = length(rest - CameraTime.xyz);
    float reach = saturate(1.0f - distance / 6000.0f);
    if (reach > 0.0f)
    {
        float3 offset, tx, ty;
        float shore;
        Swell(rest.xy, WaveAt(rest.xy), 1.0f, input.spacing, offset, tx, ty, shore);
        world += offset * reach;
    }
    output.pos = mul(ViewProjection, float4(world, 1.0f));
    output.world = world;
    output.rest = rest;
    output.waves = 1.0f;
    output.up = float3(0.0f, 0.0f, 1.0f);
    output.kind = float2(1.0f, 1.0f);
    return output;
}

PS_INPUT VSMesh(VS_MESH_INPUT input)
{
    PS_INPUT output;
    float4 world = mul(World, float4(input.pos, 1.0f));
    output.pos = mul(ViewProjection, world);
    output.world = world.xyz;
    output.rest = world.xyz;
    output.waves = MeshWaves.x;
    output.up = mul((float3x3)World, input.normal);
    output.kind = float2(0.0f, 0.0f);
    return output;
}

// Slope of small ripples over the swell: deep-water waves (speed from their length), around a main wind direction.
// Waves much smaller than a pixel fade out instead of flickering.
float2 WaveSlope(float2 p, float t, float footprint, int first)
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
        if (i < first)
            continue;
        float angle = radians(waves[i].x);
        float2 d = float2(cos(angle), sin(angle));
        float k = 6.2831853f / waves[i].y;
        float phase = k * dot(d, p) - sqrt(9.81f * k) * t + i * 1.7f;
        float fade = saturate(waves[i].y / (6.0f * footprint) - 0.5f);
        slope += d * waves[i].z * cos(phase) * fade;
    }
    return slope;
}

// Random value (0 to 1) of a cell, without sines: precise even far from the map's centre.
float Hash(float2 cell)
{
    float3 p = frac(float3(cell.xyx) * 0.1031f);
    p += dot(p, p.yzx + 33.33f);
    return frac((p.x + p.y) * p.z);
}

// Soft random pattern (0 to 1), for the foam; repeats every kilometre.
float Noise(float2 p)
{
    p -= floor(p / 1024.0f) * 1024.0f;
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0f - 2.0f * f);
    return lerp(lerp(Hash(i), Hash(i + float2(1.0f, 0.0f)), f.x), lerp(Hash(i + float2(0.0f, 1.0f)), Hash(i + float2(1.0f, 1.0f)), f.x), f.y);
}

// Colour of deep water at p from the game's images (Cayo Perico's wins inside its area, blended at its edges).
float3 WaterTint(float2 p)
{
    float3 tint = WaterColors.SampleLevel(LinearClamp, (p - ColorArea.xy) * ColorArea.zw, 0).rgb;
    if (IslandArea.z != 0.0f)
    {
        float2 uv = (p - IslandArea.xy) * IslandArea.zw;
        float2 inside = saturate(min(uv, 1.0f - uv) * 12.0f);
        tint = lerp(tint, IslandColors.SampleLevel(LinearClamp, uv, 0).rgb, inside.x * inside.y);
    }
    return tint;
}

// Light gathered on the bottom under shallow water by the waves (two drifting layers of the game's pattern).
float CausticLight(float2 p, float t)
{
    float a = CausticPattern.SampleLevel(LinearWrap, p * 0.24f + float2(t * 0.021f, t * 0.013f), 0);
    float b = CausticPattern.SampleLevel(LinearWrap, float2(p.y, -p.x) * 0.19f - float2(t * 0.017f, -t * 0.011f), 0);
    return (a + b) * 0.5f;
}

float3 SkyColor(float3 ray)
{
    float3 horizon = Horizon.rgb, zenith = Zenith.rgb;
    if (Options.x > 0.5f)
        return pow(lerp(pow(abs(horizon), 1.0f / 2.2f), pow(abs(zenith), 1.0f / 2.2f), pow(saturate(ray.z), 0.45f)), 2.2f);
    return lerp(horizon, zenith, pow(saturate(ray.z), 0.45f));
}

// How far behind what is drawn a point is (m along the view, > 0 = hidden), and the pixel it falls on.
// Returns -1e9 off screen or where only the sky is drawn.
float Behind(float3 p, out int2 pixel, out float2 ndc)
{
    float4 clip = mul(ViewProjection, float4(p, 1.0f));
    pixel = int2(0, 0);
    ndc = float2(2.0f, 2.0f);
    if (clip.w <= SunDirection.w)
        return -1e9f;
    ndc = clip.xy / clip.w;
    if (abs(ndc.x) >= 1.0f || abs(ndc.y) >= 1.0f)
        return -1e9f;
    pixel = int2(Viewport.xy + (ndc * float2(0.5f, -0.5f) + 0.5f) * Viewport.zw);
    float depth = SceneDepth.Load(int3(pixel, 0));
    return depth > 0.0f ? clip.w - SunDirection.w / depth : -1e9f;
}

// What the water mirrors among the things drawn on screen (buildings, bridges, boats): the reflected ray is followed
// in steps that grow with the distance until it goes behind something. a: how much to trust it (0 = the sky only).
float4 ScreenReflection(float3 start, float3 r, float distance)
{
    float step = max(0.3f, distance * 0.008f);
    float3 previous = start;
    float3 p = start;
    [loop] for (int i = 0; i < 32; i++)
    {
        previous = p;
        p += r * step;
        int2 pixel;
        float2 ndc;
        float behind = Behind(p, pixel, ndc);
        if (abs(ndc.x) >= 1.0f || abs(ndc.y) >= 1.0f)
            return 0.0f; // Off screen (or behind the camera): it does not come back.
        if (behind > 0.0f && behind < step * 3.0f + 2.0f)
        {
            // Closer to the surface hit, by halves.
            float3 a = previous, b = p;
            [unroll] for (int j = 0; j < 5; j++)
            {
                float3 m = 0.5f * (a + b);
                int2 mp;
                float2 mn;
                if (Behind(m, mp, mn) > 0.0f)
                {
                    b = m;
                    pixel = mp;
                    ndc = mn;
                }
                else
                {
                    a = m;
                }
            }
            // Faded near the edges of the view, where the next pixel would be missing.
            float2 edge = saturate((1.0f - abs(ndc)) * 6.0f);
            return float4(SceneColor.Load(int3(pixel, 0)).rgb, edge.x * edge.y);
        }
        step *= 1.2f;
    }
    return 0.0f;
}

// The surface's normal: the swell's where the point came from, small ripples over it. squeeze (below 1 where the
// surface is pressed together), height, shore and calm for the foam and the light through the crests.
float3 SurfaceNormal(PS_INPUT input, float3 up, float footprint, out float squeeze, out float height, out float shore, out float calm)
{
    float3 n = up;
    squeeze = 1.0f;
    height = 0.0f;
    shore = 0.0f;
    calm = 1.0f;
    if (input.kind.y > 0.5f)
    {
        WaveState w = WaveAt(input.rest.xy);
        float3 offset, tx, ty;
        Swell(input.rest.xy, w, input.waves, footprint, offset, tx, ty, shore);
        float3 dx = float3(1.0f, 0.0f, 0.0f) + tx, dy = float3(0.0f, 1.0f, 0.0f) + ty;
        n = normalize(cross(dx, dy));
        squeeze = dx.x * dy.y - dx.y * dy.x; // Below 1 on the crests, where the surface is pressed together.
        height = offset.z;
        calm = lerp(0.35f, 1.0f, w.calm);
        // The game's swell only moves up and down: its highest crests whiten instead.
        if (Patterns.x > 0.5f)
            squeeze -= saturate((height - 0.4f) / 0.4f) * 0.6f;
    }
    // Over the swell, only the short ripples (the long ones are part of it): the game's ripple pattern at two sizes.
    float2 ripples;
    if (Patterns.x > 0.5f)
    {
        float2 p = input.rest.xy;
        float2 a = RipplePattern.SampleLevel(LinearWrap, p / 9.0f + float2(Drift(140.0f), Drift(50.0f)), PatternLevel(9.0f, footprint)) * 2.0f - 1.0f;
        float2 b = RipplePattern.SampleLevel(LinearWrap, float2(p.y, -p.x) / 3.4f - float2(Drift(230.0f), Drift(-120.0f)), PatternLevel(3.4f, footprint)) * 2.0f - 1.0f;
        ripples = (a * 0.35f + b * 0.25f) * input.waves * calm;
    }
    else
    {
        ripples = WaveSlope(input.rest.xy, CameraTime.w, footprint, input.kind.y > 0.5f ? 3 : 0) * input.waves * calm;
    }
    return normalize(n + float3(-ripples, 0.0f) * max(n.z, 0.2f));
}

float3 SurfaceUp(PS_INPUT input)
{
    float len = length(input.up);
    return len > 0.0001f ? input.up / len : float3(0.0f, 0.0f, 1.0f);
}

// For the ray-traced reflections: where each pixel's reflected ray leaves the water (from the camera, w = 1), and its
// direction.
struct RAY_STARTS { float4 start : SV_Target0; float4 direction : SV_Target1; };

RAY_STARTS PSRays(PS_INPUT input)
{
    if (input.kind.x > 0.5f && !IsOpenWater(input.rest.xy))
        discard;
    // Hidden behind something drawn (reversed Z), or seen from below.
    if (SceneDepth.Load(int3(input.pos.xy, 0)) >= input.pos.z)
        discard;
    float3 toCamera = CameraTime.xyz - input.world;
    float3 up = SurfaceUp(input);
    if (dot(toCamera, up) < 0.0f)
        discard;
    float squeeze, height, shore, calm;
    float3 n = SurfaceNormal(input, up, max(length(fwidth(input.rest.xy)), 0.001f), squeeze, height, shore, calm);
    float3 r = reflect(-normalize(toCamera), n);
    r.z = abs(r.z);
    RAY_STARTS o;
    o.start = float4(input.world - CameraTime.xyz, 1.0f);
    o.direction = float4(r, 0.0f);
    return o;
}

float4 PS(PS_INPUT input) : SV_Target
{
    if (input.kind.x > 0.5f && !IsOpenWater(input.rest.xy))
        discard;
    float3 camera = CameraTime.xyz;
    float3 toCamera = camera - input.world;
    float distance = length(toCamera);
    float3 v = toCamera / max(distance, 0.001f);
    float3 up = SurfaceUp(input);
    bool below = dot(toCamera, up) < 0.0f;
    bool hdr = Options.x > 0.5f;

    float footprint = max(length(fwidth(input.rest.xy)), 0.001f);
    float squeeze, height, shore, calm;
    float3 n = SurfaceNormal(input, up, footprint, squeeze, height, shore, calm);
    float2 slope = -n.xy / max(n.z, 0.2f);

    // Light received by the water body: sky all around, plus the sun from above.
    float3 light = SkyAmbient.rgb * 1.3f + SunColor.rgb * saturate(SunDirection.z) * 0.9f;
    float3 deepColor = float3(0.012f, 0.060f, 0.080f), shallowColor = float3(0.050f, 0.190f, 0.180f);
    // Light lost per metre of water crossed. On the map's water, the game's colour for the place, and the water as
    // clear as it says: brownish = muddy (lakes, rivers); red goes first in clear water, blue in muddy water.
    bool tinted = input.kind.y > 0.5f && ColorArea.z != 0.0f;
    float3 absorb = 0.30f;
    if (tinted)
    {
        float3 tint = WaterTint(input.rest.xy);
        float murk = saturate((tint.r - tint.b) * 12.0f + 0.6f);
        absorb = lerp(0.09f, 0.45f, murk) * (1.0f + (1.0f - tint / max(max(tint.r, tint.g), max(tint.b, 0.02f))) * 1.5f);
        deepColor = tint * 0.6f;
    }
    if (hdr)
    {
        deepColor = pow(deepColor, 2.2f) * 2.0f;
        shallowColor = pow(shallowColor, 2.2f) * 2.0f;
    }
    float3 deep = deepColor * light;
    float3 shallow = shallowColor * light;

    if (below)
        return float4(deep * 1.5f, 0.85f);

    // Water thickness along the view ray, from the depth of what is behind (reversed Z: depth = near / view depth).
    float thickness = 1000.0f;
    float behindRatio = 1e4f; // Distance to what is behind the water, over the distance to the water.
    if (Settings.z > 0.5f)
    {
        float sceneDepth = SceneDepth.Load(int3(input.pos.xy, 0));
        float near = SunDirection.w;
        float waterView = near / max(input.pos.z, 1e-9f);
        float sceneView = sceneDepth > 0.0f ? near / sceneDepth : 1e7f;
        behindRatio = sceneView / max(waterView, 0.001f);
        thickness = max(sceneView - waterView, 0.0f) * distance / max(waterView, 0.001f);
    }
    float vertical = thickness * abs(dot(v, up)); // Depth below the surface, roughly.
    float edge = saturate(vertical * 4.0f);

    // See-through when shallow, then the water's own colour.
    float3 transmit = exp(-thickness * absorb);
    float opacity = 1.0f - dot(transmit, 1.0f / 3.0f);
    float3 body = tinted ? deep : lerp(shallow, deep, 1.0f - exp(-thickness * 0.12f));
    // Sunlight passing through the crests of the waves, seen against the sun: a lighter green-blue.
    float3 through = (hdr ? float3(0.02f, 0.12f, 0.1f) : float3(0.1f, 0.3f, 0.28f)) * SunColor.rgb
        * pow(saturate(dot(-v, SunDirection.xyz) * 0.5f + 0.5f), 4.0f) * saturate(height * 1.5f + 0.2f);
    body += through * (1.0f - edge * 0.3f);

    // Reflection of the sky, stronger at grazing angles; the sun or moon glints on the waves.
    float fresnel = (0.02f + 0.98f * pow(1.0f - saturate(dot(n, v)), 5.0f)) * edge;
    float3 r = reflect(-v, n);
    r.z = abs(r.z);
    float3 reflection = SkyColor(r);
    bool detailed = Settings.z > 0.5f && Settings.w > 0.5f;
    if (Options.w > 0.5f)
    {
        float4 traced = RayReflection.Load(int3(input.pos.xy - Viewport.xy, 0));
        reflection = lerp(reflection, traced.rgb, traced.a);
    }
    else if (detailed)
    {
        float4 mirrored = ScreenReflection(input.world, r, distance);
        reflection = lerp(reflection, mirrored.rgb, mirrored.a);
    }
    float facing = saturate(dot(r, SunDirection.xyz));
    float3 glint = SunColor.rgb * (pow(facing, 900.0f) * (2.0f + 10.0f * SunColor.a) + pow(facing, 60.0f) * 0.25f);

    // Foam: where the water meets the shore, on the crests pressed together, on the waves rolling in; broken up.
    float pattern;
    if (Patterns.x > 0.5f)
        pattern = FoamPattern.SampleLevel(LinearWrap, input.rest.xy / 20.0f + Drift(60.0f), PatternLevel(20.0f * 8.0f, footprint)) * 0.6f
            + FoamPattern.SampleLevel(LinearWrap, input.rest.xy / 7.5f - Drift(90.0f), PatternLevel(7.5f * 8.0f, footprint)) * 0.4f;
    else
        pattern = Noise(input.rest.xy * 0.9f + CameraTime.w * 0.15f) * 0.6f + Noise(input.rest.xy * 3.1f - CameraTime.w * 0.2f) * 0.4f;
    float foam = (1.0f - smoothstep(0.05f, 0.7f, vertical)) * edge * saturate(0.5f + 3.0f * (slope.x + slope.y)) * input.waves;
    foam += saturate((0.6f - squeeze) * 3.0f) * saturate(pattern * 1.6f - 0.5f) * 0.7f;
    // The waves rolling in break into foam only where the water gets shallow.
    foam += shore * saturate(pattern * 2.0f - 0.5f) * (1.0f - smoothstep(1.0f, 6.0f, vertical));
    foam = saturate(foam) * saturate(1.0f - distance / 2500.0f);
    float3 foamColor = light * 0.85f;

    // Premultiplied alpha: the reflection is opaque, the body as opaque as the water is thick.
    float3 color = reflection * fresnel + body * (1.0f - fresnel) * (1.0f - transmit) + glint * edge;
    float alpha = saturate(fresnel + (1.0f - fresnel) * opacity);
    color = lerp(color, foamColor, foam);
    alpha = lerp(alpha, 1.0f, foam);

    // The bottom seen through the water, bent by the waves (not where something in front of the water would show),
    // each colour dimmed as much as the water absorbs it, with the light gathered by the waves on it in the sun.
    if (detailed && alpha < 0.999f)
    {
        float2 bend = slope * float2(1.0f, -1.0f) * min(thickness, 4.0f) * Viewport.w * 0.012f / max(distance * 0.05f, 1.0f);
        int2 pixel = int2(input.pos.xy + bend);
        float waterDepth = input.pos.z;
        if (SceneDepth.Load(int3(pixel, 0)) >= waterDepth)
            pixel = int2(input.pos.xy);
        float3 bottom = SceneColor.Load(int3(pixel, 0)).rgb;
        if (Caustics.x > 0.5f && behindRatio < 1e3f)
        {
            float3 ground = camera + (input.world - camera) * behindRatio;
            float under = input.world.z - ground.z;
            float strength = saturate(under * 2.0f) * exp(-under * 0.15f) * saturate(1.0f - distance / 200.0f)
                * saturate((0.25f - footprint) * 10.0f) * saturate(SunDirection.z * 3.0f) * SunColor.a;
            if (strength > 0.0f)
                bottom *= 1.0f + CausticLight(ground.xy, CameraTime.w) * 1.5f * strength;
        }
        color += bottom * transmit * (1.0f - fresnel) * (1.0f - foam);
        alpha = 1.0f;
    }

    // Same fog as the models.
    float fog = Settings.x * saturate((distance - 0.25f * Settings.y) / (0.75f * Settings.y));
    fog = fog * fog * (3.0f - 2.0f * fog);
    if (hdr)
        fog = pow(fog, 1.5f);
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
        public Vector4 Viewport;
        public Vector4 Options;
        public Vector4 WaveArea;
        public Vector4 MaskArea;
        public Vector4 OceanBounds;
        public Vector4 GridCenter;
        public Vector4 ColorArea;
        public Vector4 IslandArea;
        public Vector4 Caustics;
        public Vector4 Patterns;
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

    // A point of the open water's grid: where it is from the grid's centre, and the distance to its neighbours.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct GridVertex(Vector2 offset, float spacing)
    {
        public readonly Vector2 Offset = offset;
        public readonly float Spacing = spacing;
    }

    private const float OceanReach = 40_000f;

    private const float InlandWaves = 0.35f;

    public const float MeshWaves = 0.2f;

    // The grid: rings around the camera, closer together near it.
    private const int GridAngles = 320;
    private const int GridRings = 200;
    private const float GridInner = 0.6f;

    // Metres per texel of the images of the wave zones and of where the open water is.
    private const float WaveTexel = 8f;
    private const float MaskTexel = 4f;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11PixelShader _rayPixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11VertexShader _meshVertexShader;
    private readonly ID3D11InputLayout _meshInputLayout;
    private readonly ID3D11VertexShader _gridVertexShader;
    private readonly ID3D11InputLayout _gridInputLayout;
    private readonly ID3D11Buffer _gridVertices;
    private readonly ID3D11Buffer _gridIndices;
    private readonly int _gridIndexCount;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _meshConstantBuffer;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11DepthStencilState _noDepthState;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11SamplerState _linearClamp;
    private Constants _constants;

    // Every surface, the open ocean around the map in four bands (flat water); the surfaces not at the sea's height
    // (3D waves: the grid shows the rest).
    private (ID3D11Buffer? Buffer, int Count) _allSurfaces, _inlandSurfaces;
    private (ID3D11Texture2D Texture, ID3D11ShaderResourceView View)? _waveTexture, _maskTexture;
    private (ID3D11Texture2D Texture, ID3D11ShaderResourceView View)? _colorTexture, _islandTexture, _causticTexture;
    // The game's swell, waves along the shore, ripples and foam (all four, or none).
    private (ID3D11Texture2D Texture, ID3D11ShaderResourceView View)[]? _patterns;
    private Vector4 _waveArea, _maskArea, _oceanBounds, _colorArea, _islandArea;
    private readonly ID3D11SamplerState _linearWrap;
    private float _oceanHeight;
    private bool _openWater;
    private bool _waves3D;

    public WaterRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        ReadOnlyMemory<byte> vsBytecode = Compiler.Compile(ShaderSource, "VS", "water", "vs_5_0");
        _vertexShader = device.CreateVertexShader(vsBytecode.Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "water", "ps_5_0").Span);
        _rayPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSRays", "water", "ps_5_0").Span);
        _inputLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32_Float, 12, 0),
        ], vsBytecode.Span);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        // Model meshes: only the position and the normal of the model vertices are read.
        ReadOnlyMemory<byte> meshBytecode = Compiler.Compile(ShaderSource, "VSMesh", "water", "vs_5_0");
        _meshVertexShader = device.CreateVertexShader(meshBytecode.Span);
        _meshInputLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        ], meshBytecode.Span);
        _meshConstantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(MeshConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        ReadOnlyMemory<byte> gridBytecode = Compiler.Compile(ShaderSource, "VSGrid", "water", "vs_5_0");
        _gridVertexShader = device.CreateVertexShader(gridBytecode.Span);
        _gridInputLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32_Float, 8, 0),
        ], gridBytecode.Span);
        (_gridVertices, _gridIndices, _gridIndexCount) = CreateGrid(device);

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
        _noDepthState = device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero });

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

        _linearClamp = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
        _linearWrap = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
    }

    // Rings of points around the centre (one in the middle), each a few percent further than the previous one.
    private static (ID3D11Buffer, ID3D11Buffer, int) CreateGrid(ID3D11Device device)
    {
        float growth = MathF.Pow(OceanReach / GridInner, 1f / (GridRings - 1));
        float spacingShare = MathF.Max(growth - 1f, 2f * MathF.PI / GridAngles);
        var vertices = new GridVertex[GridAngles * GridRings + 1];
        vertices[0] = new GridVertex(Vector2.Zero, GridInner);
        for (int ring = 0; ring < GridRings; ring++)
        {
            float radius = GridInner * MathF.Pow(growth, ring);
            for (int a = 0; a < GridAngles; a++)
            {
                float angle = 2f * MathF.PI * a / GridAngles;
                vertices[1 + ring * GridAngles + a] = new GridVertex(new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, radius * spacingShare);
            }
        }
        var indices = new List<uint>(GridAngles * GridRings * 6);
        for (int a = 0; a < GridAngles; a++)
            indices.AddRange([0u, (uint)(1 + a), (uint)(1 + (a + 1) % GridAngles)]);
        for (int ring = 0; ring + 1 < GridRings; ring++)
        {
            for (int a = 0; a < GridAngles; a++)
            {
                uint i0 = (uint)(1 + ring * GridAngles + a), i1 = (uint)(1 + ring * GridAngles + (a + 1) % GridAngles);
                uint o0 = i0 + GridAngles, o1 = i1 + GridAngles;
                indices.AddRange([i0, o0, i1, i1, o0, o1]);
            }
        }
        var vertexBuffer = device.CreateBuffer(vertices,
            new BufferDescription((uint)(vertices.Length * sizeof(GridVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        var indexArray = indices.ToArray();
        var indexBuffer = device.CreateBuffer(indexArray,
            new BufferDescription((uint)(indexArray.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));
        return (vertexBuffer, indexBuffer, indexArray.Length);
    }

    public bool HasSurfaces => _allSurfaces.Count > 0;

    // Replaces the water drawn: the map's surfaces, the open ocean at its height all around the map's water file, and
    // its zones of calm water and of waves rolling in (null: no water).
    public void SetSurfaces(WaterMap? water)
    {
        DisposeSurfaces();
        if (water is null)
            return;
        var bounds = water.OceanBounds;
        float oceanHeight = water.OceanHeight;
        bool hasOcean = bounds.Z > bounds.X && bounds.W > bounds.Y;
        bool AtSeaLevel(WaterQuad q) => MathF.Abs(q.Z - oceanHeight) < 0.05f;

        var all = new List<WaterVertex>();
        var inland = new List<WaterVertex>();
        foreach (var quad in water.Quads)
        {
            bool sea = AtSeaLevel(quad);
            foreach (var corner in quad.Triangles())
            {
                var vertex = new WaterVertex(corner, MathF.Abs(corner.Z - oceanHeight) < 0.5f ? 1f : InlandWaves);
                all.Add(vertex);
                if (!sea)
                    inland.Add(vertex);
            }
        }
        // Open ocean: four bands around the map's water, drawn after it (the map's water wins where they overlap).
        if (hasOcean)
        {
            float far = OceanReach;
            AddRectangle(all, -far, bounds.Y, bounds.X, bounds.W, oceanHeight); // West.
            AddRectangle(all, bounds.Z, bounds.Y, far, bounds.W, oceanHeight); // East.
            AddRectangle(all, -far, -far, far, bounds.Y, oceanHeight); // South.
            AddRectangle(all, -far, bounds.W, far, far, oceanHeight); // North.
        }
        _allSurfaces = CreateSurfaces(all);
        _inlandSurfaces = CreateSurfaces(inland);
        _oceanBounds = hasOcean ? bounds : Vector4.Zero;
        _oceanHeight = oceanHeight;
        _openWater = hasOcean || water.Quads.Any(AtSeaLevel);

        CreateMask(water.Quads.Where(AtSeaLevel).ToList());
        CreateWaveZones(water);
        if (water.ColorMaps.Count > 0)
            (_colorTexture, _colorArea) = CreateColorMap(water.ColorMaps[0]);
        if (water.ColorMaps.Count > 1)
            (_islandTexture, _islandArea) = CreateColorMap(water.ColorMaps[1]);
        if (water.Caustics is { } caustics)
            _causticTexture = CreatePattern(caustics);
        if (water is { OceanWaves: { } ocean, ShoreWaves: { } shore, Ripples: { } ripples, Foam: { } foam })
            _patterns = [CreatePattern(ocean), CreatePattern(shore), CreatePattern(ripples), CreatePattern(foam)];
    }

    // A tiling pattern with all its smaller levels (averages of 2 x 2 texels, wrapping round), so it fades out
    // smoothly far away instead of flickering.
    private (ID3D11Texture2D, ID3D11ShaderResourceView) CreatePattern(WaterPattern pattern)
    {
        int channels = pattern.Channels;
        var levels = new List<(int Width, int Height, byte[] Values)> { (pattern.Width, pattern.Height, pattern.Values) };
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            var (w, h, values) = levels[^1];
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * channels];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                                sum += values[(((y * 2 + dy) % h) * w + (x * 2 + dx) % w) * channels + c];
                        next[(y * nw + x) * channels + c] = (byte)((sum + 2) / 4);
                    }
                }
            }
            levels.Add((nw, nh, next));
        }
        var handles = levels.Select(l => GCHandle.Alloc(l.Values, GCHandleType.Pinned)).ToArray();
        try
        {
            var data = levels.Select((l, i) => new SubresourceData(handles[i].AddrOfPinnedObject(), (uint)(l.Width * channels))).ToArray();
            var texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)pattern.Width,
                Height = (uint)pattern.Height,
                MipLevels = (uint)levels.Count,
                ArraySize = 1,
                Format = channels == 2 ? Format.R8G8_UNorm : Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, data);
            return (texture, _device.CreateShaderResourceView(texture));
        }
        finally
        {
            foreach (var handle in handles)
                handle.Free();
        }
    }

    // The image's first row is the north edge: sampled from the north-west corner, y going south.
    private ((ID3D11Texture2D, ID3D11ShaderResourceView), Vector4) CreateColorMap(WaterColorMap map)
    {
        var area = map.Area;
        var texture = CreateTexture(map.Width, map.Height, Format.R8G8B8A8_UNorm, map.Rgba, 4);
        return (texture, new Vector4(area.X, area.W, 1f / (area.Z - area.X), -1f / (area.W - area.Y)));
    }

    private (ID3D11Buffer?, int) CreateSurfaces(List<WaterVertex> vertices)
    {
        if (vertices.Count == 0)
            return (null, 0);
        var buffer = _device.CreateBuffer(CollectionsMarshal.AsSpan(vertices),
            new BufferDescription((uint)(vertices.Count * sizeof(WaterVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        return (buffer, vertices.Count);
    }

    private static void AddRectangle(List<WaterVertex> vertices, float minX, float minY, float maxX, float maxY, float z)
    {
        var a = new WaterVertex(new Vector3(minX, minY, z), 1f);
        var b = new WaterVertex(new Vector3(maxX, minY, z), 1f);
        var c = new WaterVertex(new Vector3(minX, maxY, z), 1f);
        var d = new WaterVertex(new Vector3(maxX, maxY, z), 1f);
        vertices.AddRange([a, b, c, c, b, d]);
    }

    // Where the map has open water (surfaces at the sea's height), one byte per few metres: 255 water, 0 land.
    private void CreateMask(List<WaterQuad> quads)
    {
        if (quads.Count == 0)
            return;
        float minX = quads.Min(q => q.MinX), minY = quads.Min(q => q.MinY);
        float maxX = quads.Max(q => q.MaxX), maxY = quads.Max(q => q.MaxY);
        float texel = MathF.Max(MaskTexel, MathF.Max(maxX - minX, maxY - minY) / 4096f);
        int width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / texel)), height = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / texel));
        var mask = new byte[width * height];
        foreach (var quad in quads)
        {
            int x0 = Math.Max(0, (int)((quad.MinX - minX) / texel) - 1), x1 = Math.Min(width - 1, (int)((quad.MaxX - minX) / texel) + 1);
            int y0 = Math.Max(0, (int)((quad.MinY - minY) / texel) - 1), y1 = Math.Min(height - 1, (int)((quad.MaxY - minY) / texel) + 1);
            for (int y = y0; y <= y1; y++)
            {
                float py = minY + (y + 0.5f) * texel;
                for (int x = x0; x <= x1; x++)
                {
                    if (quad.Contains(minX + (x + 0.5f) * texel, py))
                        mask[y * width + x] = 255;
                }
            }
        }
        _maskTexture = CreateTexture(width, height, Format.R8_UNorm, mask, 1);
        _maskArea = new Vector4(minX, minY, 1f / (width * texel), 1f / (height * texel));
    }

    // The calm zones and the zones of waves rolling in, blurred so their edges do not show: calm (0 to 1), size of the
    // waves rolling in (/ 2.5) and their direction (x, y from 0 to 1).
    private void CreateWaveZones(WaterMap water)
    {
        if (water.Calming.Count == 0 && water.Waves.Count == 0)
            return;
        var zones = water.Calming.Select(c => (c.MinX, c.MinY, c.MaxX, c.MaxY))
            .Concat(water.Waves.Select(w => (w.MinX, w.MinY, w.MaxX, w.MaxY))).ToList();
        float margin = 8f * WaveTexel;
        float minX = zones.Min(z => z.MinX) - margin, minY = zones.Min(z => z.MinY) - margin;
        float maxX = zones.Max(z => z.MaxX) + margin, maxY = zones.Max(z => z.MaxY) + margin;
        float texel = MathF.Max(WaveTexel, MathF.Max(maxX - minX, maxY - minY) / 4096f);
        int width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / texel)), height = Math.Max(1, (int)MathF.Ceiling((maxY - minY) / texel));
        int count = width * height;
        var calm = new float[count];
        var size = new float[count];
        var wayX = new float[count];
        var wayY = new float[count];
        Array.Fill(calm, 1f);

        (int X0, int X1, int Y0, int Y1) Cells(float zx0, float zy0, float zx1, float zy1) =>
            (Math.Max(0, (int)((zx0 - minX) / texel)), Math.Min(width - 1, (int)((zx1 - minX) / texel)),
             Math.Max(0, (int)((zy0 - minY) / texel)), Math.Min(height - 1, (int)((zy1 - minY) / texel)));
        foreach (var zone in water.Calming)
        {
            var (x0, x1, y0, y1) = Cells(zone.MinX, zone.MinY, zone.MaxX, zone.MaxY);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    calm[y * width + x] = MathF.Min(calm[y * width + x], zone.Dampening);
        }
        foreach (var zone in water.Waves)
        {
            var (x0, x1, y0, y1) = Cells(zone.MinX, zone.MinY, zone.MaxX, zone.MaxY);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * width + x;
                    size[i] = MathF.Max(size[i], zone.Amplitude);
                    wayX[i] += zone.Direction.X * zone.Amplitude;
                    wayY[i] += zone.Direction.Y * zone.Amplitude;
                }
            }
        }
        foreach (var channel in new[] { calm, size, wayX, wayY })
            Blur(channel, width, height, 4);

        var texels = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            var way = new Vector2(wayX[i], wayY[i]);
            way = way.LengthSquared() > 1e-8f ? Vector2.Normalize(way) : Vector2.Zero;
            texels[i * 4] = Byte(calm[i]);
            texels[i * 4 + 1] = Byte(size[i] / 2.5f);
            texels[i * 4 + 2] = Byte(way.X * 0.5f + 0.5f);
            texels[i * 4 + 3] = Byte(way.Y * 0.5f + 0.5f);
        }
        _waveTexture = CreateTexture(width, height, Format.R8G8B8A8_UNorm, texels, 4);
        _waveArea = new Vector4(minX, minY, 1f / (width * texel), 1f / (height * texel));

        static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
    }

    // Averages each value with its neighbours up to radius cells away, across then down.
    private static void Blur(float[] values, int width, int height, int radius)
    {
        var line = new float[Math.Max(width, height)];
        for (int pass = 0; pass < 2; pass++)
        {
            bool across = pass == 0;
            int lines = across ? height : width, length = across ? width : height;
            for (int l = 0; l < lines; l++)
            {
                int Index(int i) => across ? l * width + i : i * width + l;
                for (int i = 0; i < length; i++)
                    line[i] = values[Index(i)];
                float sum = 0f;
                int n = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    if (i >= 0 && i < length)
                    {
                        sum += line[i];
                        n++;
                    }
                }
                for (int i = 0; i < length; i++)
                {
                    values[Index(i)] = sum / Math.Max(n, 1);
                    int leaving = i - radius, entering = i + radius + 1;
                    if (leaving >= 0)
                    {
                        sum -= line[leaving];
                        n--;
                    }
                    if (entering < length)
                    {
                        sum += line[entering];
                        n++;
                    }
                }
            }
        }
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView) CreateTexture(int width, int height, Format format, byte[] texels, int bytesPerTexel)
    {
        fixed (byte* data = texels)
        {
            var texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, new SubresourceData((IntPtr)data, (uint)(width * bytesPerTexel)));
            return (texture, _device.CreateShaderResourceView(texture));
        }
    }

    // Starts drawing water over the frame, before DrawSurfaces and DrawMesh, then End. sceneDepth: copy of the depth
    // buffer after the solid models (null: the water is opaque everywhere). sceneColor: copy of the picture drawn so
    // far, for reflections of the scene and the bottom bent by the waves (null: the sky only is mirrored). viewport:
    // pixel rectangle of the 3D view (x, y, width, height). time moves the waves (seconds). waves3D: the open water's
    // surface moves with the waves (otherwise flat, only its look follows them). rayReflections: what the ray tracing
    // found along each pixel's reflected ray (RayTracedReflections.Trace), used instead of the reflections on screen.
    public void Begin(Matrix4x4 viewProjection, Camera camera, SceneLighting lighting, float time, ID3D11ShaderResourceView? sceneDepth,
        ID3D11ShaderResourceView? sceneColor, Vector4 viewport, bool waves3D = false, ID3D11ShaderResourceView? rayReflections = null)
    {
        _waves3D = waves3D && _openWater;
        // The grid follows the camera by quarters of a metre.
        var center = new Vector2(MathF.Round(camera.Position.X * 4f) / 4f, MathF.Round(camera.Position.Y * 4f) / 4f);
        _constants = new Constants
        {
            ViewProjection = viewProjection,
            CameraTime = new Vector4(camera.Position, time),
            SunDirection = new Vector4(lighting.SunDirection, camera.NearPlane),
            SunColor = new Vector4(lighting.SunColor, lighting.SunDisc),
            SkyAmbient = new Vector4(lighting.SkyAmbient, 0f),
            Zenith = new Vector4(lighting.Zenith, 0f),
            Horizon = new Vector4(lighting.Horizon, 0f),
            Settings = new Vector4(lighting.FogDistance > 0f ? 1f : 0f, MathF.Max(lighting.FogDistance, 1f), sceneDepth is null ? 0f : 1f,
                sceneColor is null ? 0f : 1f),
            Viewport = viewport,
            Options = new Vector4(lighting.IsHdr ? 1f : 0f, _waves3D ? 1f : 0f, _oceanHeight, rayReflections is null ? 0f : 1f),
            WaveArea = _waveTexture is null ? Vector4.Zero : _waveArea,
            MaskArea = _maskTexture is null ? Vector4.Zero : _maskArea,
            OceanBounds = _oceanBounds,
            GridCenter = new Vector4(center, 0f, 0f),
            ColorArea = _colorTexture is null ? Vector4.Zero : _colorArea,
            IslandArea = _islandTexture is null ? Vector4.Zero : _islandArea,
            Caustics = new Vector4(_causticTexture is null ? 0f : 1f, 0f, 0f, 0f),
            Patterns = new Vector4(_patterns is null ? 0f : 1f, 0f, 0f, 0f),
        };
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, _constants);
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
        if (sceneColor is null)
            _context.PSUnsetShaderResource(1);
        else
            _context.PSSetShaderResource(1, sceneColor);
        if (_waveTexture is { } waves)
        {
            _context.VSSetShaderResource(2, waves.View);
            _context.PSSetShaderResource(2, waves.View);
        }
        if (_maskTexture is { } mask)
            _context.PSSetShaderResource(3, mask.View);
        if (_colorTexture is { } colors)
            _context.PSSetShaderResource(4, colors.View);
        if (_islandTexture is { } island)
            _context.PSSetShaderResource(5, island.View);
        if (_causticTexture is { } caustic)
            _context.PSSetShaderResource(6, caustic.View);
        if (rayReflections is not null)
            _context.PSSetShaderResource(11, rayReflections);
        if (_patterns is { } patterns)
        {
            for (int i = 0; i < patterns.Length; i++)
                _context.PSSetShaderResource((uint)(7 + i), patterns[i].View);
            _context.VSSetShaderResource(7, patterns[0].View);
            _context.VSSetShaderResource(8, patterns[1].View);
            _context.VSSetSampler(1, _linearWrap);
        }
        _context.VSSetSampler(0, _linearClamp);
        _context.PSSetSampler(0, _linearClamp);
        _context.PSSetSampler(1, _linearWrap);
        _context.GSSetShader(null);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.RSSetState(_rasterizerState);
    }

    // After Begin (with the scene's depth): the surfaces and meshes drawn next write where each pixel's reflected ray
    // starts and where it goes, for the ray tracing, instead of the water's colour.
    public void BeginRayStarts(ID3D11RenderTargetView starts, ID3D11RenderTargetView directions)
    {
        _context.OMSetRenderTargets([starts, directions], null);
        _context.PSSetShader(_rayPixelShader);
        _context.OMSetDepthStencilState(_noDepthState, 0);
        _context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
    }

    public void DrawSurfaces()
    {
        var (buffer, count) = _waves3D ? _inlandSurfaces : _allSurfaces;
        if (buffer is not null)
        {
            _context.IASetInputLayout(_inputLayout);
            _context.IASetVertexBuffer(0, buffer, (uint)sizeof(WaterVertex));
            _context.VSSetShader(_vertexShader);
            _context.Draw((uint)count, 0);
        }
        if (_waves3D)
        {
            _context.IASetInputLayout(_gridInputLayout);
            _context.IASetVertexBuffer(0, _gridVertices, (uint)sizeof(GridVertex));
            _context.IASetIndexBuffer(_gridIndices, Format.R32_UInt, 0);
            _context.VSSetShader(_gridVertexShader);
            _context.DrawIndexed((uint)_gridIndexCount, 0, 0);
        }
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
        _context.IASetIndexBuffer(model.IndexBuffer, Format.R16_UInt, 0);
        _context.VSSetShader(_meshVertexShader);
        foreach (var part in model.Parts)
        {
            if (part.BlendMode == MeshBlendMode.Water)
                _context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        }
    }

    // The copies are written again next frame.
    public void End()
    {
        for (uint slot = 0; slot < 12; slot++)
            _context.PSUnsetShaderResource(slot);
        _context.VSUnsetShaderResource(2);
        _context.VSUnsetShaderResource(3);
        _context.VSUnsetShaderResource(7);
        _context.VSUnsetShaderResource(8);
    }

    private void DisposeSurfaces()
    {
        _allSurfaces.Buffer?.Dispose();
        _allSurfaces = default;
        _inlandSurfaces.Buffer?.Dispose();
        _inlandSurfaces = default;
        if (_waveTexture is { } waves)
        {
            waves.View.Dispose();
            waves.Texture.Dispose();
        }
        _waveTexture = null;
        if (_maskTexture is { } mask)
        {
            mask.View.Dispose();
            mask.Texture.Dispose();
        }
        _maskTexture = null;
        foreach (var look in new[] { _colorTexture, _islandTexture, _causticTexture })
        {
            if (look is { } t)
            {
                t.View.Dispose();
                t.Texture.Dispose();
            }
        }
        _colorTexture = _islandTexture = _causticTexture = null;
        foreach (var (texture, view) in _patterns ?? [])
        {
            view.Dispose();
            texture.Dispose();
        }
        _patterns = null;
        _openWater = false;
    }

    public void Dispose()
    {
        DisposeSurfaces();
        _linearClamp.Dispose();
        _linearWrap.Dispose();
        _gridVertices.Dispose();
        _gridIndices.Dispose();
        _gridInputLayout.Dispose();
        _gridVertexShader.Dispose();
        _blendState.Dispose();
        _depthState.Dispose();
        _noDepthState.Dispose();
        _rasterizerState.Dispose();
        _constantBuffer.Dispose();
        _meshConstantBuffer.Dispose();
        _meshInputLayout.Dispose();
        _meshVertexShader.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _rayPixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
