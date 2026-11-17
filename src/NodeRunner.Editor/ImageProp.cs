using System.Globalization;
using System.Numerics;
using System.Text;

namespace NodeRunner.Editor;

// A round plaque made from an image: a disc with the image on its front (and back), its edge coloured by the image's
// rim. In the prop's space: the back face on y = 0 (the pivot at its centre, to hang it on a wall), the front towards
// -y, z up. Written in the XML forms of Sollumz (YdrXml, YtypXml), the texture embedded in the model.
public static class ImageProp
{
    // Relief: how far the raised parts stand out of the face (m), 0 for a flat face; RaiseDark: the image's darker
    // parts raised instead of its brighter ones.
    public readonly record struct Options(string Name, float Diameter, float Thickness, bool BackImage, float Relief = 0,
        bool RaiseDark = false);

    public const float MinDiameter = 0.05f, MaxDiameter = 30f;
    public const float MinThickness = 0.002f, MaxThickness = 1f;
    public const float MaxRelief = 0.5f;

    // Where the disc reads the texture: the image fills it, the face stops just inside its edge (no edge pixels), the
    // rim is sampled a little further in (the coloured ring of a badge).
    public const float FaceRadius = 0.495f;
    public const float RimRadius = 0.475f;

    private const int Sides = 64;
    private const int CollisionSides = 16;

    // A face in relief is a grid of rings, its points about as far apart along a ring as between rings (some 20 000
    // points: one model with 16-bit indices).
    private const int ReliefRings = 80;

    public readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

    // Triangles: three indices each, anticlockwise seen from the side they face.
    public sealed class Mesh
    {
        public List<Vertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];

        public int Add(Vertex v)
        {
            Vertices.Add(v);
            return Vertices.Count - 1;
        }

