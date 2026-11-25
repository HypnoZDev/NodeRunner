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
    float4 FillDirection;
    float4 FillColor;
    float4 RimDirection;
    float4 RimColor;
    float4 ClassicLight;  // First trilogy pre-lit surfaces: xyz = ambient light added, w = night colours blend (0 day).
    float4 ClassicShade;  // x: how much the sun shadows darken them.
    float4 Output;        // x = 1: linear colours of an HDR image (textures and vertex colours made linear), y: brightness
                          // of the light given off by lit windows and lamps there.
    float4 SkyTop;        // Colour of the sky overhead (mirrored by shiny surfaces).
    float4 FogSun;        // Fog colour added towards the sun (sky worked out from the air), along SkySun.
    float4 SkySun;        // xyz: direction of the sun (even below the horizon).
    float4 Weather;       // x: how wet the surfaces are (rain, 0 to 1), y: how much snow lies on them, z: time (s).
};
cbuffer Object : register(b2)
{
    float4x4 World;
    float4 Tint;
    uint4 PickId;      // x: number of the object, for picking.
    float4 Emission;   // x: how much the mesh gives off its own light (0 = only lit by the sun).
                       // y = 1: the vertex colour is light worked out in advance (GTA IV), darkening nooks.
                       // y = 2: the vertex colour is the whole light (first trilogy pre-lit), the second vertex
                       //        colour the same at night.
                       // z = 1: darkening decal, the texture brightness is how dark (GTA IV gta_decal_dirt).
                       // w = 1: a character (ped shaders).
    float4 Terrain;    // Ground layers: x = 1 to blend with the mask texture, y = 1 for the mask only.
                       // z = 1: the colour is the vertex colour (no texture).
    float4 Surface;    // x = 1 with a normal map (NormalTexture), y with a specular map (SpecularTexture): 1 for a
                       // character, 2 for anything else (also mirrors the sky), z: how bright its highlights are, w: how
                       // tight.
};
Texture2D ColorTexture : register(t0);
Texture2D Layer1 : register(t1);
Texture2D Layer2 : register(t2);
Texture2D Layer3 : register(t3);
Texture2D BlendMask : register(t4);
Texture2D NormalTexture : register(t6);
Texture2D SpecularTexture : register(t7);
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

// Models bent by a skeleton: each bone's matrix (default pose -> animated), as 3 rows giving x, y and z.
cbuffer Bones : register(b3)
{
    float4 BoneRows[765];
};
struct VS_SKIN_INPUT
{
    float3 pos : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR0;
    float4 weights : COLOR1; float2 maskUv : TEXCOORD1;
    uint4 bones : BLENDINDICES; float4 boneWeights : BLENDWEIGHT;
};

PS_INPUT VSSkinned(VS_SKIN_INPUT input)
{
    VS_INPUT v;
    v.pos = input.pos; v.normal = input.normal; v.uv = input.uv; v.color = input.color;
    v.weights = input.weights; v.maskUv = input.maskUv;
    // No weight: a mesh that follows no bone stays as it is.
    if (dot(input.boneWeights, 1.0f) > 0.001f)
    {
        float4 p = float4(input.pos, 1.0f);
        float3 pos = 0.0f, normal = 0.0f;
        [unroll] for (int k = 0; k < 4; k++)
        {
            uint b = min(input.bones[k], 254u) * 3u;
            float w = input.boneWeights[k];
            pos += w * float3(dot(BoneRows[b], p), dot(BoneRows[b + 1], p), dot(BoneRows[b + 2], p));
            normal += w * float3(dot(BoneRows[b].xyz, input.normal), dot(BoneRows[b + 1].xyz, input.normal),
                dot(BoneRows[b + 2].xyz, input.normal));
        }
        v.pos = pos;
        v.normal = normal;
    }
    return VS(v);
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

float3 ToLinear(float3 c)
{
    return pow(abs(c), 2.2f);
}

// Colour of the fog in front of world: brighter in the direction of the sun when the sky is worked out from the air.
float3 FogTowards(float3 world)
{
    float2 view = world.xy - CameraFog.xy;
    float2 sun = SkySun.xy;
    if (dot(FogSun.rgb, 1.0f) <= 0.0f || dot(view, view) < 1e-4f || dot(sun, sun) < 1e-6f)
        return FogColor.rgb;
    float toward = saturate(dot(normalize(view), normalize(sun)));
    toward *= toward;
    return FogColor.rgb + FogSun.rgb * (toward * toward * toward * toward);
}

// Set by Shade for the second image of the solid pass (ambient occlusion): the normal of the surface, and the share of
// its light coming from the surroundings (the part the occlusion darkens).
static float3 ShadedNormal = float3(0.0f, 0.0f, 1.0f);
static float AmbientShare = 1.0f;
static float3 SurfaceColor = 0.0f; // Its colour without the light (lit by NightLights).
static float WetMirror = 1.0f;     // How much a wet surface mirrors the sky (little for leaves and grass).

// The normal bent by a normal map (x and y in red and green, z rebuilt), along directions worked out from how the
// position and the texture coordinates change from pixel to pixel: no tangents needed in the vertices.
float3 Relief(float3 n, float3 world, float2 uv)
{
    float2 xy = NormalTexture.Sample(ColorSampler, uv).rg * 2.0f - 1.0f;
    float3 bump = float3(xy, sqrt(saturate(1.0f - dot(xy, xy))));
    float3 dp1 = ddx(world), dp2 = ddy(world);
    float2 duv1 = ddx(uv), duv2 = ddy(uv);
    float3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
    float3 t = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 b = dp2perp * duv1.y + dp1perp * duv2.y;
    float scale = rsqrt(max(max(dot(t, t), dot(b, b)), 1e-20f));
    return normalize(t * scale * bump.x - b * scale * bump.y + n * bump.z);
}

// Colour of the sky seen along a mirrored direction (the ground below the horizon).
float3 MirroredSky(float3 mirrored)
{
    return mirrored.z >= 0.0f ? lerp(FogColor.rgb, SkyTop.rgb, saturate(mirrored.z * 1.6f))
        : lerp(FogColor.rgb, GroundAmbient.rgb * 0.5f, saturate(-mirrored.z * 4.0f));
}

float Hash(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031f);
    p3 += dot(p3, p3.yzx + 33.33f);
    return frac((p3.x + p3.y) * p3.z);
}

float ValueNoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0f - 2.0f * f);
    return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + 1.0f), f.x), f.y);
}

// Rings spreading from raindrops on a puddle (one cell = 1 in p, a drop now and then in each cell, two grids shifted
// so the rings overlap): the slope of the water along x and y.
float2 Ripples(float2 p, float time)
{
    float2 slope = 0.0f;
    [unroll] for (int layer = 0; layer < 2; layer++)
    {
        float2 q = p + layer * float2(0.5f, 0.37f);
        float2 cell = floor(q);
        float2 d = q - cell - (0.3f + 0.4f * float2(Hash(cell + layer * 17.0f), Hash(cell.yx + 31.0f + layer * 5.0f)));
        float age = frac(time * 0.9f + Hash(cell * 1.7f + layer * 3.1f));
        float r = length(d);
        float ring = r - age * 0.3f;
        float strength = saturate(1.0f - abs(ring) / 0.06f) * (1.0f - age);
        slope += (r > 1e-4f ? d / r : 0.0f) * (sin(ring * 90.0f) * strength);
    }
    return slope;
}

// Snow (0 to 1) lying on what faces the sky: flat ground and tops covered, slopes partly with a ragged edge, none on
// walls; characters hardly. Snow fills the small relief (the geometric normal decides more than the normal map).
float SnowCover(float3 world, float3 geometric, float3 n)
{
    float up = lerp(geometric.z, n.z, 0.3f);
    float ragged = ValueNoise(world.xy * 0.12f) * 0.6f + ValueNoise(world.xy * 0.9f) * 0.4f;
    float snow = saturate((up - 0.5f + 0.4f * (ragged - 0.5f)) * 5.0f) * Weather.y;
    return Emission.w > 0.5f ? snow * 0.25f : snow;
}

