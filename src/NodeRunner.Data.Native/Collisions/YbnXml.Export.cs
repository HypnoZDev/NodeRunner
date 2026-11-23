using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Collisions;

// Collision files of the game written out as XML, in the layout read back by Convert (and by Sollumz / CodeWalker).
public static partial class YbnXml
{
    private const int BoundsLength = 0x70;
    private const int CompositeLength = 0xB0;
    private const int GeometryLength = 0x130;

    public static XmlConversion Export(string fileName, ReadOnlySpan<byte> data, ResourceHeader header)
    {
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        var xml = new XmlText();
        xml.Open("BoundsFile");
        WriteBound(xml, res, 0, "Bounds", child: null);
        xml.Close("BoundsFile");
        return new XmlConversion(fileName + ".xml", xml.ToString(), []);
    }

    // The composite child's transform and flags, when the bounds is one.
    internal readonly record struct ChildInfo(Matrix4x4 Transform, uint Flags1, uint Flags2);

    // Bounds at offset (or a "None" element when offset is negative), under the given element name. In a fragment, the
    // parts of a composite are written without their collision types (as CodeWalker does).
    internal static void WriteBound(XmlText xml, ResourceData res, int offset, string name, ChildInfo? child, bool fragment = false)
    {
        if (offset < 0)
        {
            xml.Empty(name, "type=\"None\"");
            return;
        }
        byte type = res.Byte(offset + 0x10);
        string typeName = type switch
        {
            XmlBound.Sphere => "Sphere",
            XmlBound.Capsule => "Capsule",
            XmlBound.Box => "Box",
            XmlBound.Geometry => "Geometry",
            XmlBound.GeometryBvh => "GeometryBVH",
            XmlBound.Composite => "Composite",
            XmlBound.Disc => "Disc",
            XmlBound.Cylinder => "Cylinder",
            XmlBound.Plane => "Cloth",
            _ => throw new InvalidDataException($"Type de collision {type} inconnu."),
        };
        xml.Open(name, $"type=\"{typeName}\"");

        xml.Vector("BoxMin", Vec3(res, offset + 0x30));
        xml.Vector("BoxMax", Vec3(res, offset + 0x20));
        xml.Vector("BoxCenter", Vec3(res, offset + 0x40));
        xml.Vector("SphereCenter", Vec3(res, offset + 0x50));
        xml.Value("SphereRadius", Float(res, offset + 0x14));
        xml.Value("Margin", Float(res, offset + 0x2C));
        xml.Value("Volume", Float(res, offset + 0x6C));
        xml.Vector("Inertia", Vec3(res, offset + 0x60));
        xml.Value("MaterialIndex", res.Byte(offset + 0x4C));
        xml.Value("MaterialColourIndex", res.Byte(offset + 0x5D));
        xml.Value("ProceduralID", res.Byte(offset + 0x4D));
        byte roomAndDensity = res.Byte(offset + 0x4E);
        xml.Value("RoomID", roomAndDensity & 0x1F);
        xml.Value("PedDensity", roomAndDensity >> 5);
        xml.Value("UnkFlags", res.Byte(offset + 0x4F));
        xml.Value("PolyFlags", res.Byte(offset + 0x5C));
        xml.Value("UnkType", res.UInt32(offset + 0x3C));
        if (child is { } c)
        {
            var m = c.Transform;
            xml.Lines("CompositeTransform", null,
            [
                Row(m.M11, m.M12, m.M13, m.M14), Row(m.M21, m.M22, m.M23, m.M24),
                Row(m.M31, m.M32, m.M33, m.M34), Row(m.M41, m.M42, m.M43, m.M44),
            ]);
            if (!fragment)
            {
                xml.Text("CompositeFlags1", FlagNames(c.Flags1, CompositeFlagNames));
                xml.Text("CompositeFlags2", FlagNames(c.Flags2, CompositeFlagNames));
            }
        }

        if (type is XmlBound.Geometry or XmlBound.GeometryBvh)
            WriteGeometry(xml, res, offset);
        else if (type == XmlBound.Composite)
            WriteChildren(xml, res, offset, fragment);
        xml.Close(name);
    }

