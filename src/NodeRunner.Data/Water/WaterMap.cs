using System.Globalization;
using System.Numerics;
using System.Xml;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Water;

// Shape of a WaterQuad: the whole rectangle, or the half of it left when one corner is cut off along the diagonal
// (value of the "Type" field of water.xml).
public enum WaterQuadShape
{
    Rectangle = 0,

    WithoutNorthEast = 1,

    WithoutSouthEast = 2,

    WithoutSouthWest = 3,

    WithoutNorthWest = 4,
}

// A flat piece of water surface at height Z (sea at 0, lakes and rivers higher).
public readonly record struct WaterQuad(float MinX, float MaxX, float MinY, float MaxY, float Z, WaterQuadShape Shape)
{
    public IEnumerable<Vector3> Triangles()
    {
        var southWest = new Vector3(MinX, MinY, Z);
        var southEast = new Vector3(MaxX, MinY, Z);
        var northWest = new Vector3(MinX, MaxY, Z);
        var northEast = new Vector3(MaxX, MaxY, Z);
        Vector3[] corners = Shape switch
        {
            WaterQuadShape.WithoutNorthEast => [southWest, southEast, northWest],
            WaterQuadShape.WithoutSouthEast => [southWest, northEast, northWest],
            WaterQuadShape.WithoutSouthWest => [southEast, northEast, northWest],
            WaterQuadShape.WithoutNorthWest => [southWest, southEast, northEast],
            _ => [southWest, southEast, northWest, northWest, southEast, northEast],
        };
        return corners;
    }
}

// Water surfaces of the map, read from the game's water.xml (and the Cayo Perico one): flat rectangles and triangles.
public sealed class WaterMap
{
    public static readonly string[] WaterFiles =
    [
        @"common.rpf\data\levels\gta5\water.xml",
        @"update\update.rpf\common\data\levels\gta5\water_heistisland.xml",
    ];

    public List<WaterQuad> Quads { get; } = [];

    // Rectangle covered by the main map's water file (min X, min Y, max X, max Y); the open ocean is outside.
    public Vector4 OceanBounds { get; private set; }

    public List<string> Errors { get; } = [];

    public static WaterMap Load(IGameFileSystem files)
    {
        var map = new WaterMap();
        for (int i = 0; i < WaterFiles.Length; i++)
        {
            var path = WaterFiles[i];
            var data = files.ReadFile(path);
            if (data is null)
            {
                if (i == 0)
                    map.Errors.Add($"{path} : fichier introuvable");
                continue;
            }
            try
            {
                var quads = Parse(new MemoryStream(data));
                if (i == 0)
                    map.OceanBounds = BoundsOf(quads);
                map.Quads.AddRange(quads);
            }
            catch (XmlException ex)
            {
                map.Errors.Add($"{path} : {ex.Message}");
            }
        }
        return map;
    }

    public static List<WaterQuad> Parse(Stream xml)
    {
        var quads = new List<WaterQuad>();
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
        if (!reader.ReadToFollowing("WaterQuads") || reader.IsEmptyElement)
            return quads;
        using var list = reader.ReadSubtree();
        while (list.ReadToFollowing("Item"))
        {
            var values = new Dictionary<string, string>();
            using var item = list.ReadSubtree();
            item.Read();
            while (item.Read())
            {
                if (item.NodeType == XmlNodeType.Element && item.GetAttribute("value") is { } value)
                    values[item.Name] = value;
            }

            float Get(string name) => values.TryGetValue(name, out var text)
                && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
            if (values.TryGetValue("IsInvisible", out var invisible) && invisible.Equals("true", StringComparison.OrdinalIgnoreCase))
                continue;
            int type = (int)Get("Type");
            quads.Add(new WaterQuad(Get("minX"), Get("maxX"), Get("minY"), Get("maxY"), Get("z"),
                Enum.IsDefined((WaterQuadShape)type) ? (WaterQuadShape)type : WaterQuadShape.Rectangle));
        }
        return quads;
    }

    private static Vector4 BoundsOf(List<WaterQuad> quads) => quads.Count == 0
        ? Vector4.Zero
        : new Vector4(quads.Min(q => q.MinX), quads.Min(q => q.MinY), quads.Max(q => q.MaxX), quads.Max(q => q.MaxY));
}
