using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Clouds in volume: for each pixel of a smaller image, a ray goes through the cloud layer (a shell around the Earth's
// curve), adding up the light the clouds send towards the eye (sun through the cloud above each point, light of the sky)
// and how much of the sky behind still shows. The sky is then drawn over with it (SkyRenderer.Draw). The cloud shapes
// come from CloudNoise, ready a moment after start-up: no clouds until then.
public sealed unsafe class CloudRenderer : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 InverseViewProjection;
    float4 Eye;      // xyz: camera, w: frame (0: the same steps every frame)
    float4 Layer;    // x: base (m), y: thickness (m), z: coverage, w: density (per metre)
    float4 Look;     // x: heaps, y: dark, z: steps, w: steps towards the sun
    float4 Wind;     // xyz: drift of the shapes (m)
    float4 SunDir;   // xyz: towards the light (sun or moon)
    float4 SunLight; // rgb: its colour
    float4 SkyLight; // rgb: light from the sky above
    float4 GroundLight; // rgb: light from below
};
Texture3D<float2> Shape : register(t0);
Texture2D<float2> WeatherMap : register(t1);
SamplerState Wrap : register(s0);

static const float Earth = 6360000.0f;
static const float ShapeScale = 4200.0f;
static const float DetailScale = 840.0f;
static const float DetailDistance = 14000.0f; // Beyond, the frayed edges would only flicker.
static const float WeatherScale = 24000.0f;
static const float FarthestStart = 60000.0f;
static const float LongestRay = 16000.0f;
static const float FadeStart = 6000.0f;
static const float FadeDistance = 22000.0f;

