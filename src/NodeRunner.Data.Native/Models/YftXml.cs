using System.Globalization;
using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using static NodeRunner.Data.Native.Models.DrawableXml;

namespace NodeRunner.Data.Native.Models;

// Fragments (.yft: vehicles, breakable objects) of the game as the XML read by Sollumz and CodeWalker: the main
// drawable and its variants, the bone transforms, the physics (parts, groups, joints), lights and glass panes.
// Not written yet: the windows of vehicles (shatter maps) and cloth.
public static class YftXml
{
    private const int FragDrawableLength = 0x150;
    private const int PhysicsGroupLength = 0x30;
    private const int PhysicsLodLength = 0x130;
    private const int ArchetypeLength = 0xE0;
    private const int ChildLength = 0x100;
    private const int GroupLength = 0xB0;
    private const int GlassWindowLength = 0x70;

    public static XmlConversion Export(string fileName, ReadOnlySpan<byte> data, ResourceHeader header, ShaderNameCatalog names)
    {
        YdrXml.RequireOriginal(header);
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        if (res.UInt16(0x68) > 0)
            throw new InvalidDataException("Objet en tissu (rideau, drapeau...) : pas encore exportable en XML.");
        var output = new Output(names, YtdXml.XmlBaseName(fileName));
        var xml = new XmlText();
        xml.Open("Fragment");
        ulong name = res.UInt64(0x58);
        xml.Text("Name", name == 0 ? null : res.Text(name, "nom du fragment"));
        xml.Vector("BoundingSphereCenter", Vec3(res, 0x20));
        xml.Value("BoundingSphereRadius", Float(res, 0x2C));
        xml.Value("UnknownB0", (int)res.UInt32(0xB0));
        xml.Value("UnknownB8", (int)res.UInt32(0xB8));
        xml.Value("UnknownBC", (int)res.UInt32(0xBC));
        xml.Value("UnknownC0", (int)res.UInt32(0xC0));
        xml.Value("UnknownC4", (int)res.UInt32(0xC4));
        xml.Value("UnknownCC", Float(res, 0xCC));
        xml.Value("GravityFactor", Float(res, 0xD0));
        xml.Value("BuoyancyFactor", Float(res, 0xD4));

        if (res.UInt64(0x30) is not 0 and var drawable)
            WriteFragDrawable(xml, res, Resolve(res, drawable, FragDrawableLength, "modèle"), "Drawable", output, child: false);

        int arrayCount = (int)res.UInt32(0x48);
        if (arrayCount > 0 && res.UInt64(0x38) is not 0 and var array)
        {
            int list = res.ResolveArray(array, arrayCount, 8, "variantes");
            int names2 = res.UInt64(0x40) is not 0 and var n ? res.ResolveArray(n, arrayCount, 8, "noms des variantes") : -1;
            xml.Open("DrawableArray");
            for (int i = 0; i < arrayCount; i++)
            {
                if (res.UInt64(list + i * 8) is 0)
                    continue;
                string? variant = names2 >= 0 && res.UInt64(names2 + i * 8) is not 0 and var p ? res.Text(p, "nom de variante") : null;
                WriteFragDrawable(xml, res, Resolve(res, res.UInt64(list + i * 8), FragDrawableLength, "variante"), "Item", output, child: false, variant);
            }
            xml.Close("DrawableArray");
        }

        if (res.UInt64(0xA8) is not 0 and var bones)
            WriteBoneTransforms(xml, res, Resolve(res, bones, 0x20, "positions des os"));

        if (res.UInt64(0xF0) is not 0 and var physics)
        {
            int group = Resolve(res, physics, PhysicsGroupLength, "physique");
            xml.Open("Physics");
            for (int lod = 0; lod < 3; lod++)
            {
                if (res.UInt64(group + 0x10 + lod * 8) is not 0 and var p)
                {
                    xml.Open($"LOD{lod + 1}");
                    WritePhysicsLod(xml, res, Resolve(res, p, PhysicsLodLength, "physique"), output);
                    xml.Close($"LOD{lod + 1}");
                }
            }
            xml.Close("Physics");
        }

        int glassCount = res.Byte(0xD9);
        if (glassCount > 0 && res.UInt64(0xE0) is not 0 and var glass)
        {
            int list = res.ResolveArray(glass, glassCount, 8, "vitres");
            xml.Open("GlassWindows");
            for (int i = 0; i < glassCount; i++)
            {
                if (res.UInt64(list + i * 8) is 0)
                {
                    xml.Empty("Item");
                    continue;
                }
                xml.Open("Item");
                WriteGlassWindow(xml, res, Resolve(res, res.UInt64(list + i * 8), GlassWindowLength, "vitre"));
                xml.Close("Item");
            }
            xml.Close("GlassWindows");
        }

        int lightCount = res.UInt16(0x118);
        if (lightCount > 0 && res.UInt64(0x110) is not 0 and var lights)
            WriteLights(xml, res, res.ResolveArray(lights, lightCount, LightRecordLength, "lumières"), lightCount);
        else
            xml.Empty("Lights");

        xml.Close("Fragment");
        return new XmlConversion(fileName + ".xml", xml.ToString(), output.Files);
    }