    private static void WriteChildren(XmlText xml, ResourceData res, int o, bool fragment)
    {
        int count = res.UInt16(o + 0xA0);
        if (count == 0 || res.UInt64(o + 0x70) == 0)
        {
            xml.Empty("Children");
            return;
        }
        int children = res.ResolveArray(res.UInt64(o + 0x70), count, 8, "morceaux du composite");
        ulong transformsPointer = res.UInt64(o + 0x78) is not 0 and var t1 ? t1 : res.UInt64(o + 0x80);
        int transforms = transformsPointer == 0 ? -1 : res.ResolveArray(transformsPointer, count, 64, "positions des morceaux");
        int flags = res.UInt64(o + 0x90) == 0 ? -1 : res.ResolveArray(res.UInt64(o + 0x90), count, 8, "types des morceaux");
        xml.Open("Children");
        for (int i = 0; i < count; i++)
        {
            ulong pointer = res.UInt64(children + i * 8);
            int at = pointer == 0 ? -1 : res.ResolveArray(pointer, 1, BoundsLength, "morceau du composite");
            var transform = Matrix4x4.Identity;
            if (transforms >= 0)
            {
                int m = transforms + i * 64;
                transform = new Matrix4x4(
                    Float(res, m), Float(res, m + 4), Float(res, m + 8), 0,
                    Float(res, m + 16), Float(res, m + 20), Float(res, m + 24), 0,
                    Float(res, m + 32), Float(res, m + 36), Float(res, m + 40), 0,
                    Float(res, m + 48), Float(res, m + 52), Float(res, m + 56), 1);
            }
            var info = new ChildInfo(transform, flags >= 0 ? res.UInt32(flags + i * 8) : 0, flags >= 0 ? res.UInt32(flags + i * 8 + 4) : 0);
            if (at >= 0)
                res.ResolveArray(pointer, 1, BlockLength(res.Byte(at + 0x10)), "morceau du composite");
            WriteBound(xml, res, at, "Item", info, fragment);
        }
        xml.Close("Children");
    }

    private static int BlockLength(byte type) => type switch
    {
        XmlBound.Composite => CompositeLength,
        XmlBound.Geometry or XmlBound.GeometryBvh => GeometryLength,
        _ => BoundsLength,
    };

