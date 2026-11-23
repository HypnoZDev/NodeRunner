using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace NodeRunner.Data.Native.Collisions;

// Collision files in the XML form exported by Sollumz (and CodeWalker): <BoundsFile><Bounds type="Composite">...
public static partial class YbnXml
{
    // A complete .ybn file (RSC7) from the XML text.
    public static byte[] Convert(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}");
        }
        var root = document.Root ?? throw new InvalidDataException("XML vide.");
        var bounds = root.Name.LocalName == "Bounds" ? root : root.Element("Bounds")
            ?? throw new InvalidDataException("Balise <Bounds> introuvable : ce n'est pas un fichier de collision.");
        var bound = ReadBound(bounds, isChild: false)
            ?? throw new InvalidDataException("La collision est vide (type None).");
        return YbnBuilder.Build(bound);
    }

    internal static XmlBound? ReadBound(XElement e, bool isChild)
    {
        string type = (string?)e.Attribute("type") ?? "";
        byte kind = type switch
        {
            "None" => 255,
            "Sphere" => XmlBound.Sphere,
            "Capsule" => XmlBound.Capsule,
            "Box" => XmlBound.Box,
            "Geometry" => XmlBound.Geometry,
            "GeometryBVH" => XmlBound.GeometryBvh,
            "Composite" => XmlBound.Composite,
            "Disc" => XmlBound.Disc,
            "Cylinder" => XmlBound.Cylinder,
            "Cloth" => XmlBound.Plane,
            _ => throw new InvalidDataException($"Type de collision inconnu : « {type} »."),
        };
        if (kind == 255)
            return null;

        var b = new XmlBound
        {
            Type = kind,
            BoxMin = Vec(e, "BoxMin"),
            BoxMax = Vec(e, "BoxMax"),
            BoxCenter = Vec(e, "BoxCenter"),
            SphereCenter = Vec(e, "SphereCenter"),
            SphereRadius = Float(e, "SphereRadius", 0),
            Margin = Float(e, "Margin", 0.005f),
            Volume = Float(e, "Volume", 0),
            Inertia = Vec(e, "Inertia"),
            MaterialIndex = (byte)Int(e, "MaterialIndex", 0),
            MaterialColourIndex = (byte)Int(e, "MaterialColourIndex", 0),
            ProceduralId = (byte)Int(e, "ProceduralID", 0),
            RoomId = (byte)Int(e, "RoomID", 0),
            PedDensity = (byte)Int(e, "PedDensity", 0),
            UnkFlags = (byte)Int(e, "UnkFlags", 0),
            PolyFlags = (byte)Int(e, "PolyFlags", 0),
            UnkType = (uint)Int(e, "UnkType", 1),
        };

        if (isChild)
        {
            if (e.Element("CompositeTransform") is { } transform)
            {
                var values = Floats(transform.Value);
                if (values.Count != 16)
                    throw new InvalidDataException($"CompositeTransform doit contenir 16 nombres ({values.Count} trouvés).");
                b.Transform = new Matrix4x4(
                    values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                    values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
            }
            b.Flags1 = Flags(e.Element("CompositeFlags1")?.Value, CompositeFlagNames, "CompositeFlags1");
            b.Flags2 = Flags(e.Element("CompositeFlags2")?.Value, CompositeFlagNames, "CompositeFlags2");
        }

        if (kind is XmlBound.Geometry or XmlBound.GeometryBvh)
            ReadGeometry(e, b);
        if (kind == XmlBound.Composite)
        {
            foreach (var item in e.Element("Children")?.Elements("Item") ?? [])
                b.Children.Add(ReadBound(item, isChild: true));
        }
        return b;
    }

    private static void ReadGeometry(XElement e, XmlBound b)
    {
        b.GeometryCenter = Vec(e, "GeometryCenter");
        // Sollumz leaves these out; every map collision of the game has these values.
        b.UnkFloat1 = Float(e, "UnkFloat1", 7.6296274E-08f);
        b.UnkFloat2 = Float(e, "UnkFloat2", 0.0025f);

        foreach (var m in e.Element("Materials")?.Elements("Item") ?? [])
        {
            uint flags = Flags(m.Element("Flags")?.Value, MaterialFlagNames, "Flags");
            uint data1 = (uint)(Int(m, "Type", 0) & 0xFF)
                | (uint)(Int(m, "ProceduralID", 0) & 0xFF) << 8
                | (uint)(Int(m, "RoomID", 0) & 0x1F) << 16
                | (uint)(Int(m, "PedDensity", 0) & 0x7) << 21
                | (flags & 0xFF) << 24;
            uint data2 = (flags >> 8 & 0xFF)
                | (uint)(Int(m, "MaterialColourIndex", 0) & 0xFF) << 8
                | (uint)(Int(m, "Unk", 0) & 0xFFFF) << 16;
            b.Materials.Add((data1, data2));
        }

        b.MaterialColours = Colours(e.Element("MaterialColours"));
        b.VertexColours = Colours(e.Element("VertexColours"));
        if (e.Element("Vertices") is { } vertices)
        {
            var values = Floats(vertices.Value);
            if (values.Count % 3 != 0)
                throw new InvalidDataException("Liste de sommets incomplète (3 nombres par sommet).");
            for (int i = 0; i < values.Count; i += 3)
                b.Vertices.Add(new Vector3(values[i], values[i + 1], values[i + 2]));
        }

        foreach (var p in e.Element("Polygons")?.Elements() ?? [])
        {
            var polygon = new XmlPolygon { Material = Attr(p, "m", 0) };
            switch (p.Name.LocalName)
            {
                case "Triangle":
                    polygon.Kind = XmlPolygon.Triangle;
                    polygon.V1 = Attr(p, "v1", 0);
                    polygon.V2 = Attr(p, "v2", 0);
                    polygon.V3 = Attr(p, "v3", 0);
                    polygon.VertexFlags = (Attr(p, "f1", 0) != 0 ? 1 : 0) | (Attr(p, "f2", 0) != 0 ? 2 : 0)
                        | (Attr(p, "f3", 0) != 0 ? 4 : 0);
                    break;
                case "Sphere":
                    polygon.Kind = XmlPolygon.Sphere;
                    polygon.V1 = Attr(p, "v", 0);
                    polygon.Radius = AttrFloat(p, "radius");
                    break;
                case "Capsule":
                case "Cylinder":
                    polygon.Kind = p.Name.LocalName == "Capsule" ? XmlPolygon.Capsule : XmlPolygon.Cylinder;
                    polygon.V1 = Attr(p, "v1", 0);
                    polygon.V2 = Attr(p, "v2", 1);
                    polygon.Radius = AttrFloat(p, "radius");
                    break;
                case "Box":
                    polygon.Kind = XmlPolygon.Box;
                    polygon.V1 = Attr(p, "v1", 0);
                    polygon.V2 = Attr(p, "v2", 1);
                    polygon.V3 = Attr(p, "v3", 2);
                    polygon.V4 = Attr(p, "v4", 3);
                    break;
                default:
                    continue;
            }
            b.Polygons.Add(polygon);
        }
    }

    // Names used by the XML for the composite children's collision types (bit i = name i).
    private static readonly string[] CompositeFlagNames =
    [
        "UNKNOWN", "MAP_WEAPON", "MAP_DYNAMIC", "MAP_ANIMAL", "MAP_COVER", "MAP_VEHICLE", "VEHICLE_NOT_BVH",
        "VEHICLE_BVH", "VEHICLE_BOX", "PED", "RAGDOLL", "ANIMAL", "ANIMAL_RAGDOLL", "OBJECT", "OBJECT_ENV_CLOTH",
        "PLANT", "PROJECTILE", "EXPLOSION", "PICKUP", "FOLIAGE", "FORKLIFT_FORKS", "TEST_WEAPON", "TEST_CAMERA",
        "TEST_AI", "TEST_SCRIPT", "TEST_VEHICLE_WHEEL", "GLASS", "MAP_RIVER", "SMOKE", "UNSMASHED", "MAP_STAIRS",
        "MAP_DEEP_SURFACE",
    ];

    private static readonly string[] MaterialFlagNames =
    [
        "FLAG_STAIRS", "FLAG_NOT_CLIMBABLE", "FLAG_SEE_THROUGH", "FLAG_SHOOT_THROUGH", "FLAG_NOT_COVER",
        "FLAG_WALKABLE_PATH", "FLAG_NO_CAM_COLLISION", "FLAG_SHOOT_THROUGH_FX", "FLAG_NO_DECAL", "FLAG_NO_NAVMESH",
        "FLAG_NO_RAGDOLL", "FLAG_VEHICLE_WHEEL", "FLAG_NO_PTFX", "FLAG_TOO_STEEP_FOR_PLAYER", "FLAG_NO_NETWORK_SPAWN",
        "FLAG_NO_CAM_COLLISION_ALLOW_CLIPPING",
    ];

    private static uint Flags(string? text, string[] names, string what)
    {
        uint value = 0;
        foreach (var token in (text ?? "").Split([',', ' ', '\t', '\r', '\n', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "NONE")
                continue;
            int bit = Array.IndexOf(names, token);
            if (bit >= 0)
                value |= 1u << bit;
            else if (uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number))
                value |= number;
            else
                throw new InvalidDataException($"{what} : drapeau inconnu « {token} ».");
        }
        return value;
    }

    private static List<uint>? Colours(XElement? e)
    {
        if (e is null || string.IsNullOrWhiteSpace(e.Value))
            return null;
        var list = new List<uint>();
        foreach (var line in e.Value.Split('\n'))
        {
            var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                continue;
            uint c = 0;
            for (int i = 0; i < Math.Min(4, parts.Length); i++)
                c |= (uint)(byte.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : 0) << (8 * i);
            list.Add(c);
        }
        return list.Count > 0 ? list : null;
    }

    private static List<float> Floats(string text)
    {
        var list = new List<float>();
        foreach (var token in text.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            list.Add(ParseFloat(token));
        return list;
    }

    private static float ParseFloat(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : throw new InvalidDataException($"Nombre invalide : « {text} ».");

    private static Vector3 Vec(XElement e, string name) => e.Element(name) is { } c
        ? new Vector3(AttrFloat(c, "x"), AttrFloat(c, "y"), AttrFloat(c, "z"))
        : Vector3.Zero;

    private static float Float(XElement e, string name, float fallback) =>
        e.Element(name)?.Attribute("value") is { } a ? ParseFloat(a.Value) : fallback;

    private static long Int(XElement e, string name, long fallback) =>
        e.Element(name)?.Attribute("value") is { } a ? ParseInt(a.Value) : fallback;

    private static int Attr(XElement e, string name, int fallback) =>
        e.Attribute(name) is { } a ? (int)ParseInt(a.Value) : fallback;

    private static float AttrFloat(XElement e, string name) => e.Attribute(name) is { } a ? ParseFloat(a.Value) : 0;

    private static long ParseInt(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v
        : float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? (long)f
        : throw new InvalidDataException($"Nombre entier invalide : « {text} ».");
}

internal sealed class XmlBound
{
    public const byte Sphere = 0, Capsule = 1, Box = 3, Geometry = 4, GeometryBvh = 8, Composite = 10, Disc = 12,
        Cylinder = 13, Plane = 15;

    public byte Type;
    public Vector3 BoxMin, BoxMax, BoxCenter, SphereCenter, Inertia;
    public float SphereRadius, Margin, Volume;
    public byte MaterialIndex, MaterialColourIndex, ProceduralId, RoomId, PedDensity, UnkFlags, PolyFlags;
    public uint UnkType = 1;

    public Matrix4x4 Transform = Matrix4x4.Identity;
    public uint Flags1, Flags2;

    public Vector3 GeometryCenter;
    public float UnkFloat1, UnkFloat2;
    public List<(uint Data1, uint Data2)> Materials = [];
    public List<uint>? MaterialColours, VertexColours;
    public List<Vector3> Vertices = [];
    public List<XmlPolygon> Polygons = [];

    public List<XmlBound?> Children = [];
}

internal struct XmlPolygon
{
    public const byte Triangle = 0, Sphere = 1, Capsule = 2, Box = 3, Cylinder = 4;

    public byte Kind;
    public int Material;
    public int V1, V2, V3, V4;
    public int VertexFlags;
    public float Radius;
}
