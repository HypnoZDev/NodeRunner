using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Rain and snow falling around the camera: drops (thin streaks along their fall) or flakes (soft dots swaying) in a box
// that follows the camera, each placed by the GPU from its number (nothing to upload), pushed by the wind and hidden
// behind what is in front of them.
public sealed unsafe class PrecipitationRenderer : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 ViewProjection; // Camera-relative point -> image.
    float4 Right;            // xyz: camera's right, w: 1 = snow, 0 = rain.
    float4 Up;               // xyz: camera's up, w: time (s), for the flakes' swaying.
    float4 Shift;            // xyz: how far the particles moved (box sizes), w: box size (m).
    float4 Fall;             // xyz: direction of the fall, w: length of a streak (m).
    float4 Color;            // rgb: colour, a: opacity.
    float4 Size;             // x: width of a drop / flake (m), y: size of a pixel 1 m away (m).
};

uint3 Scramble(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

struct PS_INPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float alpha : ALPHA; };

PS_INPUT VS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    PS_INPUT output;
    float2 corner = float2((vertex & 1u) != 0u ? 1.0f : -1.0f, (vertex & 2u) != 0u ? 1.0f : -1.0f);
    output.uv = corner;
    float3 random = float3(Scramble(uint3(instance, instance * 7u + 3u, instance * 13u + 11u))) * (1.0f / 4294967296.0f);
    float box = Shift.w;
    float3 center = (frac(random + Shift.xyz) - 0.5f) * box;
    bool snow = Right.w > 0.5f;
    if (snow)
        center += float3(sin(Up.w * 1.1f + random.x * 40.0f), cos(Up.w * 0.9f + random.y * 40.0f), 0.0f) * 0.25f;
    float distance = length(center);
    // Fade in out of the camera, out towards the box's edge (a sphere: the box's corners never show).
    output.alpha = saturate((distance - 0.8f) / 1.7f) * (1.0f - smoothstep(0.3f * box, 0.48f * box, distance)) * Color.a;
    // At least about a pixel wide, fainter when widened.
    float pixel = distance * Size.y;
    float width = max(Size.x, pixel);
    output.alpha *= Size.x / width;
    float3 offset;
    if (snow)
    {
        offset = (Right.xyz * corner.x + Up.xyz * corner.y) * width;
    }
    else
    {
        float3 side = cross(Fall.xyz, center);
        side = dot(side, side) > 1e-8f ? normalize(side) : Right.xyz;
        offset = side * (corner.x * width) + Fall.xyz * (corner.y * 0.5f * max(Fall.w, pixel));
    }
    output.pos = mul(ViewProjection, float4(center + offset, 1.0f));
    return output;
}

float4 PS(PS_INPUT input) : SV_Target
{
    float a;
    if (Right.w > 0.5f)
    {
        float r2 = dot(input.uv, input.uv);
        a = saturate(1.0f - r2);
        a *= a;
    }
    else
        a = (1.0f - input.uv.x * input.uv.x) * (1.0f - input.uv.y * input.uv.y);
    return float4(Color.rgb, a * input.alpha);
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Shift;
        public Vector4 Fall;
        public Vector4 Color;
        public Vector4 Size;
    }

    // Box around the camera (m), particles in it, falling speed (m/s), share of the clouds' wind felt near the ground,
    // width (m), opacity.
    private readonly record struct Kind(float Box, int Count, float Speed, float WindShare, float Width, float Opacity);

    private static readonly Kind Rain = new(30f, 16000, 9f, 0.35f, 0.006f, 0.32f);
    private static readonly Kind Snow = new(24f, 30000, 1.1f, 0.25f, 0.03f, 0.9f);

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11DepthStencilState _testDepth;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private Vector3D _moved;

    public PrecipitationRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        _vertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VS", "precipitation", "vs_5_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "precipitation", "ps_5_0").Span);
        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blend = device.CreateBlendState(blend);
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

    // Draws the weather's rain or snow (nothing for the others) into the bound image and depth. viewProjection: the one
    // the scene is drawn with; height: of the image in pixels; amount: share of the particles drawn (0 to 1).
    public void Draw(Weather weather, CloudWeather sky, SceneLighting lighting, Camera camera, Matrix4x4 viewProjection, int height,
        float amount)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float elapsed = (float)Math.Clamp(now - _last, 0.0, 0.1);
        _last = now;
        bool snow = weather == Weather.Snow;
        if (weather is not (Weather.Rain or Weather.Thunder or Weather.Snow) || amount <= 0f)
            return;
        var kind = snow ? Snow : Rain;
        float heavy = weather == Weather.Thunder ? 1.4f : 1f;

        var wind = Vector3.Normalize(new Vector3(1f, 0.35f, 0f)) * (sky.Wind * kind.WindShare * heavy);
        var velocity = new Vector3(0f, 0f, -kind.Speed * (snow ? 1f : heavy)) + wind;
        _moved = (_moved + new Vector3D(velocity) * (elapsed / kind.Box)).Wrapped();
        var shift = (_moved - new Vector3D(camera.Position) / kind.Box).Wrapped();

        var forward = Vector3.Normalize(camera.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Cross(right, forward);
        // Lit by the grey sky above them, a little by the sun through the clouds.
        var color = lighting.SkyAmbient * (snow ? 1.1f : 0.9f) + lighting.SunColor * 0.4f;
        float pixel = 2f * MathF.Tan(camera.FieldOfView / 2f) / Math.Max(height, 1);
        var constants = new Constants
        {
            ViewProjection = Matrix4x4.CreateTranslation(camera.Position) * viewProjection,
            Right = new Vector4(right, snow ? 1f : 0f),
            Up = new Vector4(up, (float)(now % 3600.0)),
            Shift = new Vector4(shift.ToVector3(), kind.Box),
            Fall = new Vector4(Vector3.Normalize(velocity), velocity.Length() * 0.035f),
            Color = new Vector4(color, kind.Opacity),
            Size = new Vector4(kind.Width, pixel, 0f, 0f),
        };
        var map = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)map.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(_vertexShader);
        _context.GSSetShader(null);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.OMSetBlendState(_blend, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_testDepth, 0);
        _context.RSSetState(_rasterizerState);
        int count = (int)(kind.Count * heavy * Math.Clamp(amount, 0f, 1f));
        _context.DrawInstanced(4, (uint)count, 0, 0);
    }

    // Distance moved in box sizes, kept in doubles and wrapped to 0..1: no precision lost after hours.
    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public Vector3D(Vector3 v) : this(v.X, v.Y, v.Z) { }
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vector3D operator /(Vector3D a, double s) => new(a.X / s, a.Y / s, a.Z / s);
        public Vector3D Wrapped() => new(X - Math.Floor(X), Y - Math.Floor(Y), Z - Math.Floor(Z));
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    }

    public void Dispose()
    {
        _rasterizerState.Dispose();
        _testDepth.Dispose();
        _blend.Dispose();
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
