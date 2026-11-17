using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Writes a plaque made from an image (ImageProp): its model with the image as embedded texture and its collision
// (<name>.ydr), and its object type (<name>.ytyp), ready to be added to the prop library as a pack.
internal static class ImagePropMaker
{
    private const int MinSize = 64, MaxSize = 2048;

    // Writes the files into folder (created).
    public static void Write(string image, ImageProp.Options options, string folder, int material, MetaSchema schema)
    {
        var (rgba, width, height) = TextureImport.Load(image);
        // A disc shows a square image: a longer image keeps its middle.
        int side = Math.Min(width, height);
        if (width != height)
            rgba = Crop(rgba, width, (width - side) / 2, (height - side) / 2, side);
        int size = Math.Clamp(1 << BitOperations.Log2((uint)side), MinSize, MaxSize);
        if (size != side)
            rgba = TextureImport.Resize(rgba, side, side, size, size);
        Flatten(rgba, size);

        // Compressed levels stop at 4 pixels (one block), as in the game's files.
        int levels = 1;
        while (size >> levels >= 4)
            levels++;
        string name = options.Name;
        var texture = new TextureData
        {
            NameHash = Joaat.Hash(name),
            Name = name,
            Width = size,
            Height = size,
            MipLevels = levels,
            Format = TextureFormat.Bc1,
            Data = TextureCodec.Encode(rgba, size, size, TextureFormat.Bc1, levels),
        };
        byte[] dds = DdsFile.Write(texture);

        var relief = options.Relief > 0 ? Relief(rgba, size, options.RaiseDark) : null;
        byte[] model = YdrXml.Convert(new XmlExport(name + ".ydr.xml", ImageProp.DrawableXml(options, name, material, relief),
            file => file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? dds : null));
        byte[] types = YtypXml.Convert(new XmlExport(name + ".ytyp.xml", ImageProp.TypesXml(options), _ => null), schema);
        Directory.CreateDirectory(folder);
        SafeFile.WriteAllBytes(Path.Combine(folder, name + ".ydr"), model);
        SafeFile.WriteAllBytes(Path.Combine(folder, name + ".ytyp"), types);
    }

    private static byte[] Crop(byte[] rgba, int width, int left, int top, int side)
    {
        var square = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            Buffer.BlockCopy(rgba, ((top + y) * width + left) * 4, square, y * side * 4, side * 4);
        return square;
    }

    // How raised each point of the image is (0 to 1): its brightness split into its two main tones inside the disc
    // (Otsu's threshold), the brighter one raised (or the darker) with a short slope between them, then softened over a
    // point. Read on at most 256 x 256 points.
    internal static Func<Vector2, float> Relief(byte[] rgba, int size, bool raiseDark)
    {
        int n = Math.Min(size, 256), step = size / n;
        var light = new float[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float sum = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    for (int dx = 0; dx < step; dx++)
                    {
                        int i = ((y * step + dy) * size + x * step + dx) * 4;
                        sum += 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2];
                    }
                }
                light[y * n + x] = sum / (step * step);
            }
        }

        var histogram = new long[256];
        float center = (n - 1) * 0.5f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (MathF.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) / n < ImageProp.FaceRadius)
                    histogram[Math.Clamp((int)light[y * n + x], 0, 255)]++;
            }
        }
        var (threshold, dark, bright) = Otsu(histogram);
        float slope = Math.Max(4f, (bright - dark) * 0.2f);

        var height = new float[n * n];
        for (int i = 0; i < height.Length; i++)
        {
            float s = Math.Clamp((light[i] - (threshold - slope)) / (2 * slope), 0f, 1f);
            s = s * s * (3 - 2 * s);
            height[i] = raiseDark ? 1 - s : s;
        }
        height = Soften(height, n);

        return uv =>
        {
            float fx = Math.Clamp(uv.X * n - 0.5f, 0, n - 1), fy = Math.Clamp(uv.Y * n - 0.5f, 0, n - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, n - 1), y1 = Math.Min(y0 + 1, n - 1);
            float tx = fx - x0, ty = fy - y0;
            float top = height[y0 * n + x0] + (height[y0 * n + x1] - height[y0 * n + x0]) * tx;
            float bottom = height[y1 * n + x0] + (height[y1 * n + x1] - height[y1 * n + x0]) * tx;
            return top + (bottom - top) * ty;
        };
    }

    // The brightness splitting the histogram best in two, and the mean of each part. The threshold is halfway between
    // the means: with two flat tones, any value between them splits as well.
    private static (float Threshold, float Dark, float Bright) Otsu(long[] histogram)
    {
        long total = 0;
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            total += histogram[i];
            sum += (double)i * histogram[i];
        }
        if (total == 0)
            return (128, 0, 255);
        long below = 0;
        double sumBelow = 0, best = -1;
        (float, float, float) result = (128, 0, 255);
        for (int t = 0; t < 255; t++)
        {
            below += histogram[t];
            sumBelow += (double)t * histogram[t];
            long above = total - below;
            if (below == 0 || above == 0)
                continue;
            double meanBelow = sumBelow / below, meanAbove = (sum - sumBelow) / above;
            double between = (double)below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                result = ((float)(meanBelow + meanAbove) * 0.5f, (float)meanBelow, (float)meanAbove);
            }
        }
        return result;
    }

    // Average over 3 x 3 points.
    private static float[] Soften(float[] map, int n)
    {
        var result = new float[map.Length];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float sum = 0;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= n || sy >= n)
                            continue;
                        sum += map[sy * n + sx];
                        count++;
                    }
                }
                result[y * n + x] = sum / count;
            }
        }
        return result;
    }

    // Transparent parts (a round badge's corners, its soft edge) take the rim's colour: the texture has no alpha.
    internal static void Flatten(byte[] rgba, int size)
    {
        long r = 0, g = 0, b = 0, weight = 0;
        float center = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) / size;
                if (d < ImageProp.RimRadius - 0.02f || d > ImageProp.RimRadius + 0.02f)
                    continue;
                int i = (y * size + x) * 4;
                int a = rgba[i + 3];
                r += rgba[i] * a;
                g += rgba[i + 1] * a;
                b += rgba[i + 2] * a;
                weight += a;
            }
        }
        // A fully transparent rim: grey.
        byte br = weight == 0 ? (byte)128 : (byte)(r / weight);
        byte bg = weight == 0 ? (byte)128 : (byte)(g / weight);
        byte bb = weight == 0 ? (byte)128 : (byte)(b / weight);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            rgba[i] = (byte)((rgba[i] * a + br * (255 - a)) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * a + bg * (255 - a)) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * a + bb * (255 - a)) / 255);
            rgba[i + 3] = 255;
        }
    }
}
