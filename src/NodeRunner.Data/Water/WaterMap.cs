using System.Globalization;
using System.Numerics;
using System.Xml;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Water;

/// <summary>
/// Shape of a <see cref="WaterQuad"/>: the whole rectangle, or the half of it left when one corner is cut off
/// along the diagonal (value of the "Type" field of water.xml).
/// </summary>
public enum WaterQuadShape
{
    Rectangle = 0,

    /// <summary>Triangle without the (max X, max Y) corner.</summary>
    WithoutNorthEast = 1,

    /// <summary>Triangle without the (max X, min Y) corner.</summary>
    WithoutSouthEast = 2,

    /// <summary>Triangle without the (min X, min Y) corner.</summary>
    WithoutSouthWest = 3,

    /// <summary>Triangle without the (min X, max Y) corner.</summary>
    WithoutNorthWest = 4,
}

/// <summary>A flat piece of water surface at height <paramref name="Z"/> (sea at 0, lakes and rivers higher).</summary>
public readonly record struct WaterQuad(float MinX, float MaxX, float MinY, float MaxY, float Z, WaterQuadShape Shape)
{
    /// <summary>Corners of the surface, three per triangle (one or two triangles).</summary>
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

/// <summary>
/// Water surfaces of the map, read from the game's water.xml (and the Cayo Perico one): flat rectangles and triangles.
/// The game also draws the ocean beyond these surfaces, up to the horizon (see <see cref="OceanBounds"/>).
/// </summary>
public sealed class WaterMap
{
    /// <summary>Water of the main map, then of Cayo Perico (present in update.rpf whatever the DLC packs).</summary>
    public static readonly string[] WaterFiles =
    [
        @"common.rpf\data\levels\gta5\water.xml",
        @"update\update.rpf\common\data\levels\gta5\water_heistisland.xml",
    ];

    public List<WaterQuad> Quads { get; } = [];

    /// <summary>Rectangle covered by the main map's water file (min X, min Y, max X, max Y); the open ocean is outside.</summary>
    public Vector4 OceanBounds { get; private set; }

    /// <summary>Files that could not be read, with the reason.</summary>
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

    /// <summary>The water surfaces of a water.xml file (the "WaterQuads" list; calming and wave areas are ignored).</summary>
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
