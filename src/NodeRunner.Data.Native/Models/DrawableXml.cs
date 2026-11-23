using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;

namespace NodeRunner.Data.Native.Models;

// Writes a drawable of the game (original release layout) as the XML that YdrXml reads back, and that Sollumz and
// CodeWalker read: shaders, embedded textures (.dds next to the XML), skeleton, models, lights and collision.
internal static class DrawableXml
{
    private const int ShaderGroupLength = 0x40;
    private const int ShaderLength = 0x30;
    private const int ModelLength = 0x30;
    private const int GeometryLength = 0x98;
    private const int VertexBufferLength = 0x80;
    private const int IndexBufferLength = 0x60;
    private const int SkeletonLength = 0x70;
    private const int BoneLength = 0x50;
    private const int LightLength = 0xA8;
    private const int TextureNamePointer = 0x28;

    private static readonly string[] Semantics =
    [
        "Position", "BlendWeights", "BlendIndices", "Normal", "Colour0", "Colour1", "TexCoord0", "TexCoord1", "TexCoord2",
        "TexCoord3", "TexCoord4", "TexCoord5", "TexCoord6", "TexCoord7", "Tangent", "Binormal",
    ];

    private static readonly string[] LodNames = ["DrawableModelsHigh", "DrawableModelsMedium", "DrawableModelsLow", "DrawableModelsVeryLow"];
    private static readonly string[] LodSuffixes = ["High", "Med", "Low", "Vlow"];

    private static readonly string[] BoneFlagNames =
    [
        "RotX", "RotY", "RotZ", "LimitRotation", "TransX", "TransY", "TransZ", "LimitTranslation", "ScaleX", "ScaleY",
        "ScaleZ", "LimitScale", "Unk0", "Unk1", "Unk2", "Unk3",
    ];

    // Where the drawable's images go and what was written there.
    internal sealed class Output(ShaderNameCatalog names, string folder)
    {
        public ShaderNameCatalog Names { get; } = names;
        public string Folder { get; } = folder;
        public List<(string, byte[])> Files { get; } = [];
    }

    // The fields of the drawable at offset (its name when it has one), inside an element already opened.
    internal static void WriteDrawable(XmlText xml, ResourceData res, int d, Output output, bool withName)
    {
        if (withName)
        {
            ulong namePointer = res.UInt64(d + 0xA8);
            xml.Text("Name", namePointer == 0 ? null : res.Text(namePointer, "nom du modèle"));
        }
        xml.Vector("BoundingSphereCenter", Vec3(res, d + 0x20));
        xml.Value("BoundingSphereRadius", Float(res, d + 0x2C));
        xml.Vector("BoundingBoxMin", Vec3(res, d + 0x30));
        xml.Vector("BoundingBoxMax", Vec3(res, d + 0x40));
        for (int level = 0; level < 4; level++)
            xml.Value("LodDist" + LodSuffixes[level], Float(res, d + 0x70 + level * 4));
        for (int level = 0; level < 4; level++)
            xml.Value("Flags" + LodSuffixes[level], res.Byte(d + 0x80 + level * 4));

        if (res.UInt64(d + 0x10) is not 0 and var group)
            WriteShaderGroup(xml, res, res.ResolveArray(group, 1, ShaderGroupLength, "groupe de shaders"), output);
        if (res.UInt64(d + 0x18) is not 0 and var skeleton)
            WriteSkeleton(xml, res, res.ResolveArray(skeleton, 1, SkeletonLength, "squelette"));

        for (int level = 0; level < 4; level++)
        {
            if (res.UInt64(d + 0x50 + level * 8) is not 0 and var list)
                WriteModels(xml, res, res.ResolveArray(list, 1, 16, "liste des modèles"), LodNames[level], output);
        }
    }

    // Collision and lights, written after the models in a .ydr.
    internal static void WriteBoundsAndLights(XmlText xml, ResourceData res, int d)
    {
        if (res.UInt64(d + 0xC8) is not 0 and var bounds)
            YbnXml.WriteBound(xml, res, res.ResolveArray(bounds, 1, 0x70, "collision"), "Bounds", child: null);
        int lightCount = res.UInt16(d + 0xB8);
        if (lightCount > 0 && res.UInt64(d + 0xB0) is not 0 and var lights)
            WriteLights(xml, res, res.ResolveArray(lights, lightCount, LightLength, "lumières"), lightCount);
        else
            xml.Empty("Lights");
    }

    // ---- Shaders ----