struct PS_INPUT { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

PS_INPUT VS(uint id : SV_VertexID)
{
    PS_INPUT output;
    float2 p = float2((id << 1) & 2, id & 2) * 2.0f - 1.0f;
    output.pos = float4(p, 0.0f, 1.0f);
    output.ndc = p;
    return output;
}

// Distances along d from o (relative to the Earth's centre) to the sphere of radius r; negative: none.
float2 Sphere(float3 o, float3 d, float r)
{
    float b = dot(o, d);
    float h = b * b - (dot(o, o) - r * r);
    if (h < 0.0f)
        return float2(-1.0f, -1.0f);
    h = sqrt(h);
    return float2(-b - h, -b + h);
}

float Remap(float v, float a, float b, float c, float d) { return c + (v - a) / (b - a) * (d - c); }

// Cloud density (per metre) at world point p, height 0 to 1 in the layer; detail: frayed edges.
float Density(float3 p, float height, bool detail)
{
    if (height <= 0.0f || height >= 1.0f)
        return 0.0f;
    float2 w = WeatherMap.SampleLevel(Wrap, (p.xy + Wind.xy) / WeatherScale, 0);
    float coverage = saturate(Layer.z + (w.x - 0.5f) * 1.1f * (1.0f - Layer.z * Layer.z));
    float heaps = saturate(Look.x + (w.y - 0.5f) * 0.7f);
    // Flat bottom, rounded top, lower for flat layers.
    float top = lerp(0.3f, 1.0f, heaps);
    float profile = saturate(Remap(height, 0.0f, 0.07f, 0.0f, 1.0f)) * saturate(Remap(height, top * 0.55f, top, 1.0f, 0.0f));
    float shape = Shape.SampleLevel(Wrap, (p + Wind.xyz) / ShapeScale, 0).x * profile;
    float cloud = saturate(Remap(shape, 1.0f - coverage, 1.0f, 0.0f, 1.0f));
    if (detail && cloud > 0.0f)
    {
        float d = Shape.SampleLevel(Wrap, (p + Wind.xyz * 1.5f) / DetailScale, 0).y;
        // Wisps at the bottom, billows above.
        float erode = lerp(1.0f - d, d, saturate(height * 3.0f)) * 0.4f;
        cloud = saturate(Remap(cloud, erode, 1.0f, 0.0f, 1.0f));
    }
    return cloud * Layer.w;
}

// Henyey-Greenstein, 1 for light scattered evenly.
float Phase(float mu, float g)
{
    float g2 = g * g;
    return (1.0f - g2) / pow(abs(1.0f + g2 - 2.0f * g * mu), 1.5f);
}

// Light from the sun at a point behind an optical depth od towards it, with the light scattered several times inside
// (each time weaker, less absorbed, less forward).
float SunScatter(float od, float mu)
{
    float sum = 0.0f, a = 1.0f, b = 1.0f, c = 1.0f;
    [unroll] for (int k = 0; k < 3; k++)
    {
        sum += a * exp(-b * od) * lerp(Phase(mu, 0.7f * c), Phase(mu, -0.25f * c), 0.35f);
        a *= 0.5f; b *= 0.4f; c *= 0.5f;
    }
    return sum;
}

float4 PS(PS_INPUT input) : SV_Target
{
    float4 a = mul(InverseViewProjection, float4(input.ndc, 1.0f, 1.0f));
    float4 b = mul(InverseViewProjection, float4(input.ndc, 0.5f, 1.0f));
    float3 ray = normalize(b.xyz / b.w - a.xyz / a.w);

    float height = Earth + max(Eye.z, 1.0f);
    float3 o = float3(0.0f, 0.0f, height);
    float inner = Earth + Layer.x, outer = inner + Layer.y;
    float2 hitIn = Sphere(o, ray, inner), hitOut = Sphere(o, ray, outer);
    float start, end;
    if (height < inner)
    {
        if (Sphere(o, ray, Earth).x > 0.0f)
            return float4(0.0f, 0.0f, 0.0f, 1.0f); // The ground first.
        start = hitIn.y;
        end = hitOut.y;
    }
    else if (height < outer)
    {
        start = 0.0f;
        end = hitIn.x > 0.0f ? hitIn.x : hitOut.y;
    }
    else
    {
        if (hitOut.x < 0.0f)
            return float4(0.0f, 0.0f, 0.0f, 1.0f);
        start = hitOut.x;
        end = hitIn.x > 0.0f ? hitIn.x : hitOut.y;
    }
    if (start > FarthestStart || end <= start)
        return float4(0.0f, 0.0f, 0.0f, 1.0f);
    end = min(end, start + LongestRay);

    int steps = (int)Look.z, lightSteps = (int)Look.w;
    float dt = (end - start) / steps;
    // A different start along the step for neighbouring pixels (and frames): smooth instead of banded.
    float jitter = frac(52.9829189f * frac(dot(input.pos.xy, float2(0.06711056f, 0.00583715f))) + Eye.w * 0.61803399f);
    float3 sun = normalize(SunDir.xyz);
    float mu = dot(ray, sun);
    float albedo = lerp(0.98f, 0.75f, Look.y);
    float lightStep = Layer.y * 0.6f / max(lightSteps, 1);
    float3 origin = float3(Eye.xy, 0.0f);

    float3 light = 0.0f;
    float transmittance = 1.0f, near = 0.0f, weight = 0.0f;
    float t = start + dt * jitter;
    [loop] for (int i = 0; i < steps; i++)
    {
        float3 p = o + ray * t;
        float h = (length(p) - inner) / Layer.y;
        float3 world = origin + float3(p.xy, p.z - Earth);
        float sigma = Density(world, h, t < DetailDistance);
        if (sigma > 0.0f)
        {
            float od = 0.0f;
            [loop] for (int j = 0; j < lightSteps; j++)
            {
                float s = lightStep * (j + 0.5f);
                float3 q = p + sun * s;
                od += Density(world + sun * s, (length(q) - inner) / Layer.y, j < 2) * lightStep;
            }
            float3 lit = SunLight.rgb * SunScatter(od, mu) + SkyLight.rgb * lerp(0.6f, 1.1f, saturate(h)) + GroundLight.rgb * 0.3f * (1.0f - saturate(h));
            float3 source = lit * albedo;
            float step = exp(-sigma * dt);
            light += transmittance * (source - source * step);
            near += t * transmittance * (1.0f - step);
            weight += transmittance * (1.0f - step);
            transmittance *= step;
            if (transmittance < 0.01f)
                break;
        }
        t += dt;
    }

    // Far clouds fade into the haze of the horizon.
    float distance = weight > 0.0f ? near / weight : start;
    float fade = exp(-max(distance - FadeStart, 0.0f) / FadeDistance);
    return float4(light * fade, lerp(1.0f, transmittance, fade));
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 InverseViewProjection;
        public Vector4 Eye;
        public Vector4 Layer;
        public Vector4 Look;
        public Vector4 Wind;
        public Vector4 SunDir;
        public Vector4 SunLight;
        public Vector4 SkyLight;
        public Vector4 GroundLight;
    }

