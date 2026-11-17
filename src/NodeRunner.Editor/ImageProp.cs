using System.Globalization;
using System.Numerics;
using System.Text;

namespace NodeRunner.Editor;

// A round plaque made from an image: a disc with the image on its front (and back), its edge coloured by the image's
// rim. In the prop's space: the back face on y = 0 (the pivot at its centre, to hang it on a wall), the front towards
// -y, z up. Written in the XML forms of Sollumz (YdrXml, YtypXml), the texture embedded in the model.
public static class ImageProp
{
    public readonly record struct Options(string Name, float Diameter, float Thickness, bool BackImage);

    public const float MinDiameter = 0.05f, MaxDiameter = 30f;
    public const float MinThickness = 0.002f, MaxThickness = 1f;

    // Where the disc reads the texture: the image fills it, the face stops just inside its edge (no edge pixels), the
    // rim is sampled a little further in (the coloured ring of a badge).
    public const float FaceRadius = 0.495f;
    public const float RimRadius = 0.475f;

    private const int Sides = 64;
    private const int CollisionSides = 16;

    public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

    // The model's triangles (three vertices each, anticlockwise seen from the side they face).
    public static List<Vertex> Triangles(Options o)
    {
        float r = o.Diameter * 0.5f, t = o.Thickness;
        var list = new List<Vertex>(Sides * 12);
        Vector3 Ring(int i, float y) => new(r * MathF.Cos(Angle(i)), y, r * MathF.Sin(Angle(i)));
        Vector2 Uv(int i, float radius, bool mirrored) =>
            new(0.5f + (mirrored ? -1 : 1) * radius * MathF.Cos(Angle(i)), 0.5f - radius * MathF.Sin(Angle(i)));
        var rimTop = new Vector2(0.5f, 0.5f - RimRadius);

        var front = new Vector3(0, -1, 0);
        var back = new Vector3(0, 1, 0);
        for (int i = 0; i < Sides; i++)
        {
            // Front, seen from -y: x to the right, z up, as the image.
            Add(list, front, new(new(0, -t, 0), front, new(0.5f, 0.5f)), new(Ring(i, -t), front, Uv(i, FaceRadius, false)),
                new(Ring(i + 1, -t), front, Uv(i + 1, FaceRadius, false)));
            // Back, seen from +y: the image the right way round, or the rim's colour.
            Add(list, back, new(Vector3.Zero, back, o.BackImage ? new(0.5f, 0.5f) : rimTop),
                new(Ring(i, 0), back, o.BackImage ? Uv(i, FaceRadius, true) : rimTop),
                new(Ring(i + 1, 0), back, o.BackImage ? Uv(i + 1, FaceRadius, true) : rimTop));
            // Edge, coloured by the rim at the same angle.
            var n0 = Vector3.Normalize(Ring(i, 0));
            var n1 = Vector3.Normalize(Ring(i + 1, 0));
            var a = new Vertex(Ring(i, -t), n0, Uv(i, RimRadius, false));
            var b = new Vertex(Ring(i + 1, -t), n1, Uv(i + 1, RimRadius, false));
            var c = new Vertex(Ring(i + 1, 0), n1, Uv(i + 1, RimRadius, false));
            var d = new Vertex(Ring(i, 0), n0, Uv(i, RimRadius, false));
            var outward = Vector3.Normalize(n0 + n1);
            Add(list, outward, a, b, c);
            Add(list, outward, a, c, d);
        }
        return list;
    }

    private static float Angle(int i) => MathF.Tau * (i % Sides) / Sides;

    // Adds the triangle turned so that it faces facing.
    private static void Add(List<Vertex> list, Vector3 facing, Vertex a, Vertex b, Vertex c)
    {
        var n = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        if (Vector3.Dot(n, facing) >= 0)
            list.AddRange([a, b, c]);
        else
            list.AddRange([a, c, b]);
    }

    // A valid model name from an image's file name: "Secret Service.png" -> "secret_service".
    public static string SuggestName(string file)
    {
        var name = new StringBuilder();
        foreach (char c in Path.GetFileNameWithoutExtension(file).ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
                name.Append(c);
            else if (name.Length > 0 && name[^1] != '_')
                name.Append('_');
        }
        string text = name.ToString().Trim('_');
        if (text.Length == 0 || !char.IsAsciiLetterLower(text[0]))
            text = "prop_" + text;
        return text.Length > 48 ? text[..48].TrimEnd('_') : text.TrimEnd('_');
    }

    public static (Vector3 Min, Vector3 Max) Box(Options o)
    {
        float r = o.Diameter * 0.5f;
        return (new(-r, -o.Thickness, -r), new(r, 0, r));
    }

