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

    // The plaque's shape for an image: round when its corners are see-through and the ring just inside its edge is not
    // (a round badge), otherwise a rectangle of its proportions (height / width).
    public static (bool Round, float Aspect) Shape(string image)
    {
        var (rgba, width, height) = TextureImport.Load(image);
        return Shape(rgba, width, height);
    }

    internal static (bool Round, float Aspect) Shape(byte[] rgba, int width, int height)
    {
        float aspect = Math.Clamp((float)height / width, ImageProp.MinAspect, ImageProp.MaxAspect);
        if (MathF.Abs(aspect - 1) > 0.1f)
            return (false, aspect);
        float cx = (width - 1) * 0.5f, cy = (height - 1) * 0.5f, side = Math.Min(width, height);
        int Alpha(float x, float y) => rgba[(Math.Clamp((int)y, 0, height - 1) * width + Math.Clamp((int)x, 0, width - 1)) * 4 + 3];
        bool cornersClear = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }.All(c => Alpha(cx + c.Item1 * 0.45f * side, cy + c.Item2 * 0.45f * side) < 128);
        float rim = 0;
        for (int i = 0; i < 64; i++)
            rim += Alpha(cx + 0.44f * side * MathF.Cos(MathF.Tau * i / 64), cy + 0.44f * side * MathF.Sin(MathF.Tau * i / 64));
        return cornersClear && rim / 64 > 180 ? (true, 1) : (false, aspect);
    }

    // Writes the files into folder (created).
    public static void Write(string image, ImageProp.Options options, string folder, int material, MetaSchema schema)
    {
        var (rgba, width, height) = TextureImport.Load(image);
        // A disc shows a square image: a longer image keeps its middle.
        if (options.Round && width != height)
        {
            int side = Math.Min(width, height);
            rgba = Crop(rgba, width, (width - side) / 2, (height - side) / 2, side);
            width = height = side;
        }
        int w = Math.Clamp(1 << BitOperations.Log2((uint)width), MinSize, MaxSize);
        int h = Math.Clamp(1 << BitOperations.Log2((uint)height), MinSize, MaxSize);
        if (w != width || h != height)
            rgba = TextureImport.Resize(rgba, width, height, w, h);
        Flatten(rgba, w, h, options.Round);

        // Compressed levels stop at 4 pixels (one block), as in the game's files.
        int levels = 1;
        while (Math.Min(w, h) >> levels >= 4)
            levels++;
        string name = options.Name;
        var texture = new TextureData
        {
            NameHash = Joaat.Hash(name),
            Name = name,
            Width = w,
            Height = h,
            MipLevels = levels,
            Format = TextureFormat.Bc1,
            Data = TextureCodec.Encode(rgba, w, h, TextureFormat.Bc1, levels),
        };
        byte[] dds = DdsFile.Write(texture);

        var relief = options.Relief > 0 ? Relief(rgba, w, h, options.RaiseDark, options.Round) : null;
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

    // How far a pixel is from the image's centre: 0 there, 1 on the edge of the face (the disc's rim, or the
    // rectangle's border).
    private static float Reach(float x, float y, int width, int height, bool round)
    {
        float px = (x - (width - 1) * 0.5f) / (width * 0.5f), py = (y - (height - 1) * 0.5f) / (height * 0.5f);
        return round ? MathF.Sqrt(px * px + py * py) : Math.Max(MathF.Abs(px), MathF.Abs(py));
    }

    // How raised each point of the image is (0 to 1): its brightness split into its two main tones on the face
    // (Otsu's threshold), the brighter one raised (or the darker) with a short slope between them, then softened over a
    // point. Read on at most 256 x 256 points.
    internal static Func<Vector2, float> Relief(byte[] rgba, int width, int height, bool raiseDark, bool round)
    {
        int nx = Math.Min(width, 256), ny = Math.Min(height, 256);
        int sx = width / nx, sy = height / ny;
        var light = new float[nx * ny];
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                float sum = 0;
                for (int dy = 0; dy < sy; dy++)
                {
                    for (int dx = 0; dx < sx; dx++)
                    {
                        int i = ((y * sy + dy) * width + x * sx + dx) * 4;
                        sum += 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2];
                    }
                }
                light[y * nx + x] = sum / (sx * sy);
            }
        }

        var histogram = new long[256];
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                if (Reach(x, y, nx, ny, round) < 2 * ImageProp.FaceRadius)
                    histogram[Math.Clamp((int)light[y * nx + x], 0, 255)]++;
            }
        }
        var (threshold, dark, bright) = Otsu(histogram);
        float slope = Math.Max(4f, (bright - dark) * 0.2f);

        var map = new float[nx * ny];
        for (int i = 0; i < map.Length; i++)
        {
            float s = Math.Clamp((light[i] - (threshold - slope)) / (2 * slope), 0f, 1f);
            s = s * s * (3 - 2 * s);
            map[i] = raiseDark ? 1 - s : s;
        }
        map = Soften(map, nx, ny);

        return uv =>
        {
            float fx = Math.Clamp(uv.X * nx - 0.5f, 0, nx - 1), fy = Math.Clamp(uv.Y * ny - 0.5f, 0, ny - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, nx - 1), y1 = Math.Min(y0 + 1, ny - 1);
            float tx = fx - x0, ty = fy - y0;
            float top = map[y0 * nx + x0] + (map[y0 * nx + x1] - map[y0 * nx + x0]) * tx;
            float bottom = map[y1 * nx + x0] + (map[y1 * nx + x1] - map[y1 * nx + x0]) * tx;
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
    private static float[] Soften(float[] map, int width, int height)
    {
        var result = new float[map.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= width || sy >= height)
                            continue;
                        sum += map[sy * width + sx];
                        count++;
                    }
                }
                result[y * width + x] = sum / count;
            }
        }
        return result;
    }

    // Transparent parts (a round badge's corners, a logo's background) take the rim's colour: the texture has no alpha.
    internal static void Flatten(byte[] rgba, int width, int height, bool round)
    {
        long r = 0, g = 0, b = 0, weight = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (MathF.Abs(Reach(x, y, width, height, round) - 2 * ImageProp.RimRadius) > 0.04f)
                    continue;
                int i = (y * width + x) * 4;
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