    // Cloud image size (fraction of the scene's), steps along each ray and towards the sun, for each quality (1 to 3).
    private static readonly (int Divider, int Steps, int LightSteps)[] Qualities = [(4, 24, 3), (2, 48, 5), (2, 72, 6)];

    // Drift repeats after this many metres, a multiple of every noise's size (shapes, details, weather map).
    private const float WindPeriod = 168000f;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11SamplerState _wrap;
    private readonly Task<(byte[] Shape, byte[] Weather)> _noise;
    private ID3D11Texture3D? _shape;
    private ID3D11ShaderResourceView? _shapeRead;
    private ID3D11Texture2D? _weather;
    private ID3D11ShaderResourceView? _weatherRead;
    private ID3D11Texture2D? _image;
    private ID3D11ShaderResourceView? _imageRead;
    private ID3D11RenderTargetView? _imageWrite;
    private int _width, _height;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTime;
    private Vector3 _drift;
    private int _frame;

    public CloudRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _vertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VS", "clouds", "vs_5_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "clouds", "ps_5_0").Span);
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
        _wrap = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
        _noise = Task.Run(() => (CloudNoise.Shape(), CloudNoise.Weather()));
    }

    // The cloud shapes are made (false for a moment after start-up).
    public bool IsReady => _shapeRead is not null || _noise.IsCompletedSuccessfully;

    // Draws the clouds seen with viewProjection into their own image, for a scene image of width x height pixels;
    // quality 1 to 3; moving: a different step pattern each frame (smoothed by the TAA). Binds its own image: bind the
    // scene's image again after. Returns the image to give SkyRenderer.Draw, or null (no clouds yet).
    public ID3D11ShaderResourceView? Render(Matrix4x4 viewProjection, Vector3 camera, SceneLighting lighting, CloudWeather weather,
        int width, int height, int quality, bool moving)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float elapsed = (float)Math.Clamp(now - _lastTime, 0.0, 0.25);
        _lastTime = now;
        var windDirection = Vector3.Normalize(new Vector3(1f, 0.35f, 0.04f));
        _drift += windDirection * weather.Wind * elapsed;
        _drift = new Vector3(_drift.X % WindPeriod, _drift.Y % WindPeriod, _drift.Z % WindPeriod);

        if (!EnsureNoise() || !Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var (divider, steps, lightSteps) = Qualities[Math.Clamp(quality, 1, Qualities.Length) - 1];
        EnsureImage(Math.Max(1, width / divider), Math.Max(1, height / divider));
        if (moving)
            _frame = (_frame + 1) % 64;

        // Light reaching the clouds from above: the sun before the weather dims it on the ground, from where it really
        // is (a low sun lights the clouds from below; the map's light never comes from below the horizon).
        var sunDirection = lighting.Sun.Z > -0.1f && lighting.Sun != Vector3.Zero ? Vector3.Normalize(lighting.Sun) : lighting.SunDirection;
        // High up, the sun just set still reddens them.
        var tint = lighting.PhysicalSky ? Atmosphere.Transmittance(Vector3.Normalize(sunDirection with { Z = MathF.Max(sunDirection.Z, 0.035f) })) : Vector3.One;
        var sunLight = lighting.SunColor * tint;
        // At night the moonlit clouds stay a little above the dark sky, greyer than the moonlight.
        float night = Math.Clamp(lighting.NightLights, 0f, 1f);
        var moon = Vector3.Lerp(sunLight, new Vector3(Luminance(sunLight)), 0.6f);
        sunLight = Vector3.Lerp(sunLight, moon * 0.35f, night);
        var skyLight = lighting.SkyAmbient * (1f - 0.6f * night);
        var groundLight = lighting.GroundAmbient * (1f - 0.6f * night);
        Write(new Constants
        {
            InverseViewProjection = inverse,
            Eye = new Vector4(camera, moving ? _frame : 0f),
            Layer = new Vector4(weather.Base, weather.Thickness, Math.Clamp(weather.Coverage, 0f, 1f), weather.Density * 0.03f),
            Look = new Vector4(weather.Heaps, weather.Dark, steps, lightSteps),
            Wind = new Vector4(_drift, 0f),
            SunDir = new Vector4(sunDirection, 0f),
            SunLight = new Vector4(sunLight, 0f),
            SkyLight = new Vector4(skyLight, 0f),
            GroundLight = new Vector4(groundLight, 0f),
        });

        _context.OMSetRenderTargets(_imageWrite!);
        _context.RSSetViewport(new Viewport(0, 0, _width, _height));
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _shapeRead!);
        _context.PSSetShaderResource(1, _weatherRead!);
        _context.PSSetSampler(0, _wrap);
        _context.GSSetShader(null);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.RSSetState(_rasterizerState);
        _context.Draw(3, 0);
        _context.PSUnsetShaderResource(0);
        _context.PSUnsetShaderResource(1);
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        return _imageRead;
    }

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private bool EnsureNoise()
    {
        if (_shapeRead is not null)
            return true;
        if (!_noise.IsCompletedSuccessfully)
            return false;
        var (shape, weather) = _noise.Result;
        const int s = CloudNoise.ShapeSize, w = CloudNoise.WeatherSize;
        fixed (byte* data = shape)
        {
            _shape = _device.CreateTexture3D(new Texture3DDescription
            {
                Width = s,
                Height = s,
                Depth = s,
                MipLevels = 1,
                Format = Format.R8G8_UNorm,
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, [new SubresourceData(data, s * 2, s * s * 2)]);
        }
        fixed (byte* data = weather)
        {
            _weather = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = w,
                Height = w,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            }, [new SubresourceData(data, w * 2)]);
        }
        _shapeRead = _device.CreateShaderResourceView(_shape);
        _weatherRead = _device.CreateShaderResourceView(_weather);
        return true;
    }

    private void EnsureImage(int width, int height)
    {
        if (_image is not null && width == _width && height == _height)
            return;
        DisposeImage();
        _width = width;
        _height = height;
        _image = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _imageRead = _device.CreateShaderResourceView(_image);
        _imageWrite = _device.CreateRenderTargetView(_image);
    }

    private void Write(Constants constants)
    {
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);
    }

    private void DisposeImage()
    {
        _imageWrite?.Dispose();
        _imageRead?.Dispose();
        _image?.Dispose();
        (_imageWrite, _imageRead, _image) = (null, null, null);
    }

    public void Dispose()
    {
        DisposeImage();
        _weatherRead?.Dispose();
        _weather?.Dispose();
        _shapeRead?.Dispose();
        _shape?.Dispose();
        _wrap.Dispose();
        _blendState.Dispose();
        _rasterizerState.Dispose();
        _depthState.Dispose();
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
