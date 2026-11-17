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

        byte[] model = YdrXml.Convert(new XmlExport(name + ".ydr.xml", ImageProp.DrawableXml(options, name, material),
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
