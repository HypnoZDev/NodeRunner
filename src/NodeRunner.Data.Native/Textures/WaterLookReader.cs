using System.Globalization;
using System.Numerics;
using System.Xml;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Textures;
using NodeRunner.Data.Water;

namespace NodeRunner.Data.Native.Textures;

// The look of GTA V's water from the game's files: colour of the water per place (waterfog images, placed by the
// FogMin / FogMax of watertune.xml) and the light pattern on the bottom (caustic).
public static class WaterLookReader
{
    private static readonly string[] GraphicsFiles =
    [
        @"update\update.rpf\x64\textures\graphics.ytd",
        @"x64a.rpf\textures\graphics.ytd",
    ];

    private static readonly (string Tune, string Texture)[] ColorFiles =
    [
        (@"common.rpf\data\watertune.xml", "waterfog"),
        (@"update\update.rpf\common\data\watertune_heistisland.xml", "waterfog_h4"),
    ];

    // Adds what is found to the map; a missing or unreadable piece is left out (the water keeps a plain colour).
    public static void Load(IGameFileSystem files, WaterMap map)
    {
        var ytd = new YtdReader(files);
        var dictionaries = new List<IReadOnlyDictionary<uint, TextureData>>();
        foreach (var path in GraphicsFiles)
        {
            try
            {
                if (files.FileExists(path))
                    dictionaries.Add(ytd.ReadTextures(path));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
            {
                map.Errors.Add($"{path} : {ex.Message}");
            }
        }

        byte[]? Image(string name, out TextureData? found)
        {
            found = null;
            foreach (var textures in dictionaries)
            {
                if (textures.TryGetValue(Joaat.Hash(name), out var texture) && TextureCodec.CanDecode(texture.Format))
                {
                    found = texture;
                    return TextureCodec.Decode(texture);
                }
            }
            return null;
        }

        foreach (var (tune, name) in ColorFiles)
        {
            var area = FogArea(files.ReadFile(tune));
            if (area is null || Image(name, out var texture) is not { } rgba || texture is null)
                continue;
            map.ColorMaps.Add(new WaterColorMap(texture.Width, texture.Height, rgba, area.Value));
        }

        if (Image("caustic", out var caustic) is { } pattern && caustic is not null)
        {
            var values = new byte[caustic.Width * caustic.Height];
            for (int i = 0; i < values.Length; i++)
                values[i] = pattern[i * 4];
            map.Caustics = new WaterPattern(caustic.Width, caustic.Height, values);
        }
    }

    // Rectangle covered by the colour image (min x, min y, max x, max y); null if the file lacks it.
    public static Vector4? FogArea(byte[]? xml)
    {
        if (xml is null)
            return null;
        Vector2? min = null, max = null;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("FogMin" or "FogMax"))
                    continue;
                if (!float.TryParse(reader.GetAttribute("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(reader.GetAttribute("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                    continue;
                if (reader.Name == "FogMin")
                    min = new Vector2(x, y);
                else
                    max = new Vector2(x, y);
            }
        }
        catch (XmlException)
        {
            return null;
        }
        if (min is not { } a || max is not { } b || b.X <= a.X || b.Y <= a.Y)
            return null;
        return new Vector4(a.X, a.Y, b.X, b.Y);
    }
}