    private static void WriteShaderGroup(XmlText xml, ResourceData res, int g, Output output)
    {
        xml.Open("ShaderGroup");
        if (res.UInt64(g + 0x08) is not 0 and var dictionary)
        {
            var textures = YtdReader.ReadWithSettings(res, res.ResolveArray(dictionary, 1, 0x40, "textures intégrées"));
            if (textures.Count == 0)
                xml.Empty("TextureDictionary");
            else
            {
                xml.Open("TextureDictionary");
                YtdXml.WriteTextures(xml, textures, output.Folder, output.Files);
                xml.Close("TextureDictionary");
            }
        }
        int count = res.UInt16(g + 0x18);
        if (count == 0 || res.UInt64(g + 0x10) == 0)
            xml.Empty("Shaders");
        else
        {
            int list = res.ResolveArray(res.UInt64(g + 0x10), count, 8, "liste des shaders");
            xml.Open("Shaders");
            for (int i = 0; i < count; i++)
            {
                if (res.UInt64(list + i * 8) is 0)
                {
                    xml.Empty("Item");
                    continue;
                }
                xml.Open("Item");
                WriteShader(xml, res, res.ResolveArray(res.UInt64(list + i * 8), 1, ShaderLength, "shader"), output.Names);
                xml.Close("Item");
            }
            xml.Close("Shaders");
        }
        xml.Close("ShaderGroup");
    }

    private static void WriteShader(XmlText xml, ResourceData res, int s, ShaderNameCatalog names)
    {
        xml.Text("Name", HashText(res.UInt32(s + 0x08), names));
        xml.Text("FileName", HashText(res.UInt32(s + 0x18), names));
        xml.Value("RenderBucket", res.Byte(s + 0x11));
        if (res.UInt64(s) is 0)
            return;
        int count = res.Byte(s + 0x10);
        int parameterSize = res.UInt16(s + 0x14);
        int block = res.ResolveArray(res.UInt64(s), Math.Max(1, parameterSize + 4 * count), 1, "paramètres du shader");
        xml.Open("Parameters");
        for (int i = 0; i < count; i++)
        {
            int p = block + 16 * i;
            int type = res.Byte(p);
            uint nameHash = res.UInt32(block + parameterSize + 4 * i);
            string name = names[nameHash] ?? nameHash.ToString(CultureInfo.InvariantCulture);
            ulong pointer = res.UInt64(p + 8);
            string head = $"name=\"{XmlText.Escape(name)}\" type=\"{(type == 0 ? "Texture" : type == 1 ? "Vector" : "Array")}\"";
            if (type == 0)
            {
                string? texture = pointer == 0 ? null
                    : res.Text(res.UInt64(res.ResolveArray(pointer, 1, 0x30, "texture du shader") + TextureNamePointer), "nom de texture");
                if (texture is null)
                    xml.Empty("Item", head);
                else
                {
                    xml.Open("Item", head);
                    xml.Text("Name", texture);
                    xml.Close("Item");
                }
            }
            else if (pointer == 0)
            {
                xml.Empty("Item", head);
            }
            else
            {
                int values = res.ResolveArray(pointer, type, 16, "valeurs du shader");
                if (type == 1)
                    xml.Empty("Item", head + " " + Vector4Attributes(Vec4(res, values)));
                else
                {
                    xml.Open("Item", head);
                    for (int v = 0; v < type; v++)
                        xml.Empty("Value", Vector4Attributes(Vec4(res, values + v * 16)));
                    xml.Close("Item");
                }
            }
        }
        xml.Close("Parameters");
    }

    // A name the catalogue knows, else "hash_XXXXXXXX" as CodeWalker writes unknown names; empty for 0.
    private static string HashText(uint hash, ShaderNameCatalog names) =>
        hash == 0 ? "" : names[hash] ?? $"hash_{hash:X8}";

    // ---- Skeleton ----

