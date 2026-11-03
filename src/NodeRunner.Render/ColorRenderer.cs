using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Draws untextured, vertex-coloured 3D lines and triangles (grid, path nodes, navmesh, debug shapes...): <list
// type="bullet"> <item>lines that change every frame.
public sealed unsafe class ColorRenderer : IDisposable
{
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 ViewProjection;
    float3 CameraPosition;
    float FadeDistance; // 0 = no fade
};
struct VS_INPUT { float3 pos : POSITION; float4 col : COLOR0; };
struct PS_INPUT { float4 pos : SV_POSITION; float4 col : COLOR0; float3 world : TEXCOORD0; };

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(ViewProjection, float4(input.pos, 1.0f));
    output.col = input.col;
    output.world = input.pos;
    return output;
}

float4 PS(PS_INPUT input) : SV_Target
{
    float4 col = input.col;
    if (FadeDistance > 0)
        col.a *= saturate(1.0f - distance(input.world, CameraPosition) / FadeDistance);
    return col;
}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection;
        public Vector3 CameraPosition;
        public float FadeDistance;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly LineBatch _batch = new();

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11DepthStencilState _depthStencilState;
    private readonly ID3D11DepthStencilState _translucentDepthState;
    private readonly ID3D11DepthStencilState _onTopDepthState;

    private ID3D11Buffer? _vertexBuffer;
    private int _vertexBufferCapacity;

    public ColorRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        ReadOnlyMemory<byte> vsBytecode = Compiler.Compile(ShaderSource, "VS", "colors", "vs_4_0");
        ReadOnlyMemory<byte> psBytecode = Compiler.Compile(ShaderSource, "PS", "colors", "ps_4_0");
        _vertexShader = device.CreateVertexShader(vsBytecode.Span);
        _pixelShader = device.CreatePixelShader(psBytecode.Span);

        InputElementDescription[] layout =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("COLOR", 0, Format.R8G8B8A8_UNorm, 12, 0),
        ];
        _inputLayout = device.CreateInputLayout(layout, vsBytecode.Span);

        _constantBuffer = device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Constants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
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
        _blendState = device.CreateBlendState(blendDesc);

        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
            AntialiasedLineEnable = true,
        });

        // Reversed Z (see GraphicsDevice): closer = greater depth.
        _depthStencilState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.GreaterEqual,
            StencilEnable = false,
        });

        _translucentDepthState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual,
            StencilEnable = false,
        });

        _onTopDepthState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        });
    }

    public int PendingLineCount => _batch.LineCount;

    public void AddLine(Vector3 from, Vector3 to, Vector4 color) => _batch.AddLine(from, to, color);

    public void AddBox(Vector3 min, Vector3 max, Matrix4x4 transform, Vector4 color) => _batch.AddBox(min, max, transform, color);

    // Draws every pending segment, then empties the batch. fadeDistance &gt; 0 makes lines fade out with distance
    // from the camera. onTop draws them over everything, even behind walls.
    public void Flush(Matrix4x4 viewProjection, Vector3 cameraPosition, float fadeDistance = 0f, bool onTop = false)
    {
        var vertices = _batch.Vertices;
        if (vertices.Count == 0)
            return;

        EnsureCapacity(vertices.Count);

        var vbMap = _context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
        var source = CollectionsMarshal.AsSpan(vertices);
        source.CopyTo(new Span<ColorVertex>((void*)vbMap.DataPointer, source.Length));
        _context.Unmap(_vertexBuffer!, 0);

        DrawVertexBuffer(_vertexBuffer!, vertices.Count, triangles: false, viewProjection, cameraPosition, fadeDistance,
            onTop ? _onTopDepthState : null);
        _batch.Clear();
    }

    public ColorMesh? CreateMesh(LineBatch batch) => CreateMesh(batch.Vertices, triangles: false);

    public ColorMesh? CreateMesh(TriangleBatch batch) => CreateMesh(batch.Vertices, triangles: true);

    private ColorMesh? CreateMesh(List<ColorVertex> vertices, bool triangles)
    {
        if (vertices.Count == 0)
            return null;

        var buffer = _device.CreateBuffer(
            CollectionsMarshal.AsSpan(vertices),
            new BufferDescription((uint)(vertices.Count * sizeof(ColorVertex)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        return new ColorMesh(buffer, vertices.Count, triangles);
    }

    public void Draw(ColorMesh mesh, Matrix4x4 viewProjection, Vector3 cameraPosition, float fadeDistance = 0f)
    {
        DrawVertexBuffer(mesh.Buffer, mesh.VertexCount, mesh.IsTriangles, viewProjection, cameraPosition, fadeDistance);
    }

    private void DrawVertexBuffer(ID3D11Buffer buffer, int vertexCount, bool triangles, Matrix4x4 viewProjection, Vector3 cameraPosition,
        float fadeDistance, ID3D11DepthStencilState? depthState = null)
    {
        var constants = new Constants
        {
            ViewProjection = viewProjection,
            CameraPosition = cameraPosition,
            FadeDistance = fadeDistance,
        };
        var cbMap = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)cbMap.DataPointer, constants);
        _context.Unmap(_constantBuffer, 0);

        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, buffer, (uint)sizeof(ColorVertex));
        _context.IASetPrimitiveTopology(triangles ? PrimitiveTopology.TriangleList : PrimitiveTopology.LineList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.GSSetShader(null);
        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(depthState ?? (triangles ? _translucentDepthState : _depthStencilState), 0);
        _context.RSSetState(_rasterizerState);

        _context.Draw((uint)vertexCount, 0);
    }

    private void EnsureCapacity(int vertexCount)
    {
        if (_vertexBuffer is not null && _vertexBufferCapacity >= vertexCount)
            return;

        _vertexBuffer?.Dispose();
        _vertexBufferCapacity = Math.Max(vertexCount * 3 / 2, 4096);
        _vertexBuffer = _device.CreateBuffer(new BufferDescription(
            (uint)(_vertexBufferCapacity * sizeof(ColorVertex)),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _depthStencilState.Dispose();
        _translucentDepthState.Dispose();
        _onTopDepthState.Dispose();
        _rasterizerState.Dispose();
        _blendState.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}

// Vertex: position + RGBA8 colour (R in the lowest byte).
[StructLayout(LayoutKind.Sequential)]
internal readonly struct ColorVertex(Vector3 position, uint color)
{
    public readonly Vector3 Position = position;
    public readonly uint Color = color;

    public static uint Pack(Vector4 c)
    {
        uint r = (uint)(Math.Clamp(c.X, 0f, 1f) * 255f + 0.5f);
        uint g = (uint)(Math.Clamp(c.Y, 0f, 1f) * 255f + 0.5f);
        uint b = (uint)(Math.Clamp(c.Z, 0f, 1f) * 255f + 0.5f);
        uint a = (uint)(Math.Clamp(c.W, 0f, 1f) * 255f + 0.5f);
        return r | (g << 8) | (b << 16) | (a << 24);
    }
}

// A list of coloured segments being built on the CPU.
public sealed class LineBatch
{
    internal List<ColorVertex> Vertices { get; } = new();

    public int LineCount => Vertices.Count / 2;

    public void AddLine(Vector3 from, Vector3 to, Vector4 color)
    {
        uint packed = ColorVertex.Pack(color);
        Vertices.Add(new ColorVertex(from, packed));
        Vertices.Add(new ColorVertex(to, packed));
    }

    public void AddBox(Vector3 min, Vector3 max, Matrix4x4 transform, Vector4 color)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            corners[i] = Vector3.Transform(local, transform);
        }

        // Pairs of corners differing by exactly one axis bit.
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) == 0)
                    AddLine(corners[i], corners[i | bit], color);
            }
        }
    }

    public void Clear() => Vertices.Clear();
}

// A list of coloured triangles being built on the CPU.
public sealed class TriangleBatch
{
    internal List<ColorVertex> Vertices { get; } = new();

    public int TriangleCount => Vertices.Count / 3;

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector4 color)
    {
        uint packed = ColorVertex.Pack(color);
        Vertices.Add(new ColorVertex(a, packed));
        Vertices.Add(new ColorVertex(b, packed));
        Vertices.Add(new ColorVertex(c, packed));
    }

    public void Clear() => Vertices.Clear();
}

// Lines or triangles stored on the GPU.
public sealed class ColorMesh : IDisposable
{
    internal ColorMesh(ID3D11Buffer buffer, int vertexCount, bool isTriangles)
    {
        Buffer = buffer;
        VertexCount = vertexCount;
        IsTriangles = isTriangles;
    }

    internal ID3D11Buffer Buffer { get; }
    internal int VertexCount { get; }
    internal bool IsTriangles { get; }

    public void Dispose() => Buffer.Dispose();
}