    private static int Resolve(ResourceData res, ulong pointer, int length, string what) => res.ResolveArray(pointer, 1, length, what);

    // A drawable of the fragment: the main one or a variant (with their collision), or the one of a physics part
    // (child: no skeleton, no collision of its own).
    private static void WriteFragDrawable(XmlText xml, ResourceData res, int d, string element, Output output, bool child, string? name = null)
    {
        xml.Open(element);
        ulong namePointer = res.UInt64(d + 0x130);
        xml.Text("Name", name ?? (namePointer == 0 ? null : res.Text(namePointer, "nom du modèle")));
        xml.RawArray("Matrix", Matrix43(res, d + 0xB0), 3);

        int capacity = res.UInt16(d + 0x102);
        int count = Math.Min(capacity, res.UInt16(d + 0x110));
        if (capacity > 0 && res.UInt64(d + 0x108) is not 0 and var matrices)
        {
            int at = res.ResolveArray(matrices, capacity, 64, "matrices");
            int indexCount = res.UInt16(d + 0x100);
            int indices = indexCount > 0 && res.UInt64(d + 0xF8) is not 0 and var ip ? res.ResolveArray(ip, indexCount, 8, "matrices") : -1;
            xml.Open("Matrices", $"capacity=\"{capacity}\"");
            for (int i = 0; i < count; i++)
            {
                ulong id = indices >= 0 && i < indexCount ? res.UInt64(indices + i * 8) : 0;
                xml.Open("Item", $"id=\"{id}\"");
                xml.Rows(Matrix43(res, at + i * 64), 3);
                xml.Close("Item");
            }
            xml.Close("Matrices");
        }

        WriteDrawable(xml, res, d, output, withName: false, withSkeleton: !child);
        if (!child && res.UInt64(d + 0xF0) is not 0 and var bounds)
            YbnXml.WriteBound(xml, res, Resolve(res, bounds, 0x70, "collision"), "Bounds", child: null, fragment: true);
        xml.Close(element);
    }

    // A 4 x 3 matrix stored as four columns of 16 bytes (the 4th value of each unused).
    private static string[] Matrix43(ResourceData res, int at)
    {
        var values = new string[12];
        for (int c = 0; c < 4; c++)
        {
            for (int r = 0; r < 3; r++)
                values[c * 3 + r] = XmlText.Number(Float(res, at + c * 16 + r * 4));
        }
        return values;
    }

    private static string[] Floats(ResourceData res, int at, int count)
    {
        var values = new string[count];
        for (int i = 0; i < count; i++)
            values[i] = XmlText.Number(Float(res, at + i * 4));
        return values;
    }

    private static void WriteBoneTransforms(XmlText xml, ResourceData res, int b)
    {
        int count = res.Byte(b + 0x10);
        string head = $"unk=\"{res.UInt16(b + 0x12)}\"";
        if (count == 0)
        {
            xml.Empty("BoneTransforms", head);
            return;
        }
        xml.Open("BoneTransforms", head);
        for (int i = 0; i < count; i++)
        {
            xml.Open("Item");
            xml.Rows(Floats(res, b + 0x20 + i * 48, 12), 4);
            xml.Close("Item");
        }
        xml.Close("BoneTransforms");
    }