    private static void WriteSkeleton(XmlText xml, ResourceData res, int k)
    {
        xml.Open("Skeleton");
        xml.Value("Unknown1C", res.UInt32(k + 0x1C));
        xml.Value("Unknown50", res.UInt32(k + 0x50));
        xml.Value("Unknown54", res.UInt32(k + 0x54));
        xml.Value("Unknown58", res.UInt32(k + 0x58));
        int count = res.UInt16(k + 0x5E);
        if (count > 0 && res.UInt64(k + 0x20) is not 0 and var bonesPointer)
        {
            int bones = res.ResolveArray(bonesPointer, count, BoneLength, "os");
            int local = res.UInt64(k + 0x30) is not 0 and var l ? res.ResolveArray(l, count, 64, "positions des os") : -1;
            xml.Open("Bones");
            for (int i = 0; i < count; i++)
            {
                int b = bones + i * BoneLength;
                xml.Open("Item");
                ulong name = res.UInt64(b + 0x38);
                xml.Text("Name", name == 0 ? null : res.Text(name, "nom d'os"));
                xml.Value("Tag", res.UInt16(b + 0x44));
                xml.Value("Index", res.Int16(b + 0x42));
                xml.Value("ParentIndex", res.Int16(b + 0x32));
                xml.Value("SiblingIndex", res.Int16(b + 0x30));
                xml.Text("Flags", Flags(res.UInt16(b + 0x40), BoneFlagNames, "None"));
                xml.Vector("Translation", Vec3(res, b + 0x10));
                xml.Vector("Rotation", Vec4(res, b));
                xml.Vector("Scale", Vec3(res, b + 0x20));
                // 4th column of the bone's local transform.
                var unknown = local < 0 ? Vector4.Zero : new Vector4(
                    Float(res, local + i * 64 + 12), Float(res, local + i * 64 + 28), Float(res, local + i * 64 + 44), Float(res, local + i * 64 + 60));
                xml.Vector("TransformUnk", unknown);
                xml.Close("Item");
            }
            xml.Close("Bones");
        }
        xml.Close("Skeleton");
    }

    // ---- Models ----

    private static void WriteModels(XmlText xml, ResourceData res, int header, string name, Output output)
    {
        int count = res.UInt16(header + 8);
        if (count == 0 || res.UInt64(header) == 0)
        {
            xml.Empty(name);
            return;
        }
        int list = res.ResolveArray(res.UInt64(header), count, 8, "modèles");
        xml.Open(name);
        for (int i = 0; i < count; i++)
        {
            if (res.UInt64(list + i * 8) is 0)
            {
                xml.Empty("Item");
                continue;
            }
            xml.Open("Item");
            WriteModel(xml, res, res.ResolveArray(res.UInt64(list + i * 8), 1, ModelLength, "modèle"));
            xml.Close("Item");
        }
        xml.Close(name);
    }

    private static void WriteModel(XmlText xml, ResourceData res, int m)
    {
        uint binding = res.UInt32(m + 0x28);
        ushort mask = res.UInt16(m + 0x2C);
        xml.Value("RenderMask", mask & 0xFF);
        xml.Value("Flags", mask >> 8);
        xml.Value("HasSkin", binding >> 8 & 0xFF);
        xml.Value("BoneIndex", binding >> 24);
        xml.Value("Unknown1", binding & 0xFF);

        int count = res.UInt16(m + 0x10);
        if (count == 0 || res.UInt64(m + 0x08) == 0)
        {
            xml.Empty("Geometries");
            return;
        }
        int pointers = res.ResolveArray(res.UInt64(m + 0x08), count, 8, "géométries");
        int shaders = res.ResolveArray(res.UInt64(m + 0x20), count, 2, "shaders des géométries");
        // One box per geometry, after a box around all of them when there are several.
        int boxes = res.UInt64(m + 0x18) is not 0 and var b ? res.ResolveArray(b, count + (count > 1 ? 1 : 0), 32, "boîtes") + (count > 1 ? 32 : 0) : -1;
        xml.Open("Geometries");
        for (int i = 0; i < count; i++)
        {
            xml.Open("Item");
            xml.Value("ShaderIndex", res.UInt16(shaders + 2 * i));
            xml.Vector("BoundingBoxMin", boxes < 0 ? Vector4.Zero : Vec4(res, boxes + 32 * i));
            xml.Vector("BoundingBoxMax", boxes < 0 ? Vector4.Zero : Vec4(res, boxes + 32 * i + 16));
            WriteGeometry(xml, res, res.ResolveArray(res.UInt64(pointers + i * 8), 1, GeometryLength, "géométrie"));
            xml.Close("Item");
        }
        xml.Close("Geometries");
    }

