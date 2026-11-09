using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Textures;

namespace NodeRunner.Data.Native.Models;

// Drawables in the XML form exported by Sollumz (and CodeWalker): <Drawable> with its shaders, embedded textures
// (.dds in the folder named after the file), models of each level of detail, lights and collision.
public static class YdrXml
{
    private static readonly string[] Semantics =
    [
        "Position", "BlendWeights", "BlendIndices", "Normal", "Colour0", "Colour1", "TexCoord0", "TexCoord1", "TexCoord2",
        "TexCoord3", "TexCoord4", "TexCoord5", "TexCoord6", "TexCoord7", "Tangent", "Binormal",
    ];

    // Storage type of each component (4 bits each), by layout name.
    private static readonly Dictionary<string, ulong> Layouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GTAV1"] = 0x7755555555996996,
        ["GTAV2"] = 0x030000000199A006,
        ["GTAV3"] = 0x0300000001996006,
        ["GTAV4"] = 0x7655555555996996,
    };

    private static readonly string[] LodNames = ["DrawableModelsHigh", "DrawableModelsMedium", "DrawableModelsLow", "DrawableModelsVeryLow"];

    private static readonly string[] LightTypes = ["", "Point", "Spot", "", "Capsule"];

    // A complete .ydr file (RSC7).
    public static byte[] Convert(XmlExport export)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(export.Text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}");
        }
        var root = document.Root ?? throw new InvalidDataException("XML vide.");
        if (root.Name.LocalName != "Drawable")
            throw new InvalidDataException("Balise <Drawable> introuvable : ce n'est pas un modèle .ydr.");
        return YdrBuilder.Build(Read(root, export));
    }

    internal static XmlDrawable Read(XElement e, XmlExport export)
    {
        if (e.Element("Joints")?.Elements().Any(j => j.Elements("Item").Any()) == true)
            throw new InvalidDataException("Modèle avec limites d'articulation (Joints) : pas encore pris en charge.");

        var d = new XmlDrawable
        {
            Name = e.Element("Name")?.Value.Trim() is { Length: > 0 } name ? name : export.BaseName,
            SphereCenter = Vec3(e, "BoundingSphereCenter"),
            SphereRadius = Float(e, "BoundingSphereRadius", 0),
            BoxMin = Vec3(e, "BoundingBoxMin"),
            BoxMax = Vec3(e, "BoundingBoxMax"),
        };
        string[] levels = ["High", "Med", "Low", "Vlow"];
        for (int i = 0; i < 4; i++)
        {
            d.LodDistances[i] = Float(e, "LodDist" + levels[i], 9998);
            d.LodFlags[i] = (byte)Int(e, "Flags" + levels[i], 0);
        }

        if (e.Element("ShaderGroup") is { } group)
        {
            if (group.Element("TextureDictionary") is { } dictionary)
                d.Textures = YtdXml.ReadTextures(dictionary, export);
            foreach (var item in group.Element("Shaders")?.Elements("Item") ?? [])
                d.Shaders.Add(ReadShader(item));
        }
        if (d.Shaders.Count > ushort.MaxValue)
            throw new InvalidDataException("Trop de shaders.");

        if (e.Element("Skeleton") is { } skeleton && skeleton.Element("Bones")?.Elements("Item").Any() == true)
            d.Skeleton = ReadSkeleton(skeleton);

        for (int level = 0; level < 4; level++)
        {
            foreach (var item in e.Element(LodNames[level])?.Elements("Item") ?? [])
            {
                var model = ReadModel(item, d.Shaders.Count);
                if (model.BoneIndex != 0 && model.BoneIndex >= (d.Skeleton?.Bones.Count ?? 0))
                    throw new InvalidDataException($"Modèle rattaché à l'os {model.BoneIndex}, absent du squelette.");
                d.Models[level].Add(model);
            }
        }
        if (d.Models.All(level => level.All(m => m.Geometries.Count == 0)))
            throw new InvalidDataException("Le modèle ne contient aucune géométrie (DrawableModelsHigh vide).");

        foreach (var item in e.Element("Lights")?.Elements("Item") ?? [])
            d.Lights.Add(ReadLight(item));

        if (e.Element("Bounds") is { } bounds)
            d.Bound = YbnXml.ReadBound(bounds, isChild: false);
        return d;
    }

    // ---- Shaders ----

    private static XmlShader ReadShader(XElement e)
    {
        var shader = new XmlShader
        {
            Name = Hash(e.Element("Name")?.Value),
            FileName = Hash(e.Element("FileName")?.Value),
            RenderBucket = (byte)Int(e, "RenderBucket", 0),
        };
        if (shader.RenderBucket > 15)
            throw new InvalidDataException($"Shader : RenderBucket {shader.RenderBucket} invalide.");
        foreach (var item in e.Element("Parameters")?.Elements("Item") ?? [])
        {
            var parameter = new XmlShaderParameter { Name = Hash(((string?)item.Attribute("name"))?.ToLowerInvariant()) };
            switch ((string?)item.Attribute("type"))
            {
                case "Texture":
                    parameter.Texture = item.Element("Name")?.Value.Trim() is { Length: > 0 } texture ? texture : null;
                    break;
                case "Vector":
                    parameter.Values = [Vec4(item)];
                    break;
                case "Array":
                    parameter.Values = [.. item.Elements("Value").Select(Vec4)];
                    if (parameter.Values.Length is 0 or > 255)
                        throw new InvalidDataException("Paramètre de shader : tableau vide ou trop grand.");
                    break;
                default:
                    throw new InvalidDataException($"Paramètre de shader : type « {(string?)item.Attribute("type")} » inconnu.");
            }
            shader.Parameters.Add(parameter);
        }
        if (shader.Parameters.Count > 255)
            throw new InvalidDataException("Shader avec plus de 255 paramètres.");
        return shader;
    }

    // ---- Models ----

    private static XmlModel ReadModel(XElement e, int shaderCount)
    {
        var model = new XmlModel
        {
            RenderMask = (byte)Int(e, "RenderMask", 255),
            Flags = (byte)Int(e, "Flags", 0),
            BoneIndex = (byte)Int(e, "BoneIndex", 0),
            Unknown1 = (byte)Int(e, "Unknown1", 0),
        };
        if (Int(e, "HasSkin", 0) != 0)
            throw new InvalidDataException("Modèle déformé par un squelette (HasSkin) : pas encore pris en charge.");
        foreach (var item in e.Element("Geometries")?.Elements("Item") ?? [])
        {
            var geometry = ReadGeometry(item);
            if (geometry.Shader >= shaderCount)
                throw new InvalidDataException($"Géométrie avec un shader inexistant (ShaderIndex {geometry.Shader}).");
            model.Geometries.Add(geometry);
        }
        if (model.Geometries.Count > 0x7FFF)
            throw new InvalidDataException("Trop de géométries dans un modèle.");
        return model;
    }

    private static XmlGeometry ReadGeometry(XElement e)
    {
        var buffer = e.Element("VertexBuffer") ?? throw new InvalidDataException("Géométrie sans <VertexBuffer>.");
        var layout = buffer.Element("Layout") ?? throw new InvalidDataException("Géométrie sans <Layout>.");
        string layoutName = (string?)layout.Attribute("type") ?? "GTAV1";
        if (!Layouts.TryGetValue(layoutName, out ulong types))
            throw new InvalidDataException($"Format de sommets « {layoutName} » inconnu.");
        uint flags = 0;
        foreach (var component in layout.Elements())
        {
            int index = Array.IndexOf(Semantics, component.Name.LocalName);
            if (index < 0)
                throw new InvalidDataException($"Composante de sommet « {component.Name.LocalName} » inconnue.");
            flags |= 1u << index;
        }
        if ((flags & 1) == 0)
            throw new InvalidDataException("Sommets sans position.");

        var g = new XmlGeometry
        {
            Shader = (ushort)Int(e, "ShaderIndex", 0),
            BoxMin = Vec4(e.Element("BoundingBoxMin")),
            BoxMax = Vec4(e.Element("BoundingBoxMax")),
            VertexFlags = (ushort)Int(buffer, "Flags", 0),
            Declaration = new VertexLayout(flags, types),
        };
        string data = (buffer.Element("Data") ?? buffer.Element("Data2"))?.Value ?? "";
        (g.Vertices, g.VertexCount) = ReadVertices(data, g.Declaration);
        if (g.VertexCount == 0)
            throw new InvalidDataException("Géométrie sans sommet.");
        if (g.VertexCount > ushort.MaxValue)
            throw new InvalidDataException($"Géométrie de {g.VertexCount} sommets : 65 535 au plus (la découper dans Blender).");

        var indices = new List<ushort>();
        foreach (var token in Tokens(e.Element("IndexBuffer")?.Element("Data")?.Value ?? ""))
        {
            if (!ushort.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort index) || index >= g.VertexCount)
                throw new InvalidDataException($"Indice de sommet invalide : « {token} ».");
            indices.Add(index);
        }
        if (indices.Count == 0 || indices.Count % 3 != 0)
            throw new InvalidDataException("Liste de triangles vide ou incomplète.");
        g.Indices = [.. indices];
        return g;
    }

    // One row of text per vertex, the values of each component present in order.
    private static (byte[] Bytes, int Count) ReadVertices(string text, VertexLayout layout)
    {
        var rows = text.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        int stride = layout.Stride;
        var bytes = new byte[rows.Count * stride];
        var values = new List<string>();
        for (int v = 0; v < rows.Count; v++)
        {
            values.Clear();
            values.AddRange(Tokens(rows[v]));
            int at = 0;
            var vertex = bytes.AsSpan(v * stride, stride);
            int offset = 0;
            for (int c = 0; c < 16; c++)
            {
                if (!layout.Has(c))
                    continue;
                int type = layout.Type(c);
                int needed = VertexLayout.Values(type);
                if (at + needed > values.Count)
                    throw new InvalidDataException($"Sommet {v + 1} : il manque des valeurs.");
                WriteComponent(vertex[offset..], type, values, at);
                at += needed;
                offset += VertexLayout.Size(type);
            }
            if (at != values.Count)
                throw new InvalidDataException($"Sommet {v + 1} : trop de valeurs pour le format des sommets.");
        }
        return (bytes, rows.Count);
    }

    private static void WriteComponent(Span<byte> o, int type, List<string> values, int at)
    {
        float F(int i) => ParseFloat(values[at + i]);
        byte B(int i) => byte.TryParse(values[at + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b)
            ? b : throw new InvalidDataException($"Valeur de 0 à 255 attendue : « {values[at + i]} ».");
        switch (type)
        {
            case 1:
            case 3:
                for (int i = 0; i < (type == 1 ? 2 : 4); i++)
                    BinaryPrimitives.WriteHalfLittleEndian(o[(i * 2)..], (Half)F(i));
                break;
            case 2:
            case 5:
            case 6:
            case 7:
                for (int i = 0; i < VertexLayout.Values(type); i++)
                    BinaryPrimitives.WriteSingleLittleEndian(o[(i * 4)..], F(i));
                break;
            case 8:
            case 9:
                for (int i = 0; i < 4; i++)
                    o[i] = B(i);
                break;
            case 10:
                for (int i = 0; i < 4; i++)
                    o[i] = (byte)(sbyte)Math.Round(Math.Clamp(F(i), -1f, 1f) * 127f);
                break;
        }
    }

    // ---- Skeleton ----

    private static readonly string[] BoneFlagNames =
    [
        "RotX", "RotY", "RotZ", "LimitRotation", "TransX", "TransY", "TransZ", "LimitTranslation", "ScaleX", "ScaleY",
        "ScaleZ", "LimitScale", "Unk0", "Unk1", "Unk2", "Unk3",
    ];

    private static XmlSkeleton ReadSkeleton(XElement e)
    {
        var skeleton = new XmlSkeleton
        {
            Unknown1C = (uint)Int(e, "Unknown1C", 0),
            Unknown50 = (uint)Int(e, "Unknown50", 0),
            Unknown54 = (uint)Int(e, "Unknown54", 0),
            Unknown58 = (uint)Int(e, "Unknown58", 0),
        };
        foreach (var item in e.Element("Bones")!.Elements("Item"))
        {
            ushort flags = 0;
            foreach (var part in (item.Element("Flags")?.Value ?? "").Split([',', ' ', '|'], StringSplitOptions.RemoveEmptyEntries))
            {
                int bit = Array.IndexOf(BoneFlagNames, part);
                if (bit >= 0)
                    flags |= (ushort)(1 << bit);
                else if (part != "None")
                    flags |= (ushort)ParseInt(part);
            }
            var r = Vec4(item.Element("Rotation"));
            skeleton.Bones.Add(new XmlBone
            {
                Name = item.Element("Name")?.Value.Trim() ?? "",
                Tag = (ushort)Int(item, "Tag", 0),
                Index = (short)Int(item, "Index", skeleton.Bones.Count),
                Parent = (short)Int(item, "ParentIndex", -1),
                Sibling = (short)Int(item, "SiblingIndex", -1),
                Flags = flags,
                Translation = Vec3(item, "Translation"),
                Rotation = new Quaternion(r.X, r.Y, r.Z, r.W),
                Scale = item.Element("Scale") is null ? Vector3.One : Vec3(item, "Scale"),
                TransformUnknown = Vec4(item.Element("TransformUnk")),
            });
        }
        int count = skeleton.Bones.Count;
        if (count > short.MaxValue)
            throw new InvalidDataException("Squelette trop grand.");
        foreach (var bone in skeleton.Bones)
        {
            if (bone.Parent >= count || bone.Parent < -1)
                throw new InvalidDataException($"Os {bone.Name} : parent {bone.Parent} inexistant.");
        }
        return skeleton;
    }

    // ---- Lights ----

    // The 168-byte light record of the game.
    private static byte[] ReadLight(XElement e)
    {
        var o = new byte[0xA8];
        void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(o.AsSpan(at), v);
        void V(int at, Vector3 v) { F(at, v.X); F(at + 4, v.Y); F(at + 8, v.Z); }
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(at), v);
        void Rgb(int at, string name)
        {
            var c = e.Element(name);
            o[at] = (byte)AttrInt(c, "r");
            o[at + 1] = (byte)AttrInt(c, "g");
            o[at + 2] = (byte)AttrInt(c, "b");
        }

        string type = e.Element("Type")?.Value.Trim() ?? "Point";
        int kind = Array.IndexOf(LightTypes, type);
        if (kind <= 0 && !int.TryParse(type, out kind))
            throw new InvalidDataException($"Type de lumière « {type} » inconnu.");

        V(0x08, Vec3(e, "Position"));
        Rgb(0x18, "Colour");
        o[0x1B] = (byte)Int(e, "Flashiness", 0);
        F(0x1C, Float(e, "Intensity", 0));
        U32(0x20, (uint)Int(e, "Flags", 0));
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(0x24), (ushort)Int(e, "BoneId", 0));
        o[0x26] = (byte)kind;
        o[0x27] = (byte)Int(e, "GroupId", 0);
        U32(0x28, (uint)Int(e, "TimeFlags", 0));
        F(0x2C, Float(e, "Falloff", 0));
        F(0x30, Float(e, "FalloffExponent", 0));
        V(0x34, Vec3(e, "CullingPlaneNormal"));
        F(0x40, Float(e, "CullingPlaneOffset", 0));
        o[0x44] = (byte)Int(e, "ShadowBlur", 0);
        o[0x45] = (byte)Int(e, "Unknown45", 0);
        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(0x46), (ushort)Int(e, "Unknown46", 0));
        F(0x4C, Float(e, "VolumeIntensity", 0));
        F(0x50, Float(e, "VolumeSizeScale", 0));
        Rgb(0x54, "VolumeOuterColour");
        o[0x57] = (byte)Int(e, "LightHash", 0);
        F(0x58, Float(e, "VolumeOuterIntensity", 0));
        F(0x5C, Float(e, "CoronaSize", 0));
        F(0x60, Float(e, "VolumeOuterExponent", 0));
        o[0x64] = (byte)Int(e, "LightFadeDistance", 0);
        o[0x65] = (byte)Int(e, "ShadowFadeDistance", 0);
        o[0x66] = (byte)Int(e, "SpecularFadeDistance", 0);
        o[0x67] = (byte)Int(e, "VolumetricFadeDistance", 0);
        F(0x68, Float(e, "ShadowNearClip", 0));
        F(0x6C, Float(e, "CoronaIntensity", 0));
        F(0x70, Float(e, "CoronaZBias", 0));
        V(0x74, Vec3(e, "Direction"));
        V(0x80, Vec3(e, "Tangent"));
        F(0x8C, Float(e, "ConeInnerAngle", 0));
        F(0x90, Float(e, "ConeOuterAngle", 0));
        V(0x94, Vec3(e, "Extent"));
        U32(0xA0, Hash(e.Element("ProjectedTextureHash")?.Value));
        return o;
    }

    // ---- Values ----

    // A name as the game stores it: "hash_1234ABCD" or a plain number (unknown names, as CodeWalker writes them) or the
    // text's hash; 0 when empty.
    internal static uint Hash(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;
        if (char.IsAsciiDigit(text[0]) && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
            return number;
        if (text.StartsWith("hash_", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
            return hash;
        return Joaat.Hash(text);
    }

    private static IEnumerable<string> Tokens(string text) =>
        text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static float ParseFloat(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : throw new InvalidDataException($"Nombre invalide : « {text} ».");

    private static float Float(XElement e, string name, float fallback) =>
        e.Element(name)?.Attribute("value") is { } a ? ParseFloat(a.Value) : fallback;

    private static long Int(XElement e, string name, long fallback) =>
        e.Element(name)?.Attribute("value") is { } a ? ParseInt(a.Value) : fallback;

    private static long AttrInt(XElement? e, string name) => e?.Attribute(name) is { } a ? ParseInt(a.Value) : 0;

    private static long ParseInt(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v
        : float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? (long)f
        : throw new InvalidDataException($"Nombre entier invalide : « {text} ».");

    private static float AttrFloat(XElement? e, string name) => e?.Attribute(name) is { } a ? ParseFloat(a.Value) : 0;

    private static Vector3 Vec3(XElement e, string name)
    {
        var c = e.Element(name);
        return new Vector3(AttrFloat(c, "x"), AttrFloat(c, "y"), AttrFloat(c, "z"));
    }

    private static Vector4 Vec4(XElement? e) => new(AttrFloat(e, "x"), AttrFloat(e, "y"), AttrFloat(e, "z"), AttrFloat(e, "w"));
}

// Vertex layout: one flag bit per component present, 4 bits of storage type per component.
internal readonly record struct VertexLayout(uint Flags, ulong Types)
{
    public bool Has(int component) => ((Flags >> component) & 1) != 0;

    public int Type(int component) => (int)((Types >> (component * 4)) & 0xF);

    public int Count => System.Numerics.BitOperations.PopCount(Flags);

    public int Stride
    {
        get
        {
            int stride = 0;
            for (int c = 0; c < 16; c++)
            {
                if (Has(c))
                    stride += Size(Type(c));
            }
            return stride;
        }
    }

    // Bytes of each storage type: half2, float, half4, (unused), float2, float3, float4, ubyte4, colour, rgba8 snorm.
    public static int Size(int type) => type switch
    {
        1 or 2 or 8 or 9 or 10 => 4,
        3 or 5 => 8,
        6 => 12,
        7 => 16,
        _ => throw new InvalidDataException($"Type de composante de sommet {type} non pris en charge."),
    };

    // Numbers written in the XML for each storage type.
    public static int Values(int type) => type switch
    {
        2 => 1,
        1 or 5 => 2,
        6 => 3,
        _ => 4,
    };
}

internal sealed class XmlDrawable
{
    public string Name = "";
    public Vector3 SphereCenter, BoxMin, BoxMax;
    public float SphereRadius;
    public readonly float[] LodDistances = new float[4];
    public readonly byte[] LodFlags = new byte[4];
    public List<XmlTexture> Textures = [];
    public readonly List<XmlShader> Shaders = [];
    public readonly List<XmlModel>[] Models = [[], [], [], []];
    public readonly List<byte[]> Lights = [];
    public XmlBound? Bound;
    public XmlSkeleton? Skeleton;
}

internal sealed class XmlSkeleton
{
    public uint Unknown1C, Unknown50, Unknown54, Unknown58;
    public readonly List<XmlBone> Bones = [];
}

internal sealed class XmlBone
{
    public string Name = "";
    public ushort Tag, Flags;
    public short Index, Parent, Sibling;
    public Vector3 Translation, Scale;
    public Quaternion Rotation;
    public Vector4 TransformUnknown;
}

internal sealed class XmlShader
{
    public uint Name, FileName;
    public byte RenderBucket;
    public readonly List<XmlShaderParameter> Parameters = [];
}

internal sealed class XmlShaderParameter
{
    public uint Name;
    public string? Texture;
    // Empty for a texture parameter.
    public Vector4[] Values = [];
}

internal sealed class XmlModel
{
    public byte RenderMask, Flags, BoneIndex, Unknown1;
    public readonly List<XmlGeometry> Geometries = [];
}

internal sealed class XmlGeometry
{
    public ushort Shader;
    public Vector4 BoxMin, BoxMax;
    public ushort VertexFlags;
    public VertexLayout Declaration;
    public byte[] Vertices = [];
    public int VertexCount;
    public ushort[] Indices = [];
}
