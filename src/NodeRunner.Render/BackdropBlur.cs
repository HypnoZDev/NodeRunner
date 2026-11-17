using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Frosted glass: a blurred copy of a rectangle of the picture drawn so far (the 3D view), at a quarter of its size,
// shown behind what floats over it. Downsampled once, then blurred horizontally and vertically, twice.
public sealed class BackdropBlur : IDisposable
{
    public const int Downscale = 4;
    private const int Passes = 2;

    private const string ShaderSource = """
        Texture2D Source : register(t0);
        SamplerState Linear : register(s0);
        cbuffer BlurStep : register(b0)
        {
            float4 Region;    // uv offset (xy) and scale (zw) of the area read from the source
            float2 Direction; // one texel of the source along the blur, or zero for the downsample
            float2 Padding;
        };

        struct Varyings { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

        Varyings VS(uint id : SV_VertexID)
        {
            Varyings o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.Uv = Region.xy + uv * Region.zw;
            return o;
        }

        float4 Downsample(Varyings i) : SV_Target
        {
            return float4(Source.Sample(Linear, i.Uv).rgb, 1);
        }

        // 9-tap Gaussian in 5 bilinear samples.
        float4 Blur(Varyings i) : SV_Target
        {
            float3 c = Source.Sample(Linear, i.Uv).rgb * 0.2270270270;
            c += Source.Sample(Linear, i.Uv + Direction * 1.3846153846).rgb * 0.3162162162;
            c += Source.Sample(Linear, i.Uv - Direction * 1.3846153846).rgb * 0.3162162162;
            c += Source.Sample(Linear, i.Uv + Direction * 3.2307692308).rgb * 0.0702702703;
            c += Source.Sample(Linear, i.Uv - Direction * 3.2307692308).rgb * 0.0702702703;
            return float4(c, 1);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _downsampleShader;
    private readonly ID3D11PixelShader _blurShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly Target[] _targets = new Target[2];

    public BackdropBlur(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _vertexShader = device.CreateVertexShader(Compiler.Compile(ShaderSource, "VS", "backdrop_blur", "vs_4_0").Span);
        _downsampleShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "Downsample", "backdrop_blur", "ps_4_0").Span);
        _blurShader = device.CreatePixelShader(Compiler.Compile(ShaderSource, "Blur", "backdrop_blur", "ps_4_0").Span);
        _constants = device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _blendState = device.CreateBlendState(BlendDescription.Opaque);
        _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });
        _depthState = device.CreateDepthStencilState(DepthStencilDescription.None);
    }

    // The blurred image, valid after Render until the next one (null before the first).
    public ID3D11ShaderResourceView? View => _targets[0].View;

    // Blurs the rectangle (pixels) of the picture drawn so far into View, then binds the back buffer again.
    public void Render(GraphicsDevice graphics, int x, int y, int width, int height)
    {
        if (width < Downscale * 2 || height < Downscale * 2)
            return;
        int smallWidth = (width + Downscale - 1) / Downscale, smallHeight = (height + Downscale - 1) / Downscale;
        for (int i = 0; i < _targets.Length; i++)
            _targets[i].Resize(_device, smallWidth, smallHeight);

        var source = graphics.CopyColor();
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);
        _context.OMSetBlendState(_blendState);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.RSSetState(_rasterizerState);
        _context.RSSetViewport(new Viewport(0, 0, smallWidth, smallHeight));

        Draw(_downsampleShader, source, _targets[0],
            new Vector4Data((float)x / graphics.Width, (float)y / graphics.Height, (float)width / graphics.Width, (float)height / graphics.Height), 0f, 0f);
        var whole = new Vector4Data(0f, 0f, 1f, 1f);
        for (int pass = 0; pass < Passes; pass++)
        {
            Draw(_blurShader, _targets[0].View!, _targets[1], whole, 1f / smallWidth, 0f);
            Draw(_blurShader, _targets[1].View!, _targets[0], whole, 0f, 1f / smallHeight);
        }

        _context.PSSetShaderResource(0, null!);
        graphics.BindBackBuffer();
    }

    private void Draw(ID3D11PixelShader shader, ID3D11ShaderResourceView source, Target target, Vector4Data region, float dx, float dy)
    {
        // A view still bound as a shader input can't be a render target: unbind before switching.
        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets(target.RenderView!);
        var map = _context.Map(_constants, 0, MapMode.WriteDiscard);
        unsafe
        {
            var data = (float*)map.DataPointer;
            data[0] = region.X; data[1] = region.Y; data[2] = region.Z; data[3] = region.W;
            data[4] = dx; data[5] = dy; data[6] = 0f; data[7] = 0f;
        }
        _context.Unmap(_constants, 0);
        _context.PSSetShader(shader);
        _context.PSSetShaderResource(0, source);
        _context.Draw(3, 0);
    }

    public void Dispose()
    {
        foreach (var target in _targets)
            target.Dispose();
        _vertexShader.Dispose();
        _downsampleShader.Dispose();
        _blurShader.Dispose();
        _constants.Dispose();
        _sampler.Dispose();
        _blendState.Dispose();
        _rasterizerState.Dispose();
        _depthState.Dispose();
    }

    private readonly record struct Vector4Data(float X, float Y, float Z, float W);

    private struct Target
    {
        private ID3D11Texture2D? _texture;
        public ID3D11RenderTargetView? RenderView;
        public ID3D11ShaderResourceView? View;
        private int _width, _height;

        public void Resize(ID3D11Device device, int width, int height)
        {
            if (_texture is not null && width == _width && height == _height)
                return;
            Dispose();
            _width = width;
            _height = height;
            _texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = GraphicsDevice.ColorFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            RenderView = device.CreateRenderTargetView(_texture);
            View = device.CreateShaderResourceView(_texture);
        }

        public void Dispose()
        {
            View?.Dispose();
            RenderView?.Dispose();
            _texture?.Dispose();
            View = null;
            RenderView = null;
            _texture = null;
        }
    }
}