// Lit colour; alpha straight from the colour.
float4 Shade(PS_INPUT input, float4 color)
{
    float3 n = input.normal;
    float len = length(n);
    n = len > 0.0001f ? n / len : float3(0, 0, 1);
    float3 geometric = n;
    if (Surface.x > 0.5f)
        n = Relief(n, input.world, input.uv);
    float snow = Weather.y > 0.0f ? SnowCover(input.world, geometric, n) : 0.0f;
    n = normalize(lerp(n, geometric, snow));
    float sun = saturate(dot(n, LightDirection.xyz));
    if (sun > 0.0f)
        sun *= SunShadow(input.world, n);
    if (Emission.y > 1.5f)
    {
        // First trilogy: the game lights its buildings only with their pre-lit colours (day and night sets in San
        // Andreas) plus the ambient light of the hour; the sun only darkens what is in a shadow, a little.
        float3 prelit = lerp(input.color.rgb, input.weights.rgb, ClassicLight.w) + ClassicLight.rgb;
        float shade = 1.0f - ClassicShade.x * (1.0f - saturate(sun * 4.0f));
        float3 classic = color.rgb * Tint.rgb * min(prelit, 1.0f) * shade;
        if (Output.x > 0.5f)
            classic = ToLinear(classic) * 2.0f;
        ShadedNormal = n;
        AmbientShare = 1.0f;
        SurfaceColor = Output.x > 0.5f ? ToLinear(color.rgb * Tint.rgb) : color.rgb * Tint.rgb;
        float far = length(input.world - CameraFog.xyz);
        float haze = FogColor.a * saturate((far - 0.25f * CameraFog.w) / (0.75f * CameraFog.w));
        haze = haze * haze * (3.0f - 2.0f * haze);
        if (Output.x > 0.5f)
            haze = pow(haze, 1.5f); // Blended in linear colours, the haze would look thicker.
        return float4(lerp(classic, FogTowards(input.world), haze), color.a);
    }
    if (Output.x > 0.5f)
        color.rgb = ToLinear(color.rgb);
    color.rgb = lerp(color.rgb, Output.x > 0.5f ? float3(0.72f, 0.75f, 0.8f) : float3(0.88f, 0.9f, 0.93f), snow);
    float sky = 0.5f + 0.5f * n.z;
    float3 ambient = lerp(GroundAmbient.rgb, SkyAmbient.rgb, sky);
    if (Emission.y > 0.5f)
    {
        // About 0.5 in the open: doubled, so only nooks and the ground under objects get darker. The sun is
        // partly shaded too (beyond the shadow distance, this is the only shade left).
        float baked = saturate(dot(input.color.rgb, float3(0.667f, 0.667f, 0.667f)));
        ambient *= baked;
        sun *= 0.5f + 0.5f * baked;
    }
    // Characters: the red of the vertex colour is how much of the surroundings' light reaches the vertex (nooks under
    // the hair, the collar, between the fingers).
    if (Emission.w > 0.5f)
        ambient *= input.color.r;
    float3 light = ambient + SunColor.rgb * sun + FillColor.rgb * saturate(dot(n, FillDirection.xyz));
    // Rim light: only on the edges seen side-on.
    float3 toCamera = normalize(CameraFog.xyz - input.world);
    float edge = 1.0f - saturate(abs(dot(n, toCamera)));
    light += RimColor.rgb * saturate(dot(n, RimDirection.xyz)) * edge * edge;
    ShadedNormal = n;
    AmbientShare = dot(ambient, 1.0f) / max(dot(light, 1.0f), 0.0001f);
    // Rain: water soaks into what faces the sky (darker) and lies on it (mirrors the sky), most on flat ground, a
    // little on walls it runs down.
    float wet = Weather.x * lerp(0.3f, 1.0f, saturate(geometric.z * 1.5f));
    if (Emission.w > 0.5f)
        wet *= 0.4f;
    // Puddles in the hollows of flat ground (patches of a few metres), darker still.
    float puddle = 0.0f;
    if (Weather.x > 0.0f && Emission.w < 0.5f && geometric.z > 0.96f)
    {
        float hollow = ValueNoise(input.world.xy * 0.22f) * 0.7f + ValueNoise(input.world.xy * 0.9f) * 0.3f;
        puddle = smoothstep(0.56f, 0.61f, hollow) * saturate((geometric.z - 0.96f) * 50.0f) * Weather.x * WetMirror;
    }
    color.rgb *= (1.0f - (Output.x > 0.5f ? 0.6f : 0.35f) * wet) * (1.0f - 0.45f * puddle);
    SurfaceColor = color.rgb * Tint.rgb;
    // Own light: at least the texture colour, brighter than the surroundings at night.
    float3 lit = color.rgb * Tint.rgb * max(light, Emission.x * (Output.x > 0.5f ? Output.y : 1.4f));
    // Characters' shine: highlights of the key light, as bright as the red of the specular map (squared) says, as
    // tight as its alpha says.
    if (Surface.y > 0.5f)
    {
        float4 s = SpecularTexture.Sample(ColorSampler, input.uv);
        if (sun > 0.0f)
        {
            float3 halfway = normalize(LightDirection.xyz + toCamera);
            float power = clamp(s.a * Surface.w, 8.0f, 512.0f);
            float highlight = pow(saturate(dot(n, halfway)), power) * 0.3f;
            lit += SunColor.rgb * (s.r * s.r * Surface.z * highlight * sun);
        }
        // Other surfaces mirror the sky, more when seen side-on (Fresnel), as much as their shine; less in nooks and
        // indoors (where the light around is darker than the sky's).
        if (Surface.y > 1.5f)
        {
            float facing = 1.0f - saturate(dot(n, toCamera));
            float fresnel = 0.04f + 0.96f * facing * facing * facing * facing * facing;
            float open = saturate(dot(ambient, 1.0f) / max(dot(lerp(GroundAmbient.rgb, SkyAmbient.rgb, sky), 1.0f), 0.0001f));
            lit += MirroredSky(reflect(-toCamera, n)) * (fresnel * s.r * saturate(Surface.z) * 0.45f * open);
        }
    }
    if (wet > 0.0f)
    {
        // The water film fills the relief: smoother than the surface under it. Less in nooks and indoors.
        float3 w = normalize(lerp(n, geometric, wet));
        float crests = 0.0f;
        if (puddle > 0.0f)
        {
            // A puddle is flat, stirred by the rings of the drops (seen only up close), their crests catching the light.
            float near = saturate(1.0f - length(input.world - CameraFog.xyz) / 25.0f);
            float2 slope = Ripples(input.world.xy / 0.4f, Weather.z) * (0.35f * near * Weather.x);
            w = normalize(lerp(w, normalize(float3(-slope, 1.0f)), puddle));
            crests = saturate(length(slope) * 2.0f) * puddle;
        }
        // Sheets seen from behind (grass, leaves) are not mirrors.
        float front = dot(w, toCamera);
        wet *= saturate(front * 8.0f) * WetMirror;
        float facing = 1.0f - saturate(front);
        float fresnel = lerp(0.03f, 0.06f, puddle) + 0.9f * facing * facing * facing * facing * facing;
        float open = Emission.y > 0.5f ? saturate(dot(input.color.rgb, 0.667f)) : 1.0f;
        lit = lerp(lit, MirroredSky(reflect(-toCamera, w)), fresnel * lerp(wet * 0.8f, 1.0f, puddle) * open);
        lit += SkyTop.rgb * (crests * 0.25f * open);
    }

    // Distance fog, thicker towards the horizon colour; none on the first quarter of the distance.
    float distance = length(input.world - CameraFog.xyz);
    float fog = FogColor.a * saturate((distance - 0.25f * CameraFog.w) / (0.75f * CameraFog.w));
    fog = fog * fog * (3.0f - 2.0f * fog);
    if (Output.x > 0.5f)
        fog = pow(fog, 1.5f); // Blended in linear colours, the haze would look thicker.
    return float4(lerp(lit, FogTowards(input.world), fog), color.a);
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
// Characters' two-sided parts (hair, cloth) are lit on the side seen, as single sheets (their files hold each sheet
// twice, back to back, with the winding not telling which side is which).
void FaceCamera(inout PS_INPUT input)
{
    if (Emission.w > 0.5f && dot(input.normal, CameraFog.xyz - input.world) < 0.0f)
        input.normal = -input.normal;
}

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
    // Terrain.z: each layer laid over the previous ones by its own weight (x, y, z), as GTA IV does.
    float4 color = Terrain.z > 0.5f ? lerp(lerp(lerp(c0, c1, w.x), c2, w.y), c3, w.z)
        : lerp(lerp(c0, c1, w.b), lerp(c2, c3, w.b), w.g);
    return float4(Shade(input, color).rgb, 1.0f);
}

