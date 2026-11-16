using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// Converts images (4 bytes per pixel, red first) to the formats of the game's textures and back: mip levels, and the
// 4 x 4 block compression of DXT1 / DXT3 / DXT5 (BC1-3), ATI1 / ATI2 (BC4-5) and BC7.
public static class TextureCodec
{
    // Every mip level of the image in the format, largest first, each at its exact size.
    public static byte[] Encode(byte[] rgba, int width, int height, TextureFormat format, int levels)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("Image size does not match.", nameof(rgba));
        int total = 0;
        for (int i = 0; i < levels; i++)
            total += TextureLayout.Pitch(format, width >> i, height >> i).SlicePitch;
        var result = new byte[total];
        int at = 0;
        var level = rgba;
        int w = width, h = height;
        for (int i = 0; i < levels; i++)
        {
            int size = TextureLayout.Pitch(format, w, h).SlicePitch;
            EncodeLevel(level, w, h, format, result, at);
            at += size;
            if (i + 1 < levels)
                (level, w, h) = Half(level, w, h);
        }
        return result;
    }

    public static bool CanDecode(TextureFormat format) => format is not (TextureFormat.Bc7 or TextureFormat.Unknown);

    // The largest mip level as an image (4 bytes per pixel, red first).
    public static byte[] Decode(TextureData texture)
    {
        int w = texture.Width, h = texture.Height;
        var format = texture.Format;
        var src = texture.Data;
        var rgba = new byte[w * h * 4];
        if (!TextureLayout.IsBlockCompressed(format))
        {
            for (int i = 0; i < w * h; i++)
                DecodePixel(format, src, i, rgba.AsSpan(i * 4, 4));
            return rgba;
        }
        if (!CanDecode(format))
            throw new InvalidDataException("Les textures BC7 ne peuvent pas encore être converties en image : exporte-la en .dds.");
        int blockBytes = format is TextureFormat.Bc1 or TextureFormat.Bc4 ? 8 : 16;
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> block = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * blockBytes;
                // Smallest levels can be a few bytes short in game files: missing blocks stay black.
                if (at + blockBytes > src.Length)
                    return rgba;
                var b = src.AsSpan(at, blockBytes);
                block.Fill(255);
                switch (format)
                {
                    case TextureFormat.Bc1:
                        DecodeColor(b, block, threeColor: true);
                        break;
                    case TextureFormat.Bc2:
                        DecodeColor(b[8..], block, threeColor: false);
                        ulong alpha = BinaryPrimitives.ReadUInt64LittleEndian(b);
                        for (int i = 0; i < 16; i++)
                            block[i * 4 + 3] = (byte)(((alpha >> (4 * i)) & 15) * 17);
                        break;
                    case TextureFormat.Bc3:
                        DecodeColor(b[8..], block, threeColor: false);
                        DecodeChannel(b, block, 3);
                        break;
                    case TextureFormat.Bc4:
                        DecodeChannel(b, block, 0);
                        for (int i = 0; i < 16; i++)
                            block[i * 4 + 1] = block[i * 4 + 2] = block[i * 4];
                        break;
                    case TextureFormat.Bc5:
                        DecodeChannel(b, block, 0);
                        DecodeChannel(b[8..], block, 1);
                        for (int i = 0; i < 16; i++)
                            block[i * 4 + 2] = 0;
                        break;
                }
                for (int y = 0; y < 4 && by * 4 + y < h; y++)
                {
                    for (int x = 0; x < 4 && bx * 4 + x < w; x++)
                        block.Slice((y * 4 + x) * 4, 4).CopyTo(rgba.AsSpan(((by * 4 + y) * w + bx * 4 + x) * 4));
                }
            }
        }
        return rgba;
    }

    // ---- Mip levels ----

    // Half the size (2 x 2 pixels averaged; colours weighted by their alpha, so cut-out edges don't darken).
    private static (byte[], int, int) Half(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        Parallel.For(0, nh, y =>
        {
            int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4;
                int alpha = src[a + 3] + src[b + 3] + src[c + 3] + src[d + 3];
                int o = (y * nw + x) * 4;
                for (int k = 0; k < 3; k++)
                {
                    dst[o + k] = alpha == 0
                        ? (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) / 4)
                        : (byte)((src[a + k] * src[a + 3] + src[b + k] * src[b + 3] + src[c + k] * src[c + 3] + src[d + k] * src[d + 3]
                            + alpha / 2) / alpha);
                }
                dst[o + 3] = (byte)((alpha + 2) / 4);
            }
        });
        return (dst, nw, nh);
    }

    // ---- Levels ----

    private static void EncodeLevel(byte[] src, int w, int h, TextureFormat format, byte[] dst, int at)
    {
        if (!TextureLayout.IsBlockCompressed(format))
        {
            int bytes = TextureLayout.Pitch(format, 1, 1).SlicePitch;
            for (int i = 0; i < w * h; i++)
                EncodePixel(format, src.AsSpan(i * 4, 4), dst.AsSpan(at + i * bytes, bytes));
            return;
        }
        int blockBytes = format is TextureFormat.Bc1 or TextureFormat.Bc4 ? 8 : 16;
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Parallel.For(0, bh, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                // Pixels past the edge repeat the last row / column.
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, h - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, w - 1);
                        src.AsSpan((sy * w + sx) * 4, 4).CopyTo(block[((y * 4 + x) * 4)..]);
                    }
                }
                var o = dst.AsSpan(at + (by * bw + bx) * blockBytes, blockBytes);
                switch (format)
                {
                    case TextureFormat.Bc1:
                        EncodeColor(block, o, punchThrough: true);
                        break;
                    case TextureFormat.Bc2:
                        ulong alpha = 0;
                        for (int i = 0; i < 16; i++)
                            alpha |= (ulong)((block[i * 4 + 3] * 15 + 127) / 255) << (4 * i);
                        BinaryPrimitives.WriteUInt64LittleEndian(o, alpha);
                        EncodeColor(block, o[8..], punchThrough: false);
                        break;
                    case TextureFormat.Bc3:
                        EncodeChannel(block, 3, o);
                        EncodeColor(block, o[8..], punchThrough: false);
                        break;
                    case TextureFormat.Bc4:
                        EncodeChannel(block, 0, o);
                        break;
                    case TextureFormat.Bc5:
                        EncodeChannel(block, 0, o);
                        EncodeChannel(block, 1, o[8..]);
                        break;
                    case TextureFormat.Bc7:
                        EncodeBc7(block, o);
                        break;
                }
            }
        });
    }

    // ---- Uncompressed pixels ----

    private static void EncodePixel(TextureFormat format, ReadOnlySpan<byte> p, Span<byte> o)
    {
        switch (format)
        {
            case TextureFormat.B8G8R8A8:
            case TextureFormat.B8G8R8X8:
                o[0] = p[2];
                o[1] = p[1];
                o[2] = p[0];
                o[3] = format == TextureFormat.B8G8R8X8 ? (byte)255 : p[3];
                break;
            case TextureFormat.R8G8B8A8:
                p.CopyTo(o);
                break;
            case TextureFormat.B5G5R5A1:
                int v = (p[3] >= 128 ? 0x8000 : 0) | (p[0] * 31 + 127) / 255 << 10 | (p[1] * 31 + 127) / 255 << 5 | (p[2] * 31 + 127) / 255;
                BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)v);
                break;
            case TextureFormat.A8:
                o[0] = p[3];
                break;
            case TextureFormat.L8:
                o[0] = (byte)((p[0] * 77 + p[1] * 150 + p[2] * 29 + 128) >> 8);
                break;
        }
    }

    private static void DecodePixel(TextureFormat format, byte[] src, int i, Span<byte> o)
    {
        int bytes = TextureLayout.Pitch(format, 1, 1).SlicePitch;
        if ((i + 1) * bytes > src.Length)
            return;
        var p = src.AsSpan(i * bytes, bytes);
        switch (format)
        {
            case TextureFormat.B8G8R8A8:
            case TextureFormat.B8G8R8X8:
                o[0] = p[2];
                o[1] = p[1];
                o[2] = p[0];
                o[3] = format == TextureFormat.B8G8R8X8 ? (byte)255 : p[3];
                break;
            case TextureFormat.R8G8B8A8:
                p.CopyTo(o);
                break;
            case TextureFormat.B5G5R5A1:
                int v = BinaryPrimitives.ReadUInt16LittleEndian(p);
                o[0] = (byte)(((v >> 10) & 31) * 255 / 31);
                o[1] = (byte)(((v >> 5) & 31) * 255 / 31);
                o[2] = (byte)((v & 31) * 255 / 31);
                o[3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                break;
            case TextureFormat.A8:
                o[0] = o[1] = o[2] = 255;
                o[3] = p[0];
                break;
            case TextureFormat.L8:
                o[0] = o[1] = o[2] = p[0];
                o[3] = 255;
                break;
        }
    }

    // ---- Colour blocks (BC1, and the colour half of BC2 / BC3) ----

    // punchThrough (BC1): pixels with alpha under 128 become transparent (the block's three-colour mode).
    private static void EncodeColor(ReadOnlySpan<byte> block, Span<byte> o, bool punchThrough)
    {
        Span<Vector3> colors = stackalloc Vector3[16];
        Span<bool> transparent = stackalloc bool[16];
        int count = 0;
        bool anyTransparent = false;
        for (int i = 0; i < 16; i++)
        {
            transparent[i] = punchThrough && block[i * 4 + 3] < 128;
            anyTransparent |= transparent[i];
            colors[i] = new Vector3(block[i * 4], block[i * 4 + 1], block[i * 4 + 2]);
            if (!transparent[i])
                count++;
        }
        if (count == 0)
        {
            // Three-colour mode (first colour not above the second), every pixel transparent.
            BinaryPrimitives.WriteUInt32LittleEndian(o, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(o[4..], 0xFFFFFFFF);
            return;
        }

        var (from, to) = PrincipalEnds(colors, transparent);
        var best = EvaluateColor(colors, transparent, Pack565(from), Pack565(to), anyTransparent);
        // Least squares: the two colours that best fit the indices chosen, twice.
        for (int pass = 0; pass < 2 && !anyTransparent; pass++)
        {
            if (FitColors(colors, best.Indices) is not var (a, b))
                break;
            var next = EvaluateColor(colors, transparent, Pack565(a), Pack565(b), false);
            if (next.Error >= best.Error)
                break;
            best = next;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(o, best.C0);
        BinaryPrimitives.WriteUInt16LittleEndian(o[2..], best.C1);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], best.Indices);
    }

    // The ends of the colours along the direction they spread the most, brought in by 1/16 of their spread.
    private static (Vector3, Vector3) PrincipalEnds(ReadOnlySpan<Vector3> colors, ReadOnlySpan<bool> skip)
    {
        var mean = Vector3.Zero;
        int n = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            if (!skip[i])
            {
                mean += colors[i];
                n++;
            }
        }
        mean /= n;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            if (skip[i])
                continue;
            var d = colors[i] - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        var axis = new Vector3(1f, 1f, 1f);
        for (int k = 0; k < 8; k++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z,
                xz * axis.X + yz * axis.Y + zz * axis.Z);
            float length = axis.Length();
            if (length < 1e-6f)
                return (mean, mean);
            axis /= length;
        }
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < colors.Length; i++)
        {
            if (skip[i])
                continue;
            float t = Vector3.Dot(colors[i] - mean, axis);
            min = Math.Min(min, t);
            max = Math.Max(max, t);
        }
        float inset = (max - min) / 16f;
        return (Clamp255(mean + axis * (max - inset)), Clamp255(mean + axis * (min + inset)));
    }

    private static Vector3 Clamp255(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, new Vector3(255f));

    private readonly record struct ColorBlock(ushort C0, ushort C1, uint Indices, float Error);

    private static ColorBlock EvaluateColor(ReadOnlySpan<Vector3> colors, ReadOnlySpan<bool> transparent, ushort c0, ushort c1, bool threeColor)
    {
        // Four colours need the first above the second, three colours (with transparency) the other way round.
        if (threeColor ? c0 > c1 : c0 < c1)
            (c0, c1) = (c1, c0);
        Span<Vector3> palette = stackalloc Vector3[4];
        palette[0] = Unpack565(c0);
        palette[1] = Unpack565(c1);
        int colorsUsed;
        if (threeColor || c0 == c1)
        {
            palette[2] = Floor((palette[0] + palette[1]) / 2f);
            colorsUsed = c0 == c1 && !threeColor ? 1 : 3;
        }
        else
        {
            palette[2] = Floor((2f * palette[0] + palette[1]) / 3f);
            palette[3] = Floor((palette[0] + 2f * palette[1]) / 3f);
            colorsUsed = 4;
        }
        uint indices = 0;
        float error = 0f;
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            if (transparent[i])
                best = 3;
            else
            {
                float bestDistance = float.MaxValue;
                for (int k = 0; k < colorsUsed; k++)
                {
                    float d = Vector3.DistanceSquared(colors[i], palette[k]);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = k;
                    }
                }
                error += bestDistance;
            }
            indices |= (uint)best << (2 * i);
        }
        return new ColorBlock(c0, c1, indices, error);
    }

    private static (Vector3, Vector3)? FitColors(ReadOnlySpan<Vector3> colors, uint indices)
    {
        ReadOnlySpan<float> weights = [1f, 0f, 2f / 3f, 1f / 3f];
        float aa = 0, ab = 0, bb = 0;
        Vector3 ax = Vector3.Zero, bx = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            float w = weights[(int)(indices >> (2 * i)) & 3];
            float v = 1f - w;
            aa += w * w;
            ab += w * v;
            bb += v * v;
            ax += w * colors[i];
            bx += v * colors[i];
        }
        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) < 1e-6f)
            return null;
        return (Clamp255((ax * bb - bx * ab) / det), Clamp255((bx * aa - ax * ab) / det));
    }

    private static ushort Pack565(Vector3 c) =>
        (ushort)((int)MathF.Round(c.X * 31f / 255f) << 11 | (int)MathF.Round(c.Y * 63f / 255f) << 5 | (int)MathF.Round(c.Z * 31f / 255f));

    private static Vector3 Unpack565(ushort c)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        return new Vector3(r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2);
    }

    private static Vector3 Floor(Vector3 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z));

    private static void DecodeColor(ReadOnlySpan<byte> b, Span<byte> block, bool threeColor)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        Span<Vector3> palette = stackalloc Vector3[4];
        palette[0] = Unpack565(c0);
        palette[1] = Unpack565(c1);
        bool three = threeColor && c0 <= c1;
        if (three)
            palette[2] = Floor((palette[0] + palette[1]) / 2f);
        else
        {
            palette[2] = Floor((2f * palette[0] + palette[1]) / 3f);
            palette[3] = Floor((palette[0] + 2f * palette[1]) / 3f);
        }
        for (int i = 0; i < 16; i++)
        {
            int k = (int)(indices >> (2 * i)) & 3;
            var c = palette[k];
            block[i * 4] = (byte)c.X;
            block[i * 4 + 1] = (byte)c.Y;
            block[i * 4 + 2] = (byte)c.Z;
            if (three && k == 3)
                block[i * 4] = block[i * 4 + 1] = block[i * 4 + 2] = block[i * 4 + 3] = 0;
        }
    }

    // ---- Single channel blocks (alpha of BC3, BC4, each half of BC5) ----

    private static void EncodeChannel(ReadOnlySpan<byte> block, int channel, Span<byte> o)
    {
        Span<byte> values = stackalloc byte[16];
        int min = 255, max = 0, innerMin = 255, innerMax = 0;
        for (int i = 0; i < 16; i++)
        {
            byte v = values[i] = block[i * 4 + channel];
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            if (v is > 0 and < 255)
            {
                innerMin = Math.Min(innerMin, v);
                innerMax = Math.Max(innerMax, v);
            }
        }
        // Eight steps between the ends, or six plus exact 0 and 255 (better when the block holds both).
        var eight = EvaluateChannel(values, (byte)max, (byte)min);
        var six = innerMin > innerMax ? EvaluateChannel(values, 0, 0) : EvaluateChannel(values, (byte)innerMin, (byte)innerMax);
        var best = six.Error < eight.Error ? six : eight;
        o[0] = best.A0;
        o[1] = best.A1;
        for (int k = 0; k < 6; k++)
            o[2 + k] = (byte)(best.Indices >> (8 * k));
    }

    private readonly record struct ChannelBlock(byte A0, byte A1, ulong Indices, int Error);

    private static ChannelBlock EvaluateChannel(ReadOnlySpan<byte> values, byte a0, byte a1)
    {
        Span<int> palette = stackalloc int[8];
        ChannelPalette(a0, a1, palette);
        ulong indices = 0;
        int error = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int d = Math.Abs(values[i] - palette[k]);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = k;
                }
            }
            error += bestDistance * bestDistance;
            indices |= (ulong)best << (3 * i);
        }
        return new ChannelBlock(a0, a1, indices, error);
    }

    private static void ChannelPalette(int a0, int a1, Span<int> palette)
    {
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
        {
            for (int k = 1; k <= 6; k++)
                palette[k + 1] = ((7 - k) * a0 + k * a1 + 3) / 7;
        }
        else
        {
            for (int k = 1; k <= 4; k++)
                palette[k + 1] = ((5 - k) * a0 + k * a1 + 2) / 5;
            palette[6] = 0;
            palette[7] = 255;
        }
    }

    private static void DecodeChannel(ReadOnlySpan<byte> b, Span<byte> block, int channel)
    {
        Span<int> palette = stackalloc int[8];
        ChannelPalette(b[0], b[1], palette);
        ulong indices = 0;
        for (int k = 0; k < 6; k++)
            indices |= (ulong)b[2 + k] << (8 * k);
        for (int i = 0; i < 16; i++)
            block[i * 4 + channel] = (byte)palette[(int)(indices >> (3 * i)) & 7];
    }

    // ---- BC7 (mode 6: one pair of RGBA colours, 16 steps between them) ----

    private static readonly int[] Bc7Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private static void EncodeBc7(ReadOnlySpan<byte> block, Span<byte> o)
    {
        Span<Vector4> px = stackalloc Vector4[16];
        var mean = Vector4.Zero;
        for (int i = 0; i < 16; i++)
        {
            px[i] = new Vector4(block[i * 4], block[i * 4 + 1], block[i * 4 + 2], block[i * 4 + 3]);
            mean += px[i];
        }
        mean /= 16f;
        // Direction the pixels spread the most (power iteration on their covariance).
        Span<float> cov = stackalloc float[16];
        for (int i = 0; i < 16; i++)
        {
            var d = px[i] - mean;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                    cov[r * 4 + c] += d[r] * d[c];
            }
        }
        var axis = new Vector4(1f, 1f, 1f, 1f);
        bool flat = false;
        for (int k = 0; k < 8 && !flat; k++)
        {
            var next = Vector4.Zero;
            for (int r = 0; r < 4; r++)
                next[r] = cov[r * 4] * axis.X + cov[r * 4 + 1] * axis.Y + cov[r * 4 + 2] * axis.Z + cov[r * 4 + 3] * axis.W;
            float length = next.Length();
            flat = length < 1e-6f;
            if (!flat)
                axis = next / length;
        }
        Vector4 e0 = mean, e1 = mean;
        if (!flat)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                float t = Vector4.Dot(px[i] - mean, axis);
                min = Math.Min(min, t);
                max = Math.Max(max, t);
            }
            e0 = Vector4.Clamp(mean + axis * min, Vector4.Zero, new Vector4(255f));
            e1 = Vector4.Clamp(mean + axis * max, Vector4.Zero, new Vector4(255f));
        }

        var (q0, p0) = QuantizeBc7(e0);
        var (q1, p1) = QuantizeBc7(e1);
        Span<int> indices = stackalloc int[16];
        float error = Bc7Indices(px, q0, p0, q1, p1, indices);
        // Least squares: the two colours that best fit the indices chosen, twice.
        Span<int> tried = stackalloc int[16];
        for (int pass = 0; pass < 2 && error > 0f; pass++)
        {
            float aa = 0, ab = 0, bb = 0;
            Vector4 ax = Vector4.Zero, bx = Vector4.Zero;
            for (int i = 0; i < 16; i++)
            {
                float w = Bc7Weights[indices[i]] / 64f, v = 1f - w;
                aa += v * v;
                ab += v * w;
                bb += w * w;
                ax += v * px[i];
                bx += w * px[i];
            }
            float det = aa * bb - ab * ab;
            if (MathF.Abs(det) < 1e-6f)
                break;
            var (n0, np0) = QuantizeBc7(Vector4.Clamp((ax * bb - bx * ab) / det, Vector4.Zero, new Vector4(255f)));
            var (n1, np1) = QuantizeBc7(Vector4.Clamp((bx * aa - ax * ab) / det, Vector4.Zero, new Vector4(255f)));
            float next = Bc7Indices(px, n0, np0, n1, np1, tried);
            if (next >= error)
                break;
            (q0, p0, q1, p1, error) = (n0, np0, n1, np1, next);
            tried.CopyTo(indices);
        }
        // The first pixel's index is stored on 3 bits: it must be under 8.
        if (indices[0] >= 8)
        {
            (q0, q1) = (q1, q0);
            (p0, p1) = (p1, p0);
            for (int i = 0; i < 16; i++)
                indices[i] = 15 - indices[i];
        }

        var bits = new BitWriter(o);
        bits.Write(1 << 6, 7);
        for (int c = 0; c < 4; c++)
        {
            bits.Write(q0[c], 7);
            bits.Write(q1[c], 7);
        }
        bits.Write(p0, 1);
        bits.Write(p1, 1);
        bits.Write(indices[0], 3);
        for (int i = 1; i < 16; i++)
            bits.Write(indices[i], 4);
    }

    // 7 bits per channel and one low bit shared by the four, chosen for the smallest error.
    private static (int[] Q, int P) QuantizeBc7(Vector4 e)
    {
        int[] best = new int[4];
        int bestP = 0;
        float bestError = float.MaxValue;
        for (int p = 0; p < 2; p++)
        {
            var q = new int[4];
            float error = 0f;
            for (int c = 0; c < 4; c++)
            {
                q[c] = Math.Clamp((int)MathF.Round((e[c] - p) / 2f), 0, 127);
                float d = (q[c] << 1 | p) - e[c];
                error += d * d;
            }
            if (error < bestError)
            {
                bestError = error;
                best = q;
                bestP = p;
            }
        }
        return (best, bestP);
    }

    // Nearest step of each pixel; returns the error.
    private static float Bc7Indices(ReadOnlySpan<Vector4> px, int[] q0, int p0, int[] q1, int p1, Span<int> indices)
    {
        float error = 0f;
        Span<Vector4> palette = stackalloc Vector4[16];
        for (int k = 0; k < 16; k++)
        {
            int w = Bc7Weights[k];
            for (int c = 0; c < 4; c++)
                palette[k][c] = ((64 - w) * (q0[c] << 1 | p0) + w * (q1[c] << 1 | p1) + 32) >> 6;
        }
        for (int i = 0; i < 16; i++)
        {
            float bestDistance = float.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                float d = Vector4.DistanceSquared(px[i], palette[k]);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    indices[i] = k;
                }
            }
            error += bestDistance;
        }
        return error;
    }

    // Writes bits from the lowest of the first byte up.
    private ref struct BitWriter(Span<byte> output)
    {
        private readonly Span<byte> _output = output;
        private int _at;

        public void Write(int value, int count)
        {
            for (int i = 0; i < count; i++, _at++)
            {
                if (((value >> i) & 1) != 0)
                    _output[_at >> 3] |= (byte)(1 << (_at & 7));
                else
                    _output[_at >> 3] &= (byte)~(1 << (_at & 7));
            }
        }
    }
}
