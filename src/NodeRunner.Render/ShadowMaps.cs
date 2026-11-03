using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Sun shadows ("cascaded shadow maps"): the scene is drawn seen from the sun into CascadeCount depth images, each
// covering a slice of the camera's view (near: sharp, far: coarse).
public sealed class ShadowMaps : IDisposable
{
    public const int CascadeCount = 3;

    public const int Size = 2048;

    private static readonly float[] SplitShares = [0.07f, 0.27f, 1f];

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11DepthStencilView[] _depthViews = new ID3D11DepthStencilView[CascadeCount];

    public Matrix4x4[] Cascades { get; } = new Matrix4x4[CascadeCount];

    public float[] Splits { get; } = new float[CascadeCount];

    public float[] TexelSizes { get; } = new float[CascadeCount];

    public Vector3 CameraForward { get; private set; }

    internal ID3D11ShaderResourceView View { get; }
    internal ID3D11SamplerState Sampler { get; }

    public ShadowMaps(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        _texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Size,
            Height = Size,
            MipLevels = 1,
            ArraySize = CascadeCount,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        for (int i = 0; i < CascadeCount; i++)
        {
            _depthViews[i] = device.CreateDepthStencilView(_texture, new DepthStencilViewDescription(
                _texture, DepthStencilViewDimension.Texture2DArray, Format.D32_Float, 0, (uint)i, 1));
        }
        View = device.CreateShaderResourceView(_texture, new ShaderResourceViewDescription(
            _texture, ShaderResourceViewDimension.Texture2DArray, Format.R32_Float, 0, 1, 0, CascadeCount));

        // Compares 4 texels and blends the results: soft edges for the cost of one sample.
        Sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.ComparisonMinMagLinearMipPoint,
            AddressU = TextureAddressMode.Border,
            AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Clamp,
            BorderColor = new Color4(1f, 1f, 1f, 1f),
            ComparisonFunc = ComparisonFunction.LessEqual,
            MaxLOD = float.MaxValue,
        });
    }

    // Places the slices for this camera (aspect ratio of the 3D view), the sun in sunDirection (towards the sun) and
    // shadows up to distance metres.
    public void Update(Camera camera, float aspectRatio, Vector3 sunDirection, float distance)
    {
        var forward = Vector3.Normalize(camera.Forward);
        var right = camera.Right;
        var up = Vector3.Cross(right, forward);
        CameraForward = forward;

        float tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        float tanX = tanY * aspectRatio;
        var sun = Vector3.Normalize(sunDirection);
        var lightUp = MathF.Abs(sun.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        var lightRotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sun, lightUp);
        Matrix4x4.Invert(lightRotation, out var inverseRotation);

        float near = camera.NearPlane;
        for (int i = 0; i < CascadeCount; i++)
        {
            float far = MathF.Max(distance * SplitShares[i], near + 1f);
            Splits[i] = far;

            // Smallest sphere around the slice: centred on the view axis, at the same distance from its near and far
            // corners (or at its far end when the slice is wide and short).
            float k = 1f + tanX * tanX + tanY * tanY;
            var centreOffset = forward * MathF.Min(far, k * (far + near) * 0.5f);
            float radius = MathF.Max(
                Vector3.Distance(centreOffset, forward * far + right * (far * tanX) + up * (far * tanY)),
                Vector3.Distance(centreOffset, forward * near + right * (near * tanX) + up * (near * tanY)));
            radius = MathF.Ceiling(radius + 1f); // Whole metres: the same size from frame to frame.

            float texel = 2f * radius / Size;
            TexelSizes[i] = texel;

            // Moved by whole texels as seen from the sun.
            var centre = Vector3.Transform(camera.Position + centreOffset, lightRotation);
            centre.X = MathF.Round(centre.X / texel) * texel;
            centre.Y = MathF.Round(centre.Y / texel) * texel;
            centre = Vector3.Transform(centre, inverseRotation);

            // Things between the sun and the sphere still cast their shadow: they are flattened onto depth 0
            // (no depth clipping in the shadow pass).
            var view = Matrix4x4.CreateLookAt(centre + sun * radius, centre, lightUp);
            var projection = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, 0f, 2f * radius);
            Cascades[i] = view * projection;
        }
    }

    public void BeginCascade(int cascade)
    {
        _context.OMSetRenderTargets([], _depthViews[cascade]);
        _context.RSSetViewport(new Viewport(0, 0, Size, Size));
        _context.ClearDepthStencilView(_depthViews[cascade], DepthStencilClearFlags.Depth, 1f, 0);
    }

    public void Dispose()
    {
        Sampler.Dispose();
        View.Dispose();
        foreach (var view in _depthViews)
            view.Dispose();
        _texture.Dispose();
    }
}
