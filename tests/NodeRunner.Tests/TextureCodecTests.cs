using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;

namespace NodeRunner.Tests;

public class TextureCodecTests
{
    // A smooth picture with a transparent corner.
    private static byte[] Picture(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = (byte)(x * 255 / (w - 1));
                rgba[i + 1] = (byte)(y * 255 / (h - 1));
                rgba[i + 2] = (byte)((x + y) * 255 / (w + h - 2));
                rgba[i + 3] = x < w / 4 && y < h / 4 ? (byte)0 : (byte)255;
            }
        }
        return rgba;
    }

    private static TextureData Texture(TextureFormat format, int w, int h, int levels, byte[] data) => new()
    {
        NameHash = 1, Name = "test", Width = w, Height = h, MipLevels = levels, Format = format, Data = data,
    };

    [Theory]
    [InlineData(TextureFormat.Bc1, 30)]
    [InlineData(TextureFormat.Bc2, 30)]
    [InlineData(TextureFormat.Bc3, 35)]
    [InlineData(TextureFormat.B8G8R8A8, 99)]
    [InlineData(TextureFormat.R8G8B8A8, 99)]
    public void EncodedPicturesDecodeCloseToTheOriginal(TextureFormat format, double minPsnr)
    {
        const int w = 64, h = 32, levels = 7;
        var picture = Picture(w, h);
        var encoded = TextureCodec.Encode(picture, w, h, format, levels);

        int expected = 0;
        for (int i = 0; i < levels; i++)
            expected += TextureLayout.Pitch(format, w >> i, h >> i).SlicePitch;
        Assert.Equal(expected, encoded.Length);

        var decoded = TextureCodec.Decode(Texture(format, w, h, levels, encoded));
        Assert.True(Psnr(picture, decoded) >= minPsnr, $"{format}: {Psnr(picture, decoded):F1} dB");
        // The transparent corner stays transparent.
        Assert.Equal(0, decoded[3]);
    }

    [Fact]
    public void Bc7KeepsTheBlockSizeAndAnchorBit()
    {
        var encoded = TextureCodec.Encode(Picture(16, 16), 16, 16, TextureFormat.Bc7, 1);
        Assert.Equal(16 * 16, encoded.Length);
        for (int b = 0; b < encoded.Length; b += 16)
            Assert.Equal(0x40, encoded[b] & 0x7F); // Mode 6.
    }

    [Fact]
    public void DdsWrittenIsReadBack()
    {
        var encoded = TextureCodec.Encode(Picture(32, 32), 32, 32, TextureFormat.Bc3, 6);
        var dds = DdsFile.Write(Texture(TextureFormat.Bc3, 32, 32, 6, encoded));
        var back = DdsFile.Read(dds, "test");
        Assert.Equal(TextureFormat.Bc3, back.Format);
        Assert.Equal(6, back.MipLevels);
        Assert.Equal(encoded, back.Data);
    }

    [Fact]
    public void StoredTextureWritesInPlaceAndRestores()
    {
        var content = new byte[64];
        var stored = new StoredTexture("test", 4, 4, 1, TextureFormat.Bc1, 16, 8, true);
        var before = stored.Write(content, Enumerable.Repeat((byte)7, 12).ToArray());
        Assert.All(content[16..24], b => Assert.Equal(7, b));
        Assert.Equal(0, content[24]); // Never past the texture.
        stored.Restore(content, before);
        Assert.All(content, b => Assert.Equal(0, b));
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        double mse = sum / a.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