    // ---- Physics ----

    private static void WritePhysicsLod(XmlText xml, ResourceData res, int l, Output output)
    {
        xml.Value("Unknown14", Float(res, l + 0x14));
        xml.Value("Unknown18", Float(res, l + 0x18));
        xml.Value("Unknown1C", Float(res, l + 0x1C));
        xml.Vector("PositionOffset", Vec3(res, l + 0x30));
        xml.Vector("Unknown40", Vec3(res, l + 0x40));
        xml.Vector("Unknown50", Vec3(res, l + 0x50));
        string[] damping = ["DampingLinearC", "DampingLinearV", "DampingLinearV2", "DampingAngularC", "DampingAngularV", "DampingAngularV2"];
        for (int i = 0; i < damping.Length; i++)
            xml.Vector(damping[i], Vec3(res, l + 0x60 + i * 16));

        if (res.UInt64(l + 0xD8) is not 0 and var archetype)
            WriteArchetype(xml, res, Resolve(res, archetype, ArchetypeLength, "archétype"), "Archetype");
        if (res.UInt64(l + 0xE0) is not 0 and var archetype2)
            WriteArchetype(xml, res, Resolve(res, archetype2, ArchetypeLength, "archétype"), "Archetype2");
        if (res.UInt64(l + 0x20) is not 0 and var body)
            WriteArticulatedBody(xml, res, Resolve(res, body, 0xB0, "articulations"));
        if (res.UInt64(l + 0x100) is not 0 and var transforms)
        {
            int t = Resolve(res, transforms, 0x20, "positions des morceaux");
            int count = (int)res.UInt32(t + 0x10);
            xml.Open("Transforms");
            for (int i = 0; i < count; i++)
            {
                xml.Open("Item");
                xml.Rows(Floats(res, t + 0x20 + i * 64, 16), 4);
                xml.Close("Item");
            }
            xml.Close("Transforms");
        }

        int groupCount = res.Byte(l + 0x11A);
        if (groupCount > 0 && res.UInt64(l + 0xC8) is not 0 and var groups)
        {
            int list = res.ResolveArray(groups, groupCount, 8, "groupes");
            xml.Open("Groups");
            for (int i = 0; i < groupCount; i++)
            {
                if (res.UInt64(list + i * 8) is 0)
                {
                    xml.Empty("Item");
                    continue;
                }
                xml.Open("Item");
                WriteGroup(xml, res, Resolve(res, res.UInt64(list + i * 8), GroupLength, "groupe"));
                xml.Close("Item");
            }
            xml.Close("Groups");
        }

        int childCount = res.Byte(l + 0x11D);
        if (childCount > 0 && res.UInt64(l + 0xD0) is not 0 and var children)
        {
            int list = res.ResolveArray(children, childCount, 8, "morceaux");
            int floats = res.UInt64(l + 0x28) is not 0 and var f ? res.ResolveArray(f, childCount, 4, "morceaux") : -1;
            int tensors = res.UInt64(l + 0xF0) is not 0 and var it ? res.ResolveArray(it, childCount, 16, "morceaux") : -1;
            int vectors = res.UInt64(l + 0xF8) is not 0 and var v ? res.ResolveArray(v, childCount, 16, "morceaux") : -1;
            xml.Open("Children");
            for (int i = 0; i < childCount; i++)
            {
                if (res.UInt64(list + i * 8) is 0)
                {
                    xml.Empty("Item");
                    continue;
                }
                int c = Resolve(res, res.UInt64(list + i * 8), ChildLength, "morceau");
                xml.Open("Item");
                xml.Value("GroupIndex", res.UInt16(c + 0x10));
                xml.Value("BoneTag", res.UInt16(c + 0x12));
                xml.Value("PristineMass", Float(res, c + 0x08));
                xml.Value("DamagedMass", Float(res, c + 0x0C));
                xml.Value("UnkFloat", floats >= 0 ? Float(res, floats + i * 4) : 0f);
                xml.Empty("UnkVec", Vector4Attributes(vectors >= 0 ? Vec4(res, vectors + i * 16) : default));
                xml.Empty("InertiaTensor", Vector4Attributes(tensors >= 0 ? Vec4(res, tensors + i * 16) : default));
                if (res.UInt64(c + 0xB0) != 0)
                    xml.Empty("EventSet");
                if (res.UInt64(c + 0xA0) is not 0 and var d1)
                    WriteFragDrawable(xml, res, Resolve(res, d1, FragDrawableLength, "modèle du morceau"), "Drawable", output, child: true);
                if (res.UInt64(c + 0xA8) is not 0 and var d2)
                    WriteFragDrawable(xml, res, Resolve(res, d2, FragDrawableLength, "modèle du morceau"), "Drawable2", output, child: true);
                xml.Close("Item");
            }
            xml.Close("Children");
        }

        WriteBytes(xml, res, "UnknownData1", res.UInt64(l + 0x108), res.Byte(l + 0x118));
        WriteBytes(xml, res, "UnknownData2", res.UInt64(l + 0x110), res.Byte(l + 0x119));
    }