    private static void WriteGeometry(XmlText xml, ResourceData res, int o)
    {
        xml.Vector("GeometryCenter", Vec3(res, o + 0xA0));
        xml.Value("UnkFloat1", Float(res, o + 0x9C));
        xml.Value("UnkFloat2", Float(res, o + 0xAC));

        int materialCount = res.Byte(o + 0x120);
        if (materialCount > 0 && res.UInt64(o + 0xF0) != 0)
        {
            int materials = res.ResolveArray(res.UInt64(o + 0xF0), materialCount, 8, "matériaux");
            xml.Open("Materials");
            for (int i = 0; i < materialCount; i++)
            {
                uint d1 = res.UInt32(materials + i * 8), d2 = res.UInt32(materials + i * 8 + 4);
                xml.Open("Item");
                xml.Value("Type", d1 & 0xFF);
                xml.Value("ProceduralID", d1 >> 8 & 0xFF);
                xml.Value("RoomID", d1 >> 16 & 0x1F);
                xml.Value("PedDensity", d1 >> 21 & 0x7);
                xml.Text("Flags", FlagNames(d1 >> 24 | (d2 & 0xFF) << 8, MaterialFlagNames));
                xml.Value("MaterialColourIndex", d2 >> 8 & 0xFF);
                xml.Value("Unk", d2 >> 16);
                xml.Close("Item");
            }
            xml.Close("Materials");
        }

        int colourCount = res.Byte(o + 0x121);
        if (colourCount > 0 && res.UInt64(o + 0xF8) != 0)
        {
            int colours = res.ResolveArray(res.UInt64(o + 0xF8), colourCount, 4, "couleurs des matériaux");
            var rows = new List<string>();
            for (int i = 0; i < colourCount; i++)
                rows.Add(Colour(res.UInt32(colours + i * 4)));
            WriteRows(xml, "MaterialColours", rows);
        }

        int vertexCount = (int)res.UInt32(o + 0xD0);
        var quantum = Vec3(res, o + 0x90);
        if (vertexCount > 0 && res.UInt64(o + 0xB0) != 0)
        {
            int vertices = res.ResolveArray(res.UInt64(o + 0xB0), vertexCount, 6, "sommets");
            var rows = new List<string>();
            for (int i = 0; i < vertexCount; i++)
            {
                int v = vertices + i * 6;
                var p = new Vector3(res.Int16(v), res.Int16(v + 2), res.Int16(v + 4)) * quantum;
                rows.Add($"{XmlText.Number(p.X)}, {XmlText.Number(p.Y)}, {XmlText.Number(p.Z)}");
            }
            WriteRows(xml, "Vertices", rows);
            if (res.UInt64(o + 0xB8) != 0)
            {
                int colours = res.ResolveArray(res.UInt64(o + 0xB8), vertexCount, 4, "couleurs des sommets");
                var colourRows = new List<string>();
                for (int i = 0; i < vertexCount; i++)
                    colourRows.Add(Colour(res.UInt32(colours + i * 4)));
                WriteRows(xml, "VertexColours", colourRows);
            }
        }

        int polygonCount = (int)res.UInt32(o + 0xD4);
        if (polygonCount > 0 && res.UInt64(o + 0x88) != 0)
        {
            int polygons = res.ResolveArray(res.UInt64(o + 0x88), polygonCount, 16, "polygones");
            int polygonMaterials = res.UInt64(o + 0x118) == 0 ? -1 : res.ResolveArray(res.UInt64(o + 0x118), polygonCount, 1, "matériaux des polygones");
            xml.Open("Polygons");
            for (int i = 0; i < polygonCount; i++)
            {
                int p = polygons + i * 16;
                int m = polygonMaterials >= 0 ? res.Byte(polygonMaterials + i) : 0;
                switch (res.Byte(p) & 7)
                {
                    case XmlPolygon.Triangle:
                    {
                        int v1 = res.UInt16(p + 4), v2 = res.UInt16(p + 6), v3 = res.UInt16(p + 8);
                        xml.Empty("Triangle", $"m=\"{m}\" v1=\"{v1 & 0x7FFF}\" v2=\"{v2 & 0x7FFF}\" v3=\"{v3 & 0x7FFF}\" " +
                            $"f1=\"{v1 >> 15}\" f2=\"{v2 >> 15}\" f3=\"{v3 >> 15}\"");
                        break;
                    }
                    case XmlPolygon.Sphere:
                        xml.Empty("Sphere", $"m=\"{m}\" v=\"{res.UInt16(p + 2)}\" radius=\"{XmlText.Number(Float(res, p + 4))}\"");
                        break;
                    case XmlPolygon.Capsule:
                    case XmlPolygon.Cylinder:
                        xml.Empty((res.Byte(p) & 7) == XmlPolygon.Capsule ? "Capsule" : "Cylinder",
                            $"m=\"{m}\" v1=\"{res.UInt16(p + 2)}\" v2=\"{res.UInt16(p + 8)}\" radius=\"{XmlText.Number(Float(res, p + 4))}\"");
                        break;
                    case XmlPolygon.Box:
                        xml.Empty("Box", $"m=\"{m}\" v1=\"{res.Int16(p + 4)}\" v2=\"{res.Int16(p + 6)}\" v3=\"{res.Int16(p + 8)}\" v4=\"{res.Int16(p + 10)}\"");
                        break;
                }
            }
            xml.Close("Polygons");
        }
    }

    // One value per line, as CodeWalker writes the lists of vertices and colours.
    private static void WriteRows(XmlText xml, string name, List<string> rows)
    {
        if (rows.Count == 1)
            xml.Text(name, rows[0]);
        else
            xml.Lines(name, null, rows);
    }

    private static string Colour(uint c) => $"{c & 0xFF}, {c >> 8 & 0xFF}, {c >> 16 & 0xFF}, {c >> 24}";

    private static string Row(float a, float b, float c, float d) =>
        $"{XmlText.Number(a)} {XmlText.Number(b)} {XmlText.Number(c)} {XmlText.Number(d)}";

    private static string FlagNames(uint flags, string[] names)
    {
        if (flags == 0)
            return "NONE";
        var list = new List<string>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((flags & 1u << bit) != 0)
                list.Add(bit < names.Length ? names[bit] : (1u << bit).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return string.Join(", ", list);
    }

    private static float Float(ResourceData res, int offset) => BitConverter.UInt32BitsToSingle(res.UInt32(offset));

    private static Vector3 Vec3(ResourceData res, int offset) =>
        new(Float(res, offset), Float(res, offset + 4), Float(res, offset + 8));
}
