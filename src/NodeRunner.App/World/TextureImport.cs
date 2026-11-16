using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;

namespace NodeRunner.App.World;

// Turns an image (.png, .jpg, .bmp and the other formats Windows reads, or .dds) into the pixels of a game texture: its
// size, its format, its mip levels. Also writes a texture out as .png or .dds.
internal static class TextureImport
{
    public const string OpenFilter = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.dds";

    // The new pixels of the texture (every mip level, in its format), and a note when the image had to be resized.
    public static (byte[] Pixels, string? Note) Convert(string file, StoredTexture target)
    {
        byte[] rgba;
        int width, height;
        if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            var dds = DdsFile.Read(File.ReadAllBytes(file), target.Name);
            // Already in the game's format and size: kept as it is (no second compression).
            if (dds.Format == target.Format && dds.Width == target.Width && dds.Height == target.Height && dds.MipLevels >= target.MipLevels)
                return (dds.Data, null);
            if (!TextureCodec.CanDecode(dds.Format))
                throw new InvalidDataException(L($"Cette image .dds ({dds.Format}, {dds.Width} x {dds.Height}) doit avoir le format et la taille de la texture ({target.Format}, {target.Width} x {target.Height})."));
            (rgba, width, height) = (TextureCodec.Decode(dds), dds.Width, dds.Height);
        }
        else
            (rgba, width, height) = Load(file);

        string? note = null;
        if (width != target.Width || height != target.Height)
        {
            note = L($"Image mise à la taille de la texture : {width} x {height} -> {target.Width} x {target.Height}.");
            rgba = Resize(rgba, width, height, target.Width, target.Height);
        }
        return (TextureCodec.Encode(rgba, target.Width, target.Height, target.Format, target.MipLevels), note);
    }

    // The texture at another size (same format; levels follow the size: one more each time it doubles).
    public static TextureData Resize(TextureData texture, int width, int height)
    {
        if (!TextureCodec.CanDecode(texture.Format))
            throw new InvalidDataException(L($"Les textures {texture.Format} ne peuvent pas encore changer de taille."));
        var rgba = Resize(TextureCodec.Decode(texture), texture.Width, texture.Height, width, height);
        int levels = LevelsFor(texture, width, height);
        return new TextureData
        {
            NameHash = texture.NameHash,
            Name = texture.Name,
            Width = width,
            Height = height,
            MipLevels = levels,
            Format = texture.Format,
            Data = TextureCodec.Encode(rgba, width, height, texture.Format, levels),
        };
    }

    public static int LevelsFor(TextureData texture, int width, int height)
    {
        int steps = (int)MathF.Round(MathF.Log2((float)width / texture.Width));
        // Compressed levels stop at 4 pixels (one block), as in the game's files.
        int smallest = TextureLayout.IsBlockCompressed(texture.Format) ? 4 : 1;
        int most = 1;
        while (Math.Min(width, height) >> most >= smallest)
            most++;
        return Math.Clamp(texture.MipLevels + steps, 1, most);
    }

    // Bytes of the texture at this size, every level included.
    public static long Bytes(TextureData texture, int width, int height)
    {
        long total = 0;
        int levels = LevelsFor(texture, width, height);
        for (int i = 0; i < levels; i++)
            total += TextureLayout.Pitch(texture.Format, Math.Max(1, width >> i), Math.Max(1, height >> i)).SlicePitch;
        return total;
    }

    // Writes the texture as an image: .dds keeps it exactly (every level, its format), .png its largest level.
    public static void Export(TextureData texture, string file)
    {
        if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            SafeFile.WriteAllBytes(file, DdsFile.Write(texture));
            return;
        }
        var rgba = TextureCodec.Decode(texture);
        using var bitmap = ToBitmap(rgba, texture.Width, texture.Height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        SafeFile.WriteAllBytes(file, stream.ToArray());
    }

    private static (byte[] Rgba, int Width, int Height) Load(string file)
    {
        try
        {
            // Read from memory so the file is not kept locked.
            using var stream = new MemoryStream(File.ReadAllBytes(file));
            using var image = Image.FromStream(stream);
            using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
            }
            return (FromBitmap(bitmap), bitmap.Width, bitmap.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ reports an unreadable image as one of these.
            throw new InvalidDataException(L($"{Path.GetFileName(file)} : image illisible."));
        }
    }

    private static byte[] Resize(byte[] rgba, int width, int height, int newWidth, int newHeight)
    {
        using var source = ToBitmap(rgba, width, height);
        using var target = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(target))
        using (var attributes = new ImageAttributes())
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Edges mirrored instead of fading to transparent.
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle(0, 0, newWidth, newHeight), 0, 0, width, height, GraphicsUnit.Pixel, attributes);
        }
        return FromBitmap(target);
    }

    // Bitmaps hold blue, green, red, alpha.
    private static byte[] FromBitmap(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rgba = new byte[w * h * 4];
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, rgba, y * w * 4, w * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        SwapRedBlue(rgba);
        return rgba;
    }

    private static Bitmap ToBitmap(byte[] rgba, int w, int h)
    {
        var bgra = (byte[])rgba.Clone();
        SwapRedBlue(bgra);
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}
