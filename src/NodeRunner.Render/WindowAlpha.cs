using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Alpha of the window's pixels when Windows shows its backdrop (Mica) through them: the 3D view made fully opaque
// (models and sky leave any alpha), then the rest of the window multiplied by an opacity (premultiplied alpha).
public sealed class WindowAlpha : IDisposable
{
    private const string ShaderSource = """
        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float4 PS() : SV_Target { return float4(0, 0, 0, 1); }
        """;

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11BlendState _opaqueBlend;
    private readonly ID3D11BlendState _fadeBlend;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11DepthStencilState _depthState;

    public WindowAlpha(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        _vertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VS", "window_alpha", "vs_4_0").Span);
        _pixelShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "PS", "window_alpha", "ps_4_0").Span);

        var opaque = new BlendDescription();
        opaque.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.Zero,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.Alpha,
        };
        _opaqueBlend = device.CreateBlendState(opaque);

        // destination * blend factor, colour and alpha alike.
        var fade = new BlendDescription();
        fade.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.Zero,
            DestinationBlend = Blend.BlendFactor,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.BlendFactor,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _fadeBlend = device.CreateBlendState(fade);

        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = false,
            DepthClipEnable = true,
        });
        _depthState = device.CreateDepthStencilState(DepthStencilDescription.None);
    }

    // Alpha = 1 inside the rectangle.
    public void MakeOpaque(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;
        Bind(_opaqueBlend, 1f);
        Fill(x, y, width, height);
    }

    // Multiplies every pixel outside the rectangle by the opacity.
    public void Fade(int targetWidth, int targetHeight, int x, int y, int width, int height, float opacity)
    {
        Bind(_fadeBlend, opacity);
        int right = x + Math.Max(width, 0), bottom = y + Math.Max(height, 0);
        if (width <= 0 || height <= 0)
        {
            Fill(0, 0, targetWidth, targetHeight);
            return;
        }
        Fill(0, 0, targetWidth, y);
        Fill(0, bottom, targetWidth, targetHeight - bottom);
        Fill(0, y, x, height);
        Fill(right, y, targetWidth - right, height);
    }

    private void Bind(ID3D11BlendState blend, float factor)
    {
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.OMSetBlendState(blend, new Color4(factor, factor, factor, factor), uint.MaxValue);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.RSSetState(_rasterizerState);
    }

    private void Fill(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;
        _context.RSSetViewport(new Viewport(x, y, width, height));
        _context.Draw(3, 0);
    }

    public void Dispose()
    {
        _vertexShader.Dispose();
        _pixelShader.Dispose();
        _opaqueBlend.Dispose();
        _fadeBlend.Dispose();
        _rasterizerState.Dispose();
        _depthState.Dispose();
    }
}
