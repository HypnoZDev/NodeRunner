using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NodeRunner.Render;

// Sun shadows ("cascaded shadow maps"): the scene is drawn seen from the sun into CascadeCount depth images, each
// covering a slice of the camera's view (near: sharp, far: coarse).
// An image is kept from frame to frame while it still covers its slice and nothing that casts a shadow changed: each
// is drawn a little larger than needed, so the view can move a bit before it has to be drawn again.
public sealed class ShadowMaps : IDisposable
{
    public const int CascadeCount = 3;

    public const int DefaultSize = 2048;

    private static readonly float[] SplitShares = [0.07f, 0.27f, 1f];

    // How much larger than its slice an image is drawn.
    private const float Margin = 1.15f;

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11DepthStencilView[] _depthViews = new ID3D11DepthStencilView[CascadeCount];

    // Matrices, texel sizes and spheres (world centre, radius) the images were drawn with.
    public Matrix4x4[] Cascades { get; } = new Matrix4x4[CascadeCount];

    public float[] Splits { get; } = new float[CascadeCount];

    public float[] TexelSizes { get; } = new float[CascadeCount];

    private readonly Vector3[] _centres = new Vector3[CascadeCount];
    private readonly float[] _radii = new float[CascadeCount];

    // Drawn before something casting a shadow changed.
    private readonly bool[] _stale = [true, true, true];
    private readonly bool[] _draw = new bool[CascadeCount];
    private Vector3 _sun;
    private int _frame;

    public Vector3 CameraForward { get; private set; }

    // Width and height of each image, in texels.
    public int Size { get; }

    internal ID3D11ShaderResourceView View { get; }
    internal ID3D11SamplerState Sampler { get; }

    public ShadowMaps(ID3D11Device device, ID3D11DeviceContext context, int size = DefaultSize)
    {
        _context = context;
        Size = Math.Clamp(size, 512, 8192);
        _texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Size,
            Height = (uint)Size,
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

    // True when the image of this cascade must be drawn again this frame (see Update).
    public bool MustDraw(int cascade) => _draw[cascade];

    // Every image is drawn again at the next Update.
    public void Invalidate() => Array.Fill(_stale, true);

    // Places the slices for this camera (aspect ratio of the 3D view), the sun in sunDirection (towards the sun) and
    // shadows up to distance metres, and chooses the images to draw again. castersChanged: something casting a shadow
    // was moved, added, removed or loaded since the last frame. nearMoving: something moves near the camera (the
    // character of the play mode), the two nearest images are drawn every frame.
    public void Update(Camera camera, float aspectRatio, Vector3 sunDirection, float distance, bool castersChanged = true, bool nearMoving = false)
    {
        var forward = Vector3.Normalize(camera.Forward);
        var right = camera.Right;
        var up = Vector3.Cross(right, forward);
        CameraForward = forward;

        float tanY = MathF.Tan(camera.FieldOfView * 0.5f);
        float tanX = tanY * aspectRatio;
        var sun = Vector3.Normalize(sunDirection);
        if (sun != _sun || castersChanged)
            Invalidate();
        if (nearMoving)
            _stale[0] = _stale[1] = true;
        _sun = sun;
        _frame++;
        var lightUp = MathF.Abs(sun.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        var lightRotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sun, lightUp);
        Matrix4x4.Invert(lightRotation, out var inverseRotation);

        // The far images only for a change of the scene: one of them per frame, each in turn.
        int farBudget = 1;
        float near = camera.NearPlane;
        for (int n = 0; n < CascadeCount; n++)
        {
            int i = n == 0 ? 0 : 1 + (n - 1 + _frame) % (CascadeCount - 1);
            float far = MathF.Max(distance * SplitShares[i], near + 1f);
            Splits[i] = far;

            // Smallest sphere around the slice: centred on the view axis, at the same distance from its near and far
            // corners (or at its far end when the slice is wide and short).
            float k = 1f + tanX * tanX + tanY * tanY;
            var centreOffset = forward * MathF.Min(far, k * (far + near) * 0.5f);
            float needed = MathF.Max(
                Vector3.Distance(centreOffset, forward * far + right * (far * tanX) + up * (far * tanY)),
                Vector3.Distance(centreOffset, forward * near + right * (near * tanX) + up * (near * tanY))) + 1f;
            var neededCentre = camera.Position + centreOffset;

            // Still covering the slice, and not much larger than needed (the shadow distance may have been lowered).
            bool covers = _radii[i] > 0f && Vector3.Distance(neededCentre, _centres[i]) + needed <= _radii[i]
                && _radii[i] <= needed * Margin * 1.3f;
            bool draw = !covers || _stale[i] && (i == 0 || farBudget-- > 0);
            _draw[i] = draw;
            if (!draw)
                continue;
            _stale[i] = false;

            float radius = MathF.Ceiling(needed * Margin); // Whole metres: the same size from frame to frame.
            float texel = 2f * radius / Size;
            TexelSizes[i] = texel;

            // Moved by whole texels as seen from the sun.
            var centre = Vector3.Transform(neededCentre, lightRotation);
            centre.X = MathF.Round(centre.X / texel) * texel;
            centre.Y = MathF.Round(centre.Y / texel) * texel;
            centre = Vector3.Transform(centre, inverseRotation);
            _centres[i] = centre;
            _radii[i] = radius;

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
