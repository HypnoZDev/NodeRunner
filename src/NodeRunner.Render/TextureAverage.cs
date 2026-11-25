using System.Numerics;
using NodeRunner.Data.Textures;

namespace NodeRunner.Render;

// Average colour of a texture (sRGB, weighted by alpha), read from its smallest mip level of at least 4x4 pixels.
// Null for the formats not decoded here (BC4, BC5, BC7, L8).
internal static class TextureAverage
{
    public static Vector4? Of(TextureData texture, IReadOnlyList<MipLevel> levels)
    {
        if (levels.Count == 0)
            return null;
        var level = levels[0];
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            if (levels[i].Width >= 4 && levels[i].Height >= 4)
            {
                level = levels[i];
                break;
            }
        }
        var data = texture.Data.AsSpan(level.Offset, level.SlicePitch);
        var sum = Vector4.Zero;
        switch (texture.Format)
        {
            case TextureFormat.Bc1:
                for (int b = 0; b + 8 <= data.Length; b += 8)
                    AddColorBlock(data.Slice(b, 8), alphas: null, allowTransparent: true, ref sum);
                break;
            case TextureFormat.Bc2:
            case TextureFormat.Bc3:
                Span<float> alphas = stackalloc float[16];
                for (int b = 0; b + 16 <= data.Length; b += 16)
                {
                    if (texture.Format == TextureFormat.Bc2)
                        ExplicitAlpha(data.Slice(b, 8), alphas);
                    else
                        InterpolatedAlpha(data.Slice(b, 8), alphas);
                    AddColorBlock(data.Slice(b + 8, 8), alphas, allowTransparent: false, ref sum);
                }
                break;
            case TextureFormat.B8G8R8A8:
            case TextureFormat.B8G8R8X8:
            case TextureFormat.R8G8B8A8:
                for (int p = 0; p + 4 <= data.Length; p += 4)
                {
                    float a = texture.Format == TextureFormat.B8G8R8X8 ? 1f : data[p + 3] / 255f;
                    var rgb = texture.Format == TextureFormat.R8G8B8A8
                        ? new Vector3(data[p], data[p + 1], data[p + 2]) : new Vector3(data[p + 2], data[p + 1], data[p]);
                    sum += new Vector4(rgb / 255f * a, a);
                }
                break;
            case TextureFormat.B5G5R5A1:
                for (int p = 0; p + 2 <= data.Length; p += 2)
                {
                    int v = data[p] | data[p + 1] << 8;
                    float a = (v >> 15) & 1;
                    sum += new Vector4(new Vector3((v >> 10) & 31, (v >> 5) & 31, v & 31) / 31f * a, a);
                }
                break;
            case TextureFormat.A8:
                for (int p = 0; p < data.Length; p++)
                    sum += new Vector4(data[p] / 255f, data[p] / 255f, data[p] / 255f, data[p] / 255f);
                break;
            default:
                return null;
        }
        if (sum.W <= 0f)
            return null;
        return new Vector4(new Vector3(sum.X, sum.Y, sum.Z) / sum.W, 1f);
    }

    private static Vector3 Rgb565(int v) => new(((v >> 11) & 31) / 31f, ((v >> 5) & 63) / 63f, (v & 31) / 31f);

    // The 16 texels of a BC1 colour block (rgb times alpha, alpha), added to sum.
    private static void AddColorBlock(ReadOnlySpan<byte> block, ReadOnlySpan<float> alphas, bool allowTransparent, ref Vector4 sum)
    {
        int c0 = block[0] | block[1] << 8, c1 = block[2] | block[3] << 8;
        var a = Rgb565(c0);
        var b = Rgb565(c1);
        bool fourColors = c0 > c1 || !allowTransparent;
        Span<Vector4> palette =
        [
            new(a, 1f), new(b, 1f),
            fourColors ? new((2f * a + b) / 3f, 1f) : new((a + b) * 0.5f, 1f),
            fourColors ? new((a + 2f * b) / 3f, 1f) : Vector4.Zero,
        ];
        uint indices = (uint)(block[4] | block[5] << 8 | block[6] << 16 | block[7] << 24);
        for (int t = 0; t < 16; t++)
        {
            var c = palette[(int)(indices >> (2 * t)) & 3];
            float alpha = alphas.IsEmpty ? c.W : alphas[t];
            sum += new Vector4(new Vector3(c.X, c.Y, c.Z) * alpha, alpha);
        }
    }

    private static void ExplicitAlpha(ReadOnlySpan<byte> block, Span<float> alphas)
    {
        for (int t = 0; t < 16; t++)
            alphas[t] = ((block[t / 2] >> (4 * (t & 1))) & 15) / 15f;
    }

    private static void InterpolatedAlpha(ReadOnlySpan<byte> block, Span<float> alphas)
    {
        float a0 = block[0] / 255f, a1 = block[1] / 255f;
        Span<float> palette = stackalloc float[8];
        palette[0] = a0;
        palette[1] = a1;
        if (block[0] > block[1])
        {
            for (int i = 1; i < 7; i++)
                palette[i + 1] = ((7 - i) * a0 + i * a1) / 7f;
        }
        else
        {
            for (int i = 1; i < 5; i++)
                palette[i + 1] = ((5 - i) * a0 + i * a1) / 5f;
            palette[6] = 0f;
            palette[7] = 1f;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)block[2 + i] << (8 * i);
        for (int t = 0; t < 16; t++)
            alphas[t] = palette[(int)(bits >> (3 * t)) & 7];
    }
}