        // Adds the triangle turned so that it faces facing; on the front and back, judged on its outline seen from
        // there (a steep slope of the relief still faces out).
        public void Triangle(int a, int b, int c, Vector3 facing)
        {
            if (a == b || b == c || a == c)
                return;
            var level = MathF.Abs(facing.Y) > 0.99f ? new Vector3(1, 0, 1) : Vector3.One;
            var pa = Vertices[a].Position * level;
            var n = Vector3.Cross(Vertices[b].Position * level - pa, Vertices[c].Position * level - pa);
            if (Vector3.Dot(n, facing) >= 0)
                Indices.AddRange([a, b, c]);
            else
                Indices.AddRange([a, c, b]);
        }
    }

    // The model. height: from a point of the image (texture coordinates) to how raised it is, 0 to 1; null (or no
    // relief) for a flat face.
    public static Mesh Build(Options o, Func<Vector2, float>? height = null)
    {
        bool relief = height is not null && o.Relief > 0;
        int rings = relief ? ReliefRings : 1;
        float r = o.Diameter * 0.5f, t = o.Thickness;
        int Count(int k) => k == 0 ? 1 : rings == 1 ? Sides : Math.Max(8, (int)MathF.Round(MathF.Tau * k));
        static Vector2 Direction(int i, int n) => new(MathF.Cos(MathF.Tau * i / n), MathF.Sin(MathF.Tau * i / n));
        static Vector2 Uv(Vector2 dir, float radius, bool mirrored) =>
            new(0.5f + (mirrored ? -1 : 1) * radius * dir.X, 0.5f - radius * dir.Y);
        var mesh = new Mesh();

        // Front, seen from -y: x to the right, z up, as the image; rings from the centre out.
        var front = new Vector3(0, -1, 0);
        var starts = new int[rings + 1];
        for (int k = 0; k <= rings; k++)
        {
            starts[k] = mesh.Vertices.Count;
            float part = (float)k / rings;
            for (int i = 0, n = Count(k); i < n; i++)
            {
                var dir = Direction(i, n);
                var uv = Uv(dir, FaceRadius * part, false);
                float y = -t - (relief ? o.Relief * Math.Clamp(height!(uv), 0f, 1f) : 0f);
                mesh.Add(new(new(r * part * dir.X, y, r * part * dir.Y), front, uv));
            }
        }
        for (int k = 0; k < rings; k++)
            Stitch(mesh, starts[k], Count(k), starts[k + 1], Count(k + 1), front);
        if (relief)
            SmoothNormals(mesh, mesh.Vertices.Count, front);
        int outer = Count(rings);

        // Back, seen from +y: the image the right way round, or the rim's colour.
        var back = new Vector3(0, 1, 0);
        var rimTop = new Vector2(0.5f, 0.5f - RimRadius);
        int center = mesh.Add(new(Vector3.Zero, back, o.BackImage ? new(0.5f, 0.5f) : rimTop));
        for (int i = 0; i < outer; i++)
        {
            var dir = Direction(i, outer);
            mesh.Add(new(new(r * dir.X, 0, r * dir.Y), back, o.BackImage ? Uv(dir, FaceRadius, true) : rimTop));
        }
        Stitch(mesh, center, 1, center + 1, outer, back);

        // Edge, coloured by the rim at the same angle, from the front's outer ring (raised or not) to the back.
        int edge = mesh.Vertices.Count;
        for (int i = 0; i < outer; i++)
        {
            var dir = Direction(i, outer);
            var normal = new Vector3(dir.X, 0, dir.Y);
            var uv = Uv(dir, RimRadius, false);
            mesh.Add(new(mesh.Vertices[starts[rings] + i].Position, normal, uv));
            mesh.Add(new(new(r * dir.X, 0, r * dir.Y), normal, uv));
        }
        for (int i = 0; i < outer; i++)
        {
            int f0 = edge + i * 2, b0 = f0 + 1;
            int f1 = edge + (i + 1) % outer * 2, b1 = f1 + 1;
            var outward = Vector3.Normalize(mesh.Vertices[f0].Normal + mesh.Vertices[f1].Normal);
            mesh.Triangle(f0, f1, b1, outward);
            mesh.Triangle(f0, b1, b0, outward);
        }
        return mesh;
    }

    // The model as a list of triangles (three vertices each).
    public static List<Vertex> Triangles(Options o, Func<Vector2, float>? height = null)
    {
        var mesh = Build(o, height);
        return [.. mesh.Indices.Select(i => mesh.Vertices[i])];
    }

    // Joins two rings of points (count a and b, evenly spaced from angle 0) with triangles, walking round both at once.
    private static void Stitch(Mesh mesh, int a, int countA, int b, int countB, Vector3 facing)
    {
        int A(int i) => a + i % countA;
        int B(int j) => b + j % countB;
        for (int i = 0, j = 0; i < countA || j < countB;)
        {
            float nextA = (float)(i + 1) / countA, nextB = (float)(j + 1) / countB;
            if (j >= countB || (i < countA && nextA < nextB))
            {
                mesh.Triangle(A(i), A(i + 1), B(j), facing);
                i++;
            }
            else
            {
                mesh.Triangle(A(i), B(j + 1), B(j), facing);
                j++;
            }
        }
    }

    // Normals of the front's points (the first count of the mesh) from the triangles around them.
    private static void SmoothNormals(Mesh mesh, int count, Vector3 facing)
    {
        var sums = new Vector3[count];
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            var pa = mesh.Vertices[a].Position;
            var n = Vector3.Cross(mesh.Vertices[b].Position - pa, mesh.Vertices[c].Position - pa);
            sums[a] += n;
            sums[b] += n;
            sums[c] += n;
        }
        for (int i = 0; i < count; i++)
        {
            var n = sums[i].LengthSquared() > 1e-20f ? Vector3.Normalize(sums[i]) : facing;
            mesh.Vertices[i] = mesh.Vertices[i] with { Normal = n };
        }
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
        return (new(-r, -o.Thickness - o.Relief, -r), new(r, 0, r));
    }

    // LOD distance of the archetype: further for a bigger plaque.
    public static float LodDistance(Options o) => Math.Clamp(o.Diameter * 100f, 100f, 500f);

    // Up to where the relief is drawn; the flat plaque further away.
    public static float ReliefDistance(Options o) => Math.Clamp(o.Diameter * 25f, 10f, 100f);

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    // The model: one shader (default.sps) reading the embedded texture, its collision inside. texture: the texture's
    // name; its image is "<name>/<texture>.dds" next to the XML. material: index in materials.dat. height: the relief
    // (see Build), drawn up close, the flat plaque further away.
    public static string DrawableXml(Options o, string texture, int material, Func<Vector2, float>? height = null)
    {
        bool relief = height is not null && o.Relief > 0;
        var (min, max) = Box(o);
        float radius = Vector3.Distance(min, max) * 0.5f;
        var center = (min + max) * 0.5f;
        string box = $"<BoundingBoxMin x=\"{F(min.X)}\" y=\"{F(min.Y)}\" z=\"{F(min.Z)}\" />\n <BoundingBoxMax x=\"{F(max.X)}\" y=\"{F(max.Y)}\" z=\"{F(max.Z)}\" />";
        string high = ModelXml(Build(o, height), min, max);
        string medium = relief ? $"""
             <DrawableModelsMedium>
            {ModelXml(Build(o), min, max)}
             </DrawableModelsMedium>
            """ : "";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Drawable>
             <Name>{o.Name}</Name>
             <BoundingSphereCenter x="{F(center.X)}" y="{F(center.Y)}" z="{F(center.Z)}" />
             <BoundingSphereRadius value="{F(radius)}" />
             {box}
             <LodDistHigh value="{(relief ? F(ReliefDistance(o)) : "9998")}" />
             <LodDistMed value="9998" />
             <LodDistLow value="9998" />
             <LodDistVlow value="9998" />
             <FlagsHigh value="1" />
             <FlagsMed value="{(relief ? 1 : 0)}" />
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
            {high}
             </DrawableModelsHigh>
            {medium}
            {CollisionXml(o, material)}
            </Drawable>
            """;
    }

    private static string ModelXml(Mesh mesh, Vector3 min, Vector3 max)
    {
        var data = new StringBuilder();
        foreach (var v in mesh.Vertices)
        {
            data.Append(F(v.Position.X)).Append(' ').Append(F(v.Position.Y)).Append(' ').Append(F(v.Position.Z)).Append("   ")
                .Append(F(v.Normal.X)).Append(' ').Append(F(v.Normal.Y)).Append(' ').Append(F(v.Normal.Z)).Append("   255 255 255 255   ")
                .Append(F(v.TexCoord.X)).Append(' ').Append(F(v.TexCoord.Y)).Append('\n');
        }
        return $"""
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
                  <Data>{string.Join(' ', mesh.Indices)}</Data>
                 </IndexBuffer>
                </Item>
               </Geometries>
              </Item>
            """;
    }

    // The collision: the same disc with fewer sides, closed (front, back, edge), as thick as the relief's top.
    public static string CollisionXml(Options o, int material)
    {
        float r = o.Diameter * 0.5f, t = o.Thickness + o.Relief;
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
