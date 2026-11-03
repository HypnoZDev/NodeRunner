using NodeRunner.Data.Textures;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using TextureLayout = NodeRunner.Data.Textures.TextureLayout;

namespace NodeRunner.Render;

/// <summary>A texture stored on the GPU (see <see cref="Create"/>).</summary>
public sealed class GpuTexture : IDisposable
{
    private GpuTexture(ID3D11ShaderResourceView view, long byteSize)
    {
        View = view;
        ByteSize = byteSize;
    }

    internal ID3D11ShaderResourceView View { get; }

    /// <summary>GPU memory used, in bytes.</summary>
    public long ByteSize { get; }

    /// <summary>
    /// Copies a texture to the GPU as is (compressed formats stay compressed).
    /// Null if its format is not supported or its data is unusable.
    /// </summary>
    public static unsafe GpuTexture? Create(ID3D11Device device, TextureData texture)
    {
        var format = ToDxgi(texture.Format);
        var levels = TextureLayout.GetMipLevels(texture);
        if (format == Format.Unknown || levels.Count == 0)
            return null;
        // Compressed textures must be a whole number of 4x4 blocks.
        if (TextureLayout.IsBlockCompressed(texture.Format) && (texture.Width % 4 != 0 || texture.Height % 4 != 0))
            return null;

        var description = new Texture2DDescription
        {
            Width = (uint)texture.Width,
            Height = (uint)texture.Height,
            MipLevels = (uint)levels.Count,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };

        fixed (byte* data = texture.Data)
        {
            var initialData = new SubresourceData[levels.Count];
            for (int i = 0; i < levels.Count; i++)
                initialData[i] = new SubresourceData((IntPtr)(data + levels[i].Offset), (uint)levels[i].RowPitch, (uint)levels[i].SlicePitch);
            using var resource = device.CreateTexture2D(description, initialData);
            var view = device.CreateShaderResourceView(resource, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = (uint)levels.Count, MostDetailedMip = 0 },
            });
            return new GpuTexture(view, levels.Sum(l => (long)l.SlicePitch));
        }
    }

    /// <summary>A 1x1 texture of one colour (used for models without texture).</summary>
    public static GpuTexture CreateSolid(ID3D11Device device, byte r, byte g, byte b, byte a)
    {
        var pixel = new TextureData
        {
            NameHash = 0,
            Name = "solid",
            Width = 1,
            Height = 1,
            MipLevels = 1,
            Format = TextureFormat.R8G8B8A8,
            Data = [r, g, b, a],
        };
        return Create(device, pixel)!;
    }

    private static Format ToDxgi(TextureFormat format) => format switch
    {
        TextureFormat.Bc1 => Format.BC1_UNorm,
        TextureFormat.Bc2 => Format.BC2_UNorm,
        TextureFormat.Bc3 => Format.BC3_UNorm,
        TextureFormat.Bc4 => Format.BC4_UNorm,
        TextureFormat.Bc5 => Format.BC5_UNorm,
        TextureFormat.Bc7 => Format.BC7_UNorm,
        TextureFormat.B8G8R8A8 => Format.B8G8R8A8_UNorm,
        TextureFormat.B8G8R8X8 => Format.B8G8R8X8_UNorm,
        TextureFormat.R8G8B8A8 => Format.R8G8B8A8_UNorm,
        TextureFormat.B5G5R5A1 => Format.B5G5R5A1_UNorm,
        TextureFormat.A8 => Format.A8_UNorm,
        // L8 has no direct DXGI equivalent (R8 would show in red): left untextured for now.
        _ => Format.Unknown,
    };

    public void Dispose() => View.Dispose();
}