    private static void WriteGeometry(XmlText xml, ResourceData res, int g)
    {
        int boneCount = res.UInt16(g + 0x72);
        if (boneCount > 0 && res.UInt64(g + 0x68) is not 0 and var bonesPointer)
        {
            int bones = res.ResolveArray(bonesPointer, boneCount, 2, "os de la géométrie");
            var ids = new StringBuilder();
            for (int i = 0; i < boneCount; i++)
                ids.Append(i > 0 ? ", " : "").Append(res.UInt16(bones + 2 * i));
            xml.Text("BoneIDs", ids.ToString());
        }

        if (res.UInt64(g + 0x18) is not 0 and var vb)
        {
            int v = res.ResolveArray(vb, 1, VertexBufferLength, "sommets");
            int declaration = res.ResolveArray(res.UInt64(v + 0x30), 1, 16, "format des sommets");
            uint flags = res.UInt32(declaration);
            ulong types = res.UInt64(declaration + 8);
            int stride = res.UInt16(v + 0x08);
            int vertexCount = (int)res.UInt32(v + 0x18);
            xml.Open("VertexBuffer");
            xml.Value("Flags", res.UInt16(v + 0x0A));
            xml.Open("Layout", $"type=\"{LayoutName(types)}\"");
            for (int c = 0; c < 16; c++)
            {
                if ((flags >> c & 1) != 0)
                    xml.Empty(Semantics[c]);
            }
            xml.Close("Layout");
            ulong data1 = res.UInt64(v + 0x10), data2 = res.UInt64(v + 0x20);
            if (data1 != 0)
                WriteVertices(xml, "Data", res, res.ResolveArray(data1, vertexCount, stride, "sommets"), vertexCount, stride, flags, types);
            if (data2 != 0 && data2 != data1)
                WriteVertices(xml, "Data2", res, res.ResolveArray(data2, vertexCount, stride, "sommets"), vertexCount, stride, flags, types);
            xml.Close("VertexBuffer");
        }

        if (res.UInt64(g + 0x38) is not 0 and var ib)
        {
            int i = res.ResolveArray(ib, 1, IndexBufferLength, "triangles");
            int count = (int)res.UInt32(i + 0x08);
            int data = res.ResolveArray(res.UInt64(i + 0x10), count, 2, "triangles");
            xml.Open("IndexBuffer");
            var values = new string[count];
            for (int n = 0; n < count; n++)
                values[n] = res.UInt16(data + 2 * n).ToString(CultureInfo.InvariantCulture);
            if (count == 0)
                xml.Empty("Data");
            else if (count <= 24)
                xml.Text("Data", string.Join(" ", values));
            else
                xml.Lines("Data", null, values.Chunk(24).Select(row => string.Join(" ", row)));
            xml.Close("IndexBuffer");
        }
    }

    private static string LayoutName(ulong types) => types switch
    {
        0x7755555555996996 => "GTAV1",
        0x030000000199A006 => "GTAV2",
        0x0300000001996006 => "GTAV3",
        0x7655555555996996 => "GTAV4",
        _ => types.ToString(CultureInfo.InvariantCulture),
    };

    // One row per vertex: the components present, three spaces apart, their values one space apart.
    private static void WriteVertices(XmlText xml, string name, ResourceData res, int at, int count, int stride, uint flags, ulong types)
    {
        var rows = new string[count];
        var row = new StringBuilder();
        for (int v = 0; v < count; v++)
        {
            row.Clear();
            int o = at + v * stride;
            for (int c = 0; c < 16; c++)
            {
                if ((flags >> c & 1) == 0)
                    continue;
                int type = (int)(types >> (c * 4) & 0xF);
                if (row.Length > 0)
                    row.Append("   ");
                AppendComponent(row, res.Bytes(o, VertexLayout.Size(type)), type);
                o += VertexLayout.Size(type);
            }
            rows[v] = row.ToString();
        }
        xml.Lines(name, null, rows);
    }

    private static void AppendComponent(StringBuilder row, ReadOnlySpan<byte> b, int type)
    {
        switch (type)
        {
            case 1:
            case 3:
                for (int i = 0; i < (type == 1 ? 2 : 4); i++)
                    row.Append(i > 0 ? " " : "").Append(XmlText.Number((float)BinaryPrimitives.ReadHalfLittleEndian(b[(i * 2)..])));
                break;
            case 2:
            case 5:
            case 6:
            case 7:
                for (int i = 0; i < VertexLayout.Values(type); i++)
                    row.Append(i > 0 ? " " : "").Append(XmlText.Number(BinaryPrimitives.ReadSingleLittleEndian(b[(i * 4)..])));
                break;
            case 8:
            case 9:
                for (int i = 0; i < 4; i++)
                    row.Append(i > 0 ? " " : "").Append(b[i]);
                break;
            case 10:
                for (int i = 0; i < 4; i++)
                    row.Append(i > 0 ? " " : "").Append(XmlText.Number((sbyte)b[i] / 127f));
                break;
        }
    }