    private static void WriteBytes(XmlText xml, ResourceData res, string name, ulong pointer, int count)
    {
        if (pointer == 0)
            return;
        var values = new string[count];
        if (count > 0)
        {
            int at = res.ResolveArray(pointer, count, 1, name);
            for (int i = 0; i < count; i++)
                values[i] = res.Byte(at + i).ToString(CultureInfo.InvariantCulture);
        }
        xml.RawArray(name, values, 10);
    }

    private static void WriteArchetype(XmlText xml, ResourceData res, int a, string element)
    {
        xml.Open(element);
        ulong name = res.UInt64(a + 0x18);
        xml.Text("Name", name == 0 ? null : res.Text(name, "nom de l'archétype"));
        xml.Value("Mass", Float(res, a + 0x40));
        xml.Value("MassInv", Float(res, a + 0x44));
        xml.Value("Unknown48", Float(res, a + 0x48));
        xml.Value("Unknown4C", Float(res, a + 0x4C));
        xml.Value("Unknown50", Float(res, a + 0x50));
        xml.Value("Unknown54", Float(res, a + 0x54));
        xml.Vector("InertiaTensor", Vec3(res, a + 0x60));
        xml.Vector("InertiaTensorInv", Vec3(res, a + 0x70));
        if (res.UInt64(a + 0x20) is not 0 and var bounds)
            YbnXml.WriteBound(xml, res, Resolve(res, bounds, 0x70, "collision"), "Bounds", child: null, fragment: true);
        xml.Close(element);
    }

    private static void WriteArticulatedBody(XmlText xml, ResourceData res, int b)
    {
        xml.Open("ArticulatedBody");
        var indices = new string[22];
        var flags = new string[22];
        for (int i = 0; i < 22; i++)
        {
            indices[i] = res.UInt32(b + 0x10 + i * 4).ToString(CultureInfo.InvariantCulture);
            flags[i] = res.Byte(b + 0x8A + i).ToString(CultureInfo.InvariantCulture);
        }
        xml.RawArray("ItemIndices", indices, 22);
        xml.RawArray("ItemFlags", flags, 22);

        int vectorCount = res.Byte(b + 0x88);
        if (res.UInt64(b + 0x80) is not 0 and var vectorsPointer)
        {
            int vectors = vectorCount == 0 ? -1 : res.ResolveArray(vectorsPointer, vectorCount, 16, "articulations");
            var rows = new string[vectorCount];
            for (int i = 0; i < vectorCount; i++)
            {
                var v = Vec4(res, vectors + i * 16);
                rows[i] = $"{XmlText.Number(v.X)}, {XmlText.Number(v.Y)}, {XmlText.Number(v.Z)}, {XmlText.Number(v.W)}";
            }
            if (rows.Length == 1)
                xml.Text("UnknownVectors", rows[0]);
            else if (rows.Length == 0)
                xml.Empty("UnknownVectors");
            else
                xml.Lines("UnknownVectors", null, rows);
        }

        int jointCount = res.Byte(b + 0x89);
        if (res.UInt64(b + 0x78) is not 0 and var jointsPointer)
        {
            int joints = jointCount == 0 ? -1 : res.ResolveArray(jointsPointer, jointCount, 8, "articulations");
            xml.Open("Joints");
            for (int i = 0; i < jointCount; i++)
            {
                if (res.UInt64(joints + i * 8) is 0)
                {
                    xml.Empty("Item");
                    continue;
                }
                int j = Resolve(res, res.UInt64(joints + i * 8), 0x20, "articulation");
                byte type = res.Byte(j + 0x15);
                xml.Open("Item", $"type=\"{(type == 0 ? "DOF1" : type == 1 ? "DOF3" : type.ToString(CultureInfo.InvariantCulture))}\"");
                xml.Value("FragIndex1", res.Byte(j + 0x16));
                xml.Value("FragIndex2", res.Byte(j + 0x17));
                xml.Value("Unknown10", Float(res, j + 0x10));
                // Both kinds are written with their first 9 vectors, as the other tools read them.
                for (int v = 0; v < (type <= 1 ? 9 : 0); v++)
                    xml.Empty($"Unknown{0x20 + v * 0x10:X2}", Vector4Attributes(Vec4(res, j + 0x20 + v * 16)));
                xml.Close("Item");
            }
            xml.Close("Joints");
        }
        xml.Close("ArticulatedBody");
    }