    // LOD distance of the archetype: further for a bigger plaque.
    public static float LodDistance(Options o) => Math.Clamp(o.Diameter * 100f, 100f, 500f);

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    // The model: one shader (default.sps) reading the embedded texture, its collision inside. texture: the texture's
    // name; its image is "<name>/<texture>.dds" next to the XML. material: index in materials.dat.
    public static string DrawableXml(Options o, string texture, int material)
    {
        var triangles = Triangles(o);
        var (min, max) = Box(o);
        float radius = Vector3.Distance(min, max) * 0.5f;
        var center = (min + max) * 0.5f;
        var data = new StringBuilder();
        foreach (var v in triangles)
        {
            data.Append(F(v.Position.X)).Append(' ').Append(F(v.Position.Y)).Append(' ').Append(F(v.Position.Z)).Append("   ")
                .Append(F(v.Normal.X)).Append(' ').Append(F(v.Normal.Y)).Append(' ').Append(F(v.Normal.Z)).Append("   255 255 255 255   ")
                .Append(F(v.TexCoord.X)).Append(' ').Append(F(v.TexCoord.Y)).Append('\n');
        }
        string indices = string.Join(' ', Enumerable.Range(0, triangles.Count));
        string box = $"<BoundingBoxMin x=\"{F(min.X)}\" y=\"{F(min.Y)}\" z=\"{F(min.Z)}\" />\n <BoundingBoxMax x=\"{F(max.X)}\" y=\"{F(max.Y)}\" z=\"{F(max.Z)}\" />";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Drawable>
             <Name>{o.Name}</Name>
             <BoundingSphereCenter x="{F(center.X)}" y="{F(center.Y)}" z="{F(center.Z)}" />
             <BoundingSphereRadius value="{F(radius)}" />
             {box}
             <LodDistHigh value="9998" />
             <LodDistMed value="9998" />
             <LodDistLow value="9998" />
             <LodDistVlow value="9998" />
             <FlagsHigh value="1" />
             <FlagsMed value="0" />
             <FlagsLow value="0" />
             <FlagsVlow value="0" />
             <ShaderGroup>
              <TextureDictionary>
               <Item>
                <Name>{texture}</Name>
                <Unk32 value="128" />
                <Usage>DIFFUSE</Usage>
                <UsageFlags />
                <ExtraFlags value="0" />
                <FileName>{texture}.dds</FileName>
               </Item>
              </TextureDictionary>
              <Shaders>
               <Item>
                <Name>default</Name>
                <FileName>default.sps</FileName>
                <RenderBucket value="0" />
                <Parameters>
                 <Item name="DiffuseSampler" type="Texture">
                  <Name>{texture}</Name>
                 </Item>
                 <Item name="matMaterialColorScale" type="Vector" x="1" y="0" z="0" w="1" />
                 <Item name="HardAlphaBlend" type="Vector" x="1" y="0" z="0" w="0" />
                 <Item name="useTessellation" type="Vector" x="0" y="0" z="0" w="0" />
                 <Item name="wetnessMultiplier" type="Vector" x="1" y="0" z="0" w="0" />
                 <Item name="globalAnimUV1" type="Vector" x="0" y="1" z="0" w="0" />
                 <Item name="globalAnimUV0" type="Vector" x="1" y="0" z="0" w="0" />
                </Parameters>
               </Item>
              </Shaders>
             </ShaderGroup>
             <DrawableModelsHigh>
              <Item>
               <RenderMask value="255" />
               <Flags value="0" />
               <HasSkin value="0" />
               <BoneIndex value="0" />
               <Unknown1 value="0" />
               <Geometries>
                <Item>
                 <ShaderIndex value="0" />
                 <BoundingBoxMin x="{F(min.X)}" y="{F(min.Y)}" z="{F(min.Z)}" w="0" />
                 <BoundingBoxMax x="{F(max.X)}" y="{F(max.Y)}" z="{F(max.Z)}" w="0" />
                 <VertexBuffer>
                  <Flags value="0" />
                  <Layout type="GTAV1">
                   <Position />
                   <Normal />
                   <Colour0 />
                   <TexCoord0 />
                  </Layout>
                  <Data>
            {data}</Data>
                 </VertexBuffer>
                 <IndexBuffer>
                  <Data>{indices}</Data>
                 </IndexBuffer>
                </Item>
               </Geometries>
              </Item>
             </DrawableModelsHigh>
            {CollisionXml(o, material)}
            </Drawable>
            """;
    }

    // The collision: the same disc with fewer sides, closed (front, back, edge).
    public static string CollisionXml(Options o, int material)
    {
        float r = o.Diameter * 0.5f, t = o.Thickness;
        var vertices = new StringBuilder();
        void Vertex(float x, float y, float z) => vertices.Append(F(x)).Append(", ").Append(F(y)).Append(", ").Append(F(z)).Append('\n');
        // 0: front centre, 1: back centre, then front and back of each side.
        Vertex(0, -t, 0);
        Vertex(0, 0, 0);
        for (int i = 0; i < CollisionSides; i++)
        {
            float a = MathF.Tau * i / CollisionSides;
            Vertex(r * MathF.Cos(a), -t, r * MathF.Sin(a));
            Vertex(r * MathF.Cos(a), 0, r * MathF.Sin(a));
        }
        var polygons = new StringBuilder();
        void Triangle(int a, int b, int c) => polygons.Append($"<Triangle m=\"0\" v1=\"{a}\" v2=\"{b}\" v3=\"{c}\" f1=\"0\" f2=\"0\" f3=\"0\" />\n");
        for (int i = 0; i < CollisionSides; i++)
        {
            int f0 = 2 + i * 2, b0 = f0 + 1;
            int f1 = 2 + (i + 1) % CollisionSides * 2, b1 = f1 + 1;
            // Turned outwards (the same order as the model's faces).
            Triangle(0, f0, f1);
            Triangle(1, b1, b0);
            Triangle(f0, b1, f1);
            Triangle(f0, b0, b1);
        }
        return $"""
             <Bounds type="Composite">
              <Children>
               <Item type="GeometryBVH">
                <CompositeTransform>1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1</CompositeTransform>
                <CompositeFlags1>{RoomShell.TypeFlags}</CompositeFlags1>
                <CompositeFlags2>{RoomShell.IncludeFlags}</CompositeFlags2>
                <GeometryCenter x="0" y="0" z="0" />
                <Materials>
                 <Item><Type value="{material}" /></Item>
                </Materials>
                <Vertices>
            {vertices}</Vertices>
                <Polygons>
            {polygons}</Polygons>
               </Item>
              </Children>
             </Bounds>
            """;
    }

    public static string TypesXml(Options o)
    {
        var (min, max) = Box(o);
        return RoomShell.TypesXml(o.Name, "", LodDistance(o), min, max);
    }
}