    // ---- Lights ----

    private static void WriteLights(XmlText xml, ResourceData res, int at, int count)
    {
        xml.Open("Lights");
        for (int i = 0; i < count; i++)
        {
            int o = at + i * LightLength;
            xml.Open("Item");
            xml.Vector("Position", Vec3(res, o + 0x08));
            xml.Empty("Colour", $"r=\"{res.Byte(o + 0x18)}\" g=\"{res.Byte(o + 0x19)}\" b=\"{res.Byte(o + 0x1A)}\"");
            xml.Value("Flashiness", res.Byte(o + 0x1B));
            xml.Value("Intensity", Float(res, o + 0x1C));
            xml.Value("Flags", res.UInt32(o + 0x20));
            xml.Value("BoneId", res.UInt16(o + 0x24));
            byte type = res.Byte(o + 0x26);
            xml.Text("Type", type switch { 1 => "Point", 2 => "Spot", 4 => "Capsule", _ => type.ToString(CultureInfo.InvariantCulture) });
            xml.Value("GroupId", res.Byte(o + 0x27));
            xml.Value("TimeFlags", res.UInt32(o + 0x28));
            xml.Value("Falloff", Float(res, o + 0x2C));
            xml.Value("FalloffExponent", Float(res, o + 0x30));
            xml.Vector("CullingPlaneNormal", Vec3(res, o + 0x34));
            xml.Value("CullingPlaneOffset", Float(res, o + 0x40));
            xml.Value("Unknown45", res.Byte(o + 0x45));
            xml.Value("Unknown46", res.UInt16(o + 0x46));
            xml.Value("VolumeIntensity", Float(res, o + 0x4C));
            xml.Value("VolumeSizeScale", Float(res, o + 0x50));
            xml.Empty("VolumeOuterColour", $"r=\"{res.Byte(o + 0x54)}\" g=\"{res.Byte(o + 0x55)}\" b=\"{res.Byte(o + 0x56)}\"");
            xml.Value("LightHash", res.Byte(o + 0x57));
            xml.Value("VolumeOuterIntensity", Float(res, o + 0x58));
            xml.Value("CoronaSize", Float(res, o + 0x5C));
            xml.Value("VolumeOuterExponent", Float(res, o + 0x60));
            xml.Value("LightFadeDistance", res.Byte(o + 0x64));
            xml.Value("ShadowBlur", res.Byte(o + 0x44));
            xml.Value("ShadowFadeDistance", res.Byte(o + 0x65));
            xml.Value("SpecularFadeDistance", res.Byte(o + 0x66));
            xml.Value("VolumetricFadeDistance", res.Byte(o + 0x67));
            xml.Value("ShadowNearClip", Float(res, o + 0x68));
            xml.Value("CoronaIntensity", Float(res, o + 0x6C));
            xml.Value("CoronaZBias", Float(res, o + 0x70));
            xml.Vector("Direction", Vec3(res, o + 0x74));
            xml.Vector("Tangent", Vec3(res, o + 0x80));
            xml.Value("ConeInnerAngle", Float(res, o + 0x8C));
            xml.Value("ConeOuterAngle", Float(res, o + 0x90));
            xml.Vector("Extent", Vec3(res, o + 0x94));
            uint projected = res.UInt32(o + 0xA0);
            xml.Text("ProjectedTextureHash", projected == 0 ? "" : $"hash_{projected:X8}");
            xml.Close("Item");
        }
        xml.Close("Lights");
    }

    // ---- Values ----

    private static string Flags(uint flags, string[] names, string none)
    {
        if (flags == 0)
            return none;
        var list = new List<string>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((flags & 1u << bit) != 0)
                list.Add(bit < names.Length ? names[bit] : (1u << bit).ToString(CultureInfo.InvariantCulture));
        }
        return string.Join(", ", list);
    }

    private static string Vector4Attributes(Vector4 v) =>
        $"x=\"{XmlText.Number(v.X)}\" y=\"{XmlText.Number(v.Y)}\" z=\"{XmlText.Number(v.Z)}\" w=\"{XmlText.Number(v.W)}\"";

    private static float Float(ResourceData res, int offset) => BitConverter.UInt32BitsToSingle(res.UInt32(offset));

    private static Vector3 Vec3(ResourceData res, int o) => new(Float(res, o), Float(res, o + 4), Float(res, o + 8));

    private static Vector4 Vec4(ResourceData res, int o) => new(Float(res, o), Float(res, o + 4), Float(res, o + 8), Float(res, o + 12));
}