    private static void WriteGroup(XmlText xml, ResourceData res, int g)
    {
        var name = new StringBuilder();
        for (int i = 0; i < 40 && res.Byte(g + 0x80 + i) is not 0 and var c; i++)
            name.Append((char)c);
        xml.Text("Name", name.ToString());
        xml.Value("ParentIndex", res.Byte(g + 0x4D));
        xml.Value("GlassWindowIndex", res.Byte(g + 0x52));
        xml.Value("GlassFlags", res.Byte(g + 0x53));
        string[] fields =
        [
            "Strength", "ForceTransmissionScaleUp", "ForceTransmissionScaleDown", "JointStiffness", "MinSoftAngle1",
            "MaxSoftAngle1", "MaxSoftAngle2", "MaxSoftAngle3", "RotationSpeed", "RotationStrength", "RestoringStrength",
            "RestoringMaxTorque", "LatchStrength", "Mass",
        ];
        for (int i = 0; i < fields.Length; i++)
            xml.Value(fields[i], Float(res, g + 0x10 + i * 4));
        xml.Value("MinDamageForce", Float(res, g + 0x54));
        xml.Value("DamageHealth", Float(res, g + 0x58));
        for (int at = 0x5C; at <= 0x78; at += 4)
            xml.Value($"UnkFloat{at:X2}", Float(res, g + at));
        xml.Value("UnkFloatA8", Float(res, g + 0xA8));
    }

    private static void WriteGlassWindow(XmlText xml, ResourceData res, int w)
    {
        xml.Value("Flags", res.UInt16(w + 0x56));
        xml.Open("Projection");
        var projection = new string[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
                projection[r * 3 + c] = XmlText.Number(Float(res, w + r * 16 + c * 4));
        }
        xml.Rows(projection, 3);
        xml.Close("Projection");
        xml.Value("UnkFloat13", Float(res, w + 0x30));
        xml.Value("UnkFloat14", Float(res, w + 0x34));
        xml.Value("UnkFloat15", Float(res, w + 0x38));
        xml.Value("UnkFloat16", Float(res, w + 0x3C));
        xml.Value("Thickness", Float(res, w + 0x50));
        xml.Value("UnkFloat18", Float(res, w + 0x58));
        xml.Value("UnkFloat19", Float(res, w + 0x5C));
        xml.Vector("Tangent", Vec3(res, w + 0x60));
        uint flags = res.UInt32(w + 0x40);
        xml.Open("Layout", $"type=\"{LayoutName(res.UInt64(w + 0x48))}\"");
        for (int c = 0; c < 16; c++)
        {
            if ((flags >> c & 1) != 0)
                xml.Empty(Semantics[c]);
        }
        xml.Close("Layout");
    }
}