float4 PSCutout(PS_INPUT input) : SV_Target
{
    FaceCamera(input);
    WetMirror = 0.1f;
    float4 color = Shade(input);
    clip(color.a - 0.33f);
    return float4(color.rgb, 1.0f);
}

// The vertex alpha fades decals in and out (e.g. grime fading towards the top of a wall).
float4 PSTranslucent(PS_INPUT input) : SV_Target
{
    FaceCamera(input);
    if (Emission.z > 0.5f)
    {
        float darkness = dot(ColorTexture.Sample(ColorSampler, input.uv).rgb, float3(0.299f, 0.587f, 0.114f)) * input.color.a;
        clip(darkness - 0.01f);
        return float4(0.0f, 0.0f, 0.0f, darkness);
    }
    float4 color = Shade(input);
    color.a *= input.color.a;
    clip(color.a - 0.01f);
    return color;
}

// Solid passes also giving the images of the effects drawn over them (SceneImage.Bind): the normal (0 to 1) and the
// share of the light coming from the surroundings (ambient occlusion), the surface's colour (night lights).
struct PS_TWO { float4 color : SV_Target0; float4 normal : SV_Target1; float4 albedo : SV_Target2; };
PS_TWO WithNormal(float4 color)
{
    PS_TWO output;
    output.color = color;
    output.normal = float4(ShadedNormal * 0.5f + 0.5f, saturate(AmbientShare));
    output.albedo = float4(SurfaceColor, 1.0f);
    return output;
}

PS_TWO PSOpaqueNormal(PS_INPUT input)
{
    return WithNormal(PSOpaque(input));
}

PS_TWO PSTerrainNormal(PS_INPUT input)
{
    return WithNormal(PSTerrain(input));
}

PS_TWO PSCutoutNormal(PS_INPUT input)
{
    return WithNormal(PSCutout(input));
}

// Shadow images: depth only, with the holes of foliage and fences. Only the position and the texture coordinates of
// the vertices are read.
struct PS_SHADOW { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
PS_SHADOW VSShadow(float3 pos : POSITION, float2 uv : TEXCOORD0)
{
    PS_SHADOW output;
    output.pos = mul(ViewProjection, mul(World, float4(pos, 1.0f)));
    output.uv = uv;
    return output;
}

void PSShadowCutout(PS_SHADOW input)
{
    clip(ColorTexture.Sample(ColorSampler, input.uv).a - 0.33f);
}

void PSShadowSkinnedCutout(PS_INPUT input)
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
        public Vector4 FillDirection;
        public Vector4 FillColor;
        public Vector4 RimDirection;
        public Vector4 RimColor;
        public Vector4 ClassicLight;
        public Vector4 ClassicShade;
        public Vector4 Output;
        public Vector4 SkyTop;
        public Vector4 FogSun;
        public Vector4 SkySun;
        public Vector4 Weather;
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
        public Vector4 Surface;
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

    // Second vertex stream of skinned models: 4 bone numbers (one byte each) and their weights (0 to 255).
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SkinVertex(uint bones, uint weights)
    {
        public readonly uint Bones = bones;
        public readonly uint Weights = weights;
    }

    public const int MaxBones = 255;

    // Bones of the skinned model drawn next (ModelData.Skeleton order, see SkeletonAnimator): vertex in the default
    // pose -> animated, in the model. Null: drawn in its default pose.
    public Matrix4x4[]? BonePose { get; set; }

    public SceneLighting Lighting { get; set; } = SceneLighting.Neutral;

    // The solid passes also write the surface's normal into a second image (SceneImage, for the ambient occlusion).
    public bool NormalsOutput { get; set; }

    public Vector3 CameraPosition { get; set; }

    // Seconds, for what moves by itself (rings of the raindrops on the puddles).
    public float Time { get; set; }

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
    private readonly ID3D11PixelShader _opaqueNormalPixelShader;
    private readonly ID3D11PixelShader _terrainNormalPixelShader;
    private readonly ID3D11PixelShader _cutoutNormalPixelShader;
    private readonly ID3D11PixelShader _cutoutPixelShader;
    private readonly ID3D11PixelShader _translucentPixelShader;
    private readonly ID3D11PixelShader _shadowCutoutPixelShader;
    private readonly ID3D11PixelShader _shadowSkinnedCutoutPixelShader;
    private readonly ID3D11VertexShader _shadowVertexShader;
    private readonly ID3D11InputLayout _shadowInputLayout;
    private readonly ID3D11PixelShader _pickOpaquePixelShader;
    private readonly ID3D11PixelShader _pickCutoutPixelShader;
    private readonly ID3D11PixelShader _pickTranslucentPixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11VertexShader _skinnedVertexShader;
    private readonly ID3D11InputLayout _skinnedInputLayout;
    private readonly ID3D11Buffer _boneBuffer;
    private readonly float[] _boneRows = new float[MaxBones * 12];
    private VertexMode _vertexMode;
    private bool _shadowPass;
    private ID3D11PixelShader? _boundPixelShader;
    private bool _pixelShaderBound;
    private ID3D11RasterizerState? _boundRasterizer;
    private ID3D11ShaderResourceView? _boundColorTexture;
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
        _opaqueNormalPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSOpaqueNormal", "models", "ps_5_0").Span);
        _terrainNormalPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSTerrainNormal", "models", "ps_5_0").Span);
        _cutoutNormalPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSCutoutNormal", "models", "ps_5_0").Span);
        _cutoutPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSCutout", "models", "ps_5_0").Span);
        _translucentPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSTranslucent", "models", "ps_5_0").Span);
        _shadowCutoutPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSShadowCutout", "models", "ps_5_0").Span);
        _shadowSkinnedCutoutPixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PSShadowSkinnedCutout", "models", "ps_5_0").Span);
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

