using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Draws Dear ImGui's output with Direct3D 11.
public sealed unsafe class ImGuiRenderer : IDisposable
{
    // Vertex layout of ImDrawVert: pos (2 floats), uv (2 floats), col (RGBA8).
    private const string ShaderSource = @"
cbuffer Constants : register(b0) { float4x4 ProjectionMatrix; };
struct VS_INPUT  { float2 pos : POSITION; float4 col : COLOR0; float2 uv : TEXCOORD0; };
struct PS_INPUT  { float4 pos : SV_POSITION; float4 col : COLOR0; float2 uv : TEXCOORD0; };
sampler sampler0 : register(s0);
Texture2D texture0 : register(t0);

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.0f, 1.0f));
    output.col = input.col;
    output.uv  = input.uv;
    return output;
}

float4 PS(PS_INPUT input) : SV_Target
{
    return input.col * texture0.Sample(sampler0, input.uv);
}";

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private ID3D11VertexShader _vertexShader = null!;
    private ID3D11PixelShader _pixelShader = null!;
    private ID3D11InputLayout _inputLayout = null!;
    private ID3D11Buffer _constantBuffer = null!;
    private ID3D11BlendState _blendState = null!;
    private ID3D11RasterizerState _rasterizerState = null!;
    private ID3D11DepthStencilState _depthStencilState = null!;
    private ID3D11SamplerState _fontSampler = null!;
    private ID3D11ShaderResourceView _fontTextureView = null!;

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _vertexBufferSize = 5000;
    private int _indexBufferSize = 10000;

    // Textures that ImGui can reference by id (font atlas today, game textures later).
    private readonly Dictionary<IntPtr, ID3D11ShaderResourceView> _textures = new();

    public ImGuiRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceObjects();
        CreateFontTexture();
    }

    public void Render(ImDrawDataPtr drawData)
    {
        // Avoid rendering when minimized.
        if (drawData.DisplaySize.X <= 0.0f || drawData.DisplaySize.Y <= 0.0f || drawData.CmdListsCount == 0)
            return;

        EnsureBufferSizes(drawData.TotalVtxCount, drawData.TotalIdxCount);
        UploadGeometry(drawData);
        SetupRenderState(drawData);

        // Render command lists, using the vertex/index offsets because all lists share one buffer.
        int globalVtxOffset = 0;
        int globalIdxOffset = 0;
        Vector2 clipOff = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale; // Points -> pixels.

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            for (int i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                var cmd = cmdList.CmdBuffer[i];
                if (cmd.UserCallback != IntPtr.Zero)
                    continue; // No custom callbacks used in NodeRunner.

                var clipMin = new Vector2(cmd.ClipRect.X - clipOff.X, cmd.ClipRect.Y - clipOff.Y) * clipScale;
                var clipMax = new Vector2(cmd.ClipRect.Z - clipOff.X, cmd.ClipRect.W - clipOff.Y) * clipScale;
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
                    continue;

                // Vortice's overload takes x, y, width, height (not right, bottom).
                int left = (int)clipMin.X, top = (int)clipMin.Y;
                _context.RSSetScissorRect(left, top, (int)clipMax.X - left, (int)clipMax.Y - top);

                if (_textures.TryGetValue(cmd.TextureId, out var texture))
                    _context.PSSetShaderResource(0, texture);

                _context.DrawIndexed(cmd.ElemCount, (uint)(cmd.IdxOffset + globalIdxOffset), (int)(cmd.VtxOffset + globalVtxOffset));
            }

            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }
    }

    public IntPtr RegisterTexture(ID3D11ShaderResourceView view)
    {
        var id = view.NativePointer;
        _textures[id] = view;
        return id;
    }

    public void UnregisterTexture(IntPtr id) => _textures.Remove(id);

    private void EnsureBufferSizes(int vertexCount, int indexCount)
    {
        if (_vertexBuffer is null || _vertexBufferSize < vertexCount)
        {
            _vertexBuffer?.Dispose();
            _vertexBufferSize = vertexCount + 5000;
            _vertexBuffer = _device.CreateBuffer(new BufferDescription(
                (uint)(_vertexBufferSize * sizeof(ImDrawVert)),
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));
        }

        if (_indexBuffer is null || _indexBufferSize < indexCount)
        {
            _indexBuffer?.Dispose();
            _indexBufferSize = indexCount + 10000;
            _indexBuffer = _device.CreateBuffer(new BufferDescription(
                (uint)(_indexBufferSize * sizeof(ushort)),
                BindFlags.IndexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));
        }
    }

    private void UploadGeometry(ImDrawDataPtr drawData)
    {
        var vtxMap = _context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
        var idxMap = _context.Map(_indexBuffer!, 0, MapMode.WriteDiscard);

        var vtxDst = (ImDrawVert*)vtxMap.DataPointer;
        var idxDst = (ushort*)idxMap.DataPointer;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            int vtxBytes = cmdList.VtxBuffer.Size * sizeof(ImDrawVert);
            int idxBytes = cmdList.IdxBuffer.Size * sizeof(ushort);
            Unsafe.CopyBlockUnaligned(vtxDst, (void*)cmdList.VtxBuffer.Data, (uint)vtxBytes);
            Unsafe.CopyBlockUnaligned(idxDst, (void*)cmdList.IdxBuffer.Data, (uint)idxBytes);
            vtxDst += cmdList.VtxBuffer.Size;
            idxDst += cmdList.IdxBuffer.Size;
        }

        _context.Unmap(_vertexBuffer!, 0);
        _context.Unmap(_indexBuffer!, 0);

        // Orthographic projection: ImGui's display rectangle -> clip space.
        float l = drawData.DisplayPos.X;
        float r = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float t = drawData.DisplayPos.Y;
        float b = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        var projection = new Matrix4x4(
            2.0f / (r - l), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (t - b), 0.0f, 0.0f,
            0.0f, 0.0f, 0.5f, 0.0f,
            (r + l) / (l - r), (t + b) / (b - t), 0.5f, 1.0f);

        var cbMap = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Unsafe.Write((void*)cbMap.DataPointer, projection);
        _context.Unmap(_constantBuffer, 0);
    }

    private void SetupRenderState(ImDrawDataPtr drawData)
    {
        var size = drawData.DisplaySize * drawData.FramebufferScale;
        _context.RSSetViewport(new Viewport(0, 0, size.X, size.Y));

        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, _vertexBuffer!, (uint)sizeof(ImDrawVert));
        _context.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetSampler(0, _fontSampler);
        _context.GSSetShader(null);
        _context.HSSetShader(null);
        _context.DSSetShader(null);
        _context.CSSetShader(null);

        _context.OMSetBlendState(_blendState, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        _context.OMSetDepthStencilState(_depthStencilState, 0);
        _context.RSSetState(_rasterizerState);
    }

    private void CreateDeviceObjects()
    {
        // Shaders are compiled at startup with d3dcompiler_47.dll (part of Windows 10/11).
        ReadOnlyMemory<byte> vsBytecode = Compiler.Compile(ShaderSource, "VS", "imgui", "vs_4_0");
        ReadOnlyMemory<byte> psBytecode = Compiler.Compile(ShaderSource, "PS", "imgui", "ps_4_0");

        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        InputElementDescription[] layout =
        [
            new("POSITION", 0, Format.R32G32_Float, 0, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0),
        ];
        _inputLayout = _device.CreateInputLayout(layout, vsBytecode.Span);

        _constantBuffer = _device.CreateBuffer(new BufferDescription(
            (uint)sizeof(Matrix4x4), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        var blendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
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
        _blendState = _device.CreateBlendState(blendDesc);

        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = true,
            DepthClipEnable = true,
        });

        _depthStencilState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        });

        _fontSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Always,
            MaxLOD = float.MaxValue,
        });
    }

    // After the fonts were loaded again (display scale changed).
    public void RebuildFontTexture()
    {
        UnregisterTexture(_fontTextureView.NativePointer);
        _fontTextureView.Dispose();
        CreateFontTexture();
    }

    private void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        var initData = new SubresourceData(pixels, (uint)(width * bytesPerPixel));
        using var texture = _device.CreateTexture2D(texDesc, [initData]);
        _fontTextureView = _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
        {
            Format = texDesc.Format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });

        io.Fonts.SetTexID(RegisterTexture(_fontTextureView));
        io.Fonts.ClearTexData(); // The CPU copy is no longer needed.
    }

    public void Dispose()
    {
        _textures.Clear();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _fontTextureView.Dispose();
        _fontSampler.Dispose();
        _depthStencilState.Dispose();
        _rasterizerState.Dispose();
        _blendState.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