        ReadOnlyMemory<byte> shadowBytecode = Compiler.Compile(ShaderSource, "VSShadow", "models", "vs_5_0");
        _shadowVertexShader = device.CreateVertexShader(shadowBytecode.Span);
        _shadowInputLayout = device.CreateInputLayout([layout[0], layout[2]], shadowBytecode.Span);

        ReadOnlyMemory<byte> skinnedBytecode = Compiler.Compile(ShaderSource, "VSSkinned", "models", "vs_5_0");
        _skinnedVertexShader = device.CreateVertexShader(skinnedBytecode.Span);
        InputElementDescription[] skinnedLayout =
        [
            .. layout,
            new("BLENDINDICES", 0, Format.R8G8B8A8_UInt, 0, 1),
            new("BLENDWEIGHT", 0, Format.R8G8B8A8_UNorm, 4, 1),
        ];
        _skinnedInputLayout = device.CreateInputLayout(skinnedLayout, skinnedBytecode.Span);
        _boneBuffer = device.CreateBuffer(new BufferDescription(
            (uint)(MaxBones * 48), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

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

    // Sharper textures (negative: finer mipmaps), for an image smoothed over several frames (TAA).
    public float MipBias
    {
        get => _mipBias;
        set
        {
            if (value == _mipBias)
                return;
            _mipBias = value;
            _samplerState.Dispose();
            _samplerState = CreateSampler(_device, _anisotropy, value);
        }
    }

    private float _mipBias;

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
            _samplerState = CreateSampler(_device, value, _mipBias);
        }
    }

    private static ID3D11SamplerState CreateSampler(ID3D11Device device, int anisotropy, float mipBias = 0f) =>
        device.CreateSamplerState(new SamplerDescription
        {
            Filter = anisotropy > 1 ? Filter.Anisotropic : Filter.MinMagMipLinear,
            MaxAnisotropy = (uint)anisotropy,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
            MipLODBias = mipBias,
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
                terrain is null && mesh.TintTextureName is not null, mesh.ClassicPrelit ? 2 : mesh.BakedLighting ? 1 : 0, mesh.DarkeningDecal, mesh.IsCharacter,
                new Vector2(mesh.SpecularIntensity, mesh.SpecularFalloff)));
            for (int i = 0; i < mesh.Positions.Length; i++)
            {
                vertices[v++] = new ModelVertex(mesh.Positions[i], mesh.Normals[i], mesh.TexCoords[i], mesh.Colors[i],
                    terrain?.Weights[i] ?? (mesh.ClassicPrelit ? mesh.NightColors?[i] ?? mesh.Colors[i] : Vector4.Zero),
                    terrain?.MaskTexCoords[i] ?? mesh.TintTexCoords?[i] ?? Vector2.Zero);
            }
            mesh.Indices.CopyTo(indices, x);
            x += mesh.Indices.Length;
        }

        var skinned = Skin(model, vertexCount);
        var skin = skinned?.Skin;
        var vertexBuffer = _device.CreateBuffer(vertices,
            new BufferDescription((uint)(vertices.Length * sizeof(ModelVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        ID3D11Buffer indexBuffer;
        ID3D11Buffer? skinBuffer = null;
        try
        {
            indexBuffer = _device.CreateBuffer(indices,
                new BufferDescription((uint)(indices.Length * sizeof(ushort)), BindFlags.IndexBuffer, ResourceUsage.Immutable));
            if (skin is not null)
            {
                try
                {
                    skinBuffer = _device.CreateBuffer(skin,
                        new BufferDescription((uint)(skin.Length * sizeof(SkinVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
                }
                catch
                {
                    indexBuffer.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
        return new PreparedModel(vertexBuffer, indexBuffer, ranges.ToArray(), indexCount / 3,
            (long)vertexCount * sizeof(ModelVertex) + indexCount * 2L + (skin?.Length ?? 0) * (long)sizeof(SkinVertex), skinBuffer)
        {
            BonePalette = skinned?.Palette,
        };
    }

    // Bones of each vertex, in the order of the vertex buffer, numbered among the bones the model uses (its palette:
    // which bone of the skeleton each one is); null when no mesh follows a skeleton or it uses more bones than a draw
    // can take.
    private static (SkinVertex[] Skin, int[] Palette)? Skin(ModelData model, int vertexCount)
    {
        if (model.Skeleton is not { Count: > 0 } skeleton || !model.Meshes.Any(m => m.BoneIndices is not null))
            return null;
        var local = new Dictionary<int, int>();
        var palette = new List<int>();
        int Local(int bone)
        {
            if (!local.TryGetValue(bone, out int index))
            {
                local[bone] = index = palette.Count;
                palette.Add(bone);
            }
            return index;
        }
        var skin = new SkinVertex[vertexCount];
        int v = 0;
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Indices.Length == 0 || mesh.Positions.Length == 0)
                continue;
            for (int i = 0; i < mesh.Positions.Length; i++, v++)
            {
                if (mesh.BoneIndices is not { } bones || mesh.BoneWeights is not { } weights || i >= bones.Length || i >= weights.Length)
                    continue;
                var w = weights[i];
                uint packed = 0;
                for (int k = 0; k < 4; k++)
                {
                    int bone = (int)((bones[i] >> (16 * k)) & 0xFFFF);
                    float weight = k == 0 ? w.X : k == 1 ? w.Y : k == 2 ? w.Z : w.W;
                    // Bones without weight take no room in the palette.
                    int index = weight > 0f && bone < skeleton.Count ? Local(bone) : 0;
                    packed |= (uint)Math.Min(index, 255) << (8 * k);
                }
                skin[v] = new SkinVertex(packed, PackWeights(w));
            }
        }
        if (palette.Count == 0)
            palette.Add(0);
        return palette.Count > MaxBones ? null : (skin, palette.ToArray());
    }

    private static uint PackWeights(Vector4 w)
    {
        static uint Byte(float x) => (uint)Math.Clamp(MathF.Round(x * 255f), 0f, 255f);
        return Byte(w.X) | Byte(w.Y) << 8 | Byte(w.Z) << 16 | Byte(w.W) << 24;
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
            // Relief and shine textures: characters always, other meshes when loaded with them (TextureNamesWithSurface).
            bool surface = !r.Terrain && !r.Tinted;
            GpuTexture?[]? layers = r.Terrain ? textures.Skip(1).Concat(new GpuTexture?[4]).Take(4).ToArray() : null;
            parts[i] = new GpuModel.Part(r.FirstIndex, r.IndexCount, r.BaseVertex, texture, r.BlendMode, r.Emission, layers,
                r.TerrainMode, r.VertexColored, r.PaintSlot, r.Mesh, r.Tinted && textures.Length > 1 ? textures[1] : null, r.BakedLighting,
                r.DarkeningDecal, r.Character, surface && textures.Length > 1 ? textures[1] : null,
                surface && textures.Length > 2 ? textures[2] : null, r.Shine);
        }
        return new GpuModel(prepared.TakeVertexBuffer(), prepared.TakeIndexBuffer(), parts, prepared.TriangleCount, prepared.ByteSize)
        {
            SkinBuffer = prepared.TakeSkinBuffer(),
            BonePalette = prepared.BonePalette,
        };
    }

    // Sets the shaders and states shared by every model.
    public void Begin(ModelPass pass)
    {
        _pass = pass;
        _shadowPass = false;
        bool translucent = pass == ModelPass.Translucent;
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        BindVertexMode(VertexMode.Plain);
        BindConstants();
        _context.PSSetSampler(0, _samplerState);
        _context.GSSetShader(null);
        _context.OMSetBlendState(translucent ? _translucentBlendState : _blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(translucent ? _translucentDepthStencilState : _depthStencilState, 0);
        ForgetBoundStates();
        SetRasterizer(_rasterizerState);
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

    // Other renderers change the states between two passes: the next ones are set again.
    private void ForgetBoundStates()
    {
        _boundPixelShader = null;
        _pixelShaderBound = false;
        _boundRasterizer = null;
        _boundColorTexture = null;
    }

    private void SetPixelShader(ID3D11PixelShader? shader)
    {
        if (_pixelShaderBound && ReferenceEquals(shader, _boundPixelShader))
            return;
        _context.PSSetShader(shader);
        _boundPixelShader = shader;
        _pixelShaderBound = true;
    }

    private void SetRasterizer(ID3D11RasterizerState state)
    {
        if (ReferenceEquals(state, _boundRasterizer))
            return;
        _context.RSSetState(state);
        _boundRasterizer = state;
    }

    private void SetColorTexture(GpuTexture texture)
    {
        if (ReferenceEquals(texture.View, _boundColorTexture))
            return;
        _context.PSSetShaderResource(0, texture.View);
        _boundColorTexture = texture.View;
    }

    // Vertex shader and vertex layout: plain models, models following a skeleton, or shadow images (position and
    // texture coordinates only).
    private enum VertexMode
    {
        Plain,
        Skinned,
        Shadow,
    }

    private void BindVertexMode(VertexMode mode)
    {
        _vertexMode = mode;
        _context.IASetInputLayout(mode switch { VertexMode.Skinned => _skinnedInputLayout, VertexMode.Shadow => _shadowInputLayout, _ => _inputLayout });
        _context.VSSetShader(mode switch { VertexMode.Skinned => _skinnedVertexShader, VertexMode.Shadow => _shadowVertexShader, _ => _vertexShader });
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
            constants.Texels = new Vector4(shadows.TexelSizes[0], shadows.TexelSizes[1], shadows.TexelSizes[2], 1f / shadows.Size);
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
        _shadowPass = true;
        _context.PSUnsetShaderResource(5); // The shadow images are being drawn.
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        BindVertexMode(VertexMode.Shadow);
        BindConstants();
        _context.PSSetSampler(0, _samplerState);
        _context.GSSetShader(null);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_shadowDepthStencilState, 0);
        ForgetBoundStates();
        SetRasterizer(_shadowRasterizerState);
    }

    // Draws the solid and cut-out meshes of a placed model into the shadow image seen through lightViewProjection.
    public void DrawShadow(GpuModel model, Matrix4x4 world, Matrix4x4 lightViewProjection)
    {
        SetModel(model, world, lightViewProjection, Vector3.One, pickId: 0);
        FlushConstants();
        foreach (var part in model.Parts)
        {
            if (part.BlendMode is MeshBlendMode.Translucent or MeshBlendMode.Water)
                continue; // Glass, decals and water let the light through.
            if (part.BlendMode == MeshBlendMode.Cutout)
            {
                SetPixelShader(_vertexMode == VertexMode.Skinned ? _shadowSkinnedCutoutPixelShader : _shadowCutoutPixelShader);
                SetColorTexture(part.Texture ?? _untextured);
            }
            else
            {
                SetPixelShader(null);
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
            if (HiddenMeshes is { } hidden && hidden.Contains(part.Mesh))
                continue;
            bool terrain = part.Layers is not null && part.BlendMode == MeshBlendMode.Opaque;
            SetPixelShader(part.BlendMode switch
            {
                _ when terrain => NormalsOutput ? _terrainNormalPixelShader : _terrainPixelShader,
                MeshBlendMode.Cutout => NormalsOutput ? _cutoutNormalPixelShader : _cutoutPixelShader,
                MeshBlendMode.Translucent => _translucentPixelShader,
                _ => NormalsOutput ? _opaqueNormalPixelShader : _opaquePixelShader,
            });
            SetRasterizer(Wireframe ? _wireframeRasterizerState
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
            if (Lighting.IsHdr && paint != Vector3.One)
                paint = new Vector3(MathF.Pow(paint.X, 2.2f), MathF.Pow(paint.Y, 2.2f), MathF.Pow(paint.Z, 2.2f));
            bool painted = VehiclePaint is not null && part.PaintSlot != VehiclePaintSlot.None;
            var highlight = HighlightedMesh == part.Mesh ? HighlightColor : Vector3.One;
            SetTint(tint * paint * highlight);
            var baseTexture = Untextured && part.BlendMode == MeshBlendMode.Opaque ? _white
                : part.Texture ?? (part.VertexColored || painted ? _white : translucent ? _untexturedTranslucent : _untextured);
            if (terrain)
            {
                // A missing layer shows layer 0 instead; without the mask, the vertex weights alone.
                var mask = part.Layers![3];
                bool useMask = mask is not null && part.TerrainMode != TerrainBlendMode.VertexOnly;
                SetMesh(emission, part.BakedLighting, false, part.Character, new Vector4(useMask ? 1f : 0f, part.TerrainMode == TerrainBlendMode.MaskOnly ? 1f : 0f,
                    part.TerrainMode == TerrainBlendMode.Layered ? 1f : 0f, 0f));
                for (int l = 0; l < 3; l++)
                    _context.PSSetShaderResource((uint)l + 1, (part.Layers[l] ?? baseTexture).View);
                _context.PSSetShaderResource(4, (mask ?? baseTexture).View);
            }
            else
            {
                SetMesh(emission, part.BakedLighting, part.DarkeningDecal, part.Character, new Vector4(0f, 0f, part.VertexColored ? 1f : 0f, part.TintTexture is null ? 0f : 1f));
                if (part.TintTexture is not null)
                    _context.PSSetShaderResource(1, part.TintTexture.View);
            }
            SetSurface(part.NormalTexture, part.SpecularTexture, part.Shine, part.Character);
            SetColorTexture(baseTexture);
            FlushConstants();
            _context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, part.BaseVertex);
        }
    }

    // Relief and shine textures of a part (characters, and the other meshes when loaded with them).
    private void SetSurface(GpuTexture? normal, GpuTexture? specular, Vector2 shine, bool character)
    {
        var value = new Vector4(normal is null ? 0f : 1f, specular is null ? 0f : character ? 1f : 2f, shine.X, shine.Y);
        if (normal is not null)
            _context.PSSetShaderResource(6, normal.View);
        if (specular is not null)
            _context.PSSetShaderResource(7, specular.View);
        if (_constants.Surface == value)
            return;
        _constants.Surface = value;
        _constantsDirty = true;
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
        FlushConstants();
        foreach (var part in model.Parts)
        {
            if (part.DarkeningDecal)
                continue; // A shadow on the floor: the click goes to the floor.
            SetPixelShader(part.BlendMode switch
            {
                MeshBlendMode.Cutout => _pickCutoutPixelShader,
                MeshBlendMode.Translucent => _pickTranslucentPixelShader,
                _ => _pickOpaquePixelShader,
            });
            SetRasterizer(part.BlendMode == MeshBlendMode.Opaque ? _rasterizerState : _doubleSidedRasterizerState);
            SetColorTexture(part.Texture ?? _untextured);
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
                FillDirection = new Vector4(Lighting.FillDirection, 0f),
                FillColor = new Vector4(Lighting.FillColor, 0f),
                RimDirection = new Vector4(Lighting.RimDirection, 0f),
                RimColor = new Vector4(Lighting.RimColor, 0f),
                ClassicLight = new Vector4(Lighting.ClassicAmbient, Lighting.ClassicNight),
                ClassicShade = new Vector4(Lighting.ClassicShadow, 0f, 0f, 0f),
                Output = new Vector4(Lighting.IsHdr ? 1f : 0f, SceneLighting.HdrEmission, 0f, 0f),
                SkyTop = new Vector4(Lighting.Zenith, 0f),
                FogSun = new Vector4(Lighting.FogSun, 0f),
                SkySun = new Vector4(Lighting.Sun, 0f),
                Weather = new Vector4(Lighting.Wetness, Lighting.Snow, Time, 0f),
            };
            var frameMap = _context.Map(_frameBuffer, 0, MapMode.WriteDiscard);
            Unsafe.Write((void*)frameMap.DataPointer, frame);
            _context.Unmap(_frameBuffer, 0);
            _frameViewProjection = viewProjection;
            _frameWritten = true;
        }

        // Written once, just before the first draw of the model (the mesh settings below may change it again).
        _constants.World = world;
        _constants.Tint = new Vector4(tint, 1f);
        _constants.PickId = pickId;
        _constantsDirty = true;

        bool skinned = model.SkinBuffer is not null && BonePose is not null;
        var mode = skinned ? VertexMode.Skinned : _shadowPass ? VertexMode.Shadow : VertexMode.Plain;
        if (mode != _vertexMode)
            BindVertexMode(mode);
        if (skinned)
        {
            WriteBones(BonePose!, model.BonePalette);
            _context.IASetVertexBuffer(1, model.SkinBuffer!, (uint)sizeof(SkinVertex));
        }
        _context.IASetVertexBuffer(0, model.VertexBuffer, (uint)sizeof(ModelVertex));
        _context.IASetIndexBuffer(model.IndexBuffer, Format.R16_UInt, 0);
    }

    // palette: the bone of the pose each bone number of the model is (null: the same).
    private void WriteBones(Matrix4x4[] pose, int[]? palette)
    {
        int count = Math.Min(palette?.Length ?? pose.Length, MaxBones);
        var rows = _boneRows;
        for (int b = 0; b < count; b++)
        {
            int bone = palette is null ? b : palette[b];
            ref readonly var m = ref pose[bone < pose.Length ? bone : 0];
            int o = b * 12;
            rows[o] = m.M11; rows[o + 1] = m.M21; rows[o + 2] = m.M31; rows[o + 3] = m.M41;
            rows[o + 4] = m.M12; rows[o + 5] = m.M22; rows[o + 6] = m.M32; rows[o + 7] = m.M42;
            rows[o + 8] = m.M13; rows[o + 9] = m.M23; rows[o + 10] = m.M33; rows[o + 11] = m.M43;
        }
        var map = _context.Map(_boneBuffer, 0, MapMode.WriteDiscard);
        new ReadOnlySpan<float>(rows, 0, count * 12).CopyTo(new Span<float>((void*)map.DataPointer, MaxBones * 12));
        _context.Unmap(_boneBuffer, 0);
        _context.VSSetConstantBuffer(3, _boneBuffer);
    }

    private bool _frameWritten;
    private Matrix4x4 _frameViewProjection;

    // Constants of the model being drawn: only the emission and the terrain settings change from one mesh to the
    // next. Sent to the GPU only before a draw, when they changed.
    private ObjectConstants _constants;
    private bool _constantsDirty;

    private void SetTint(Vector3 tint)
    {
        var value = new Vector4(tint, 1f);
        if (_constants.Tint == value)
            return;
        _constants.Tint = value;
        _constantsDirty = true;
    }

    // bakedLighting: 0 none, 1 GTA IV (darkens nooks), 2 first trilogy pre-lit.
    private void SetMesh(float emission, int bakedLighting, bool darkeningDecal, bool character, Vector4 terrain)
    {
        var value = new Vector4(emission, bakedLighting, darkeningDecal ? 1f : 0f, character ? 1f : 0f);
        if (_constants.Emission == value && _constants.Terrain == terrain)
            return;
        _constants.Emission = value;
        _constants.Terrain = terrain;
        _constantsDirty = true;
    }

    private void FlushConstants()
    {
        if (!_constantsDirty)
            return;
        _constantsDirty = false;
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
        _shadowSkinnedCutoutPixelShader.Dispose();
        _shadowVertexShader.Dispose();
        _shadowInputLayout.Dispose();
        _inputLayout.Dispose();
        _skinnedInputLayout.Dispose();
        _skinnedVertexShader.Dispose();
        _boneBuffer.Dispose();
        _pickTranslucentPixelShader.Dispose();
        _pickCutoutPixelShader.Dispose();
        _pickOpaquePixelShader.Dispose();
        _translucentPixelShader.Dispose();
        _cutoutPixelShader.Dispose();
        _opaquePixelShader.Dispose();
        _terrainPixelShader.Dispose();
        _opaqueNormalPixelShader.Dispose();
        _terrainNormalPixelShader.Dispose();
        _cutoutNormalPixelShader.Dispose();
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
        int Mesh, GpuTexture? TintTexture, int BakedLighting, bool DarkeningDecal, bool Character, GpuTexture? NormalTexture = null,
        GpuTexture? SpecularTexture = null, Vector2 Shine = default);

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

    // Bones of each vertex (SkinVertex), for a model that follows a skeleton; null otherwise.
    internal ID3D11Buffer? SkinBuffer { get; init; }

    // Which bone of the skeleton each bone number of SkinBuffer is.
    internal int[]? BonePalette { get; init; }

    public bool IsSkinned => SkinBuffer is not null;
    internal Part[] Parts { get; }
    internal bool HasTranslucentParts { get; }

    public bool HasWaterParts { get; }

    public int TriangleCount { get; }

    public long ByteSize { get; }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        SkinBuffer?.Dispose();
    }
}

// A model's GPU buffers made off the drawing thread (ModelRenderer.Prepare), waiting for its textures. Disposed if
// it is dropped before ModelRenderer.Finish.
public sealed class PreparedModel : IDisposable
{
    internal readonly record struct Range(int Mesh, int FirstIndex, int IndexCount, int BaseVertex, MeshBlendMode BlendMode,
        MeshEmission Emission, bool Terrain, TerrainBlendMode TerrainMode, bool VertexColored, VehiclePaintSlot PaintSlot, bool Tinted,
        int BakedLighting, bool DarkeningDecal, bool Character, Vector2 Shine);

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _skinBuffer;

    internal PreparedModel(ID3D11Buffer vertexBuffer, ID3D11Buffer indexBuffer, Range[] ranges, int triangleCount, long byteSize,
        ID3D11Buffer? skinBuffer = null)
    {
        _vertexBuffer = vertexBuffer;
        _indexBuffer = indexBuffer;
        _skinBuffer = skinBuffer;
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

    internal ID3D11Buffer? TakeSkinBuffer() => Interlocked.Exchange(ref _skinBuffer, null);

    internal int[]? BonePalette { get; init; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _vertexBuffer, null)?.Dispose();
        Interlocked.Exchange(ref _indexBuffer, null)?.Dispose();
        Interlocked.Exchange(ref _skinBuffer, null)?.Dispose();
    }
}
