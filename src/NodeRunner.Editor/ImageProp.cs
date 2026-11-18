using System.Globalization;
using System.Numerics;
using System.Text;

namespace NodeRunner.Editor;

// A plaque made from an image: round (a disc, for a round badge) or a rectangle of the image's proportions, with the
// image on its front (and back), its edge coloured by the image's rim. In the prop's space: the back face on y = 0 (the
// pivot at its centre, to hang it on a wall), the front towards -y, z up. Written in the XML forms of Sollumz (YdrXml,
// YtypXml), the texture embedded in the model.
public static class ImageProp
{
    // Width: the diameter of a round plaque. Aspect: height / width of a rectangle. Relief: how far the raised parts
    // stand out of the face (m), 0 for a flat face; RaiseDark: the image's darker parts raised instead of its brighter
    // ones.
    public readonly record struct Options(string Name, float Width, float Thickness, bool BackImage, float Relief = 0,
        bool RaiseDark = false, bool Round = true, float Aspect = 1);

    public const float MinWidth = 0.05f, MaxWidth = 30f;
    public const float MinThickness = 0.002f, MaxThickness = 1f;
    public const float MaxRelief = 0.5f;
    public const float MinAspect = 0.125f, MaxAspect = 8f;

    // Where the face reads the texture, from its centre (half the image across): the image fills it, the face stops
    // just inside its edge (no edge pixels), the rim is sampled a little further in (the coloured ring of a badge).
    public const float FaceRadius = 0.495f;
    public const float RimRadius = 0.475f;

    private const int Sides = 64;
    private const int CollisionSides = 16;

    // A face in relief is a grid of points about as far apart each way, some 20 000 of them (one model with 16-bit
    // indices): 80 rings on a disc, or a grid of up to ReliefPoints on a rectangle.
    private const int ReliefRings = 80;
    private const int ReliefPoints = 24_000;

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

    public static float Height(Options o) => o.Round ? o.Width : o.Width * Math.Clamp(o.Aspect, MinAspect, MaxAspect);

    // A point of the face (from -1 to 1 across it, x to the right and y up) on the image.
    private static Vector2 Uv(Vector2 p, float radius, bool mirrored) =>
        new(0.5f + (mirrored ? -1 : 1) * radius * p.X, 0.5f - radius * p.Y);

    // The model. height: from a point of the image (texture coordinates) to how raised it is, 0 to 1; null (or no
    // relief) for a flat face.
    public static Mesh Build(Options o, Func<Vector2, float>? height = null)
    {
        bool relief = height is not null && o.Relief > 0;
        float halfWidth = o.Width * 0.5f, halfHeight = Height(o) * 0.5f, t = o.Thickness;
        var mesh = new Mesh();

        // Front, seen from -y: x to the right, z up, as the image.
        var front = new Vector3(0, -1, 0);
        int Front(Vector2 p)
        {
            var uv = Uv(p, FaceRadius, false);
            float y = -t - (relief ? o.Relief * Math.Clamp(height!(uv), 0f, 1f) : 0f);
            return mesh.Add(new(new(p.X * halfWidth, y, p.Y * halfHeight), front, uv));
        }
        var outline = o.Round ? RoundFace(mesh, Front, relief) : RectangleFace(mesh, Front, relief, Height(o) / o.Width);
        if (relief)
            SmoothNormals(mesh, mesh.Vertices.Count, front);
        Vector2 Point(int i) => new(mesh.Vertices[i].Position.X / halfWidth, mesh.Vertices[i].Position.Z / halfHeight);

        // Back, seen from +y: the image the right way round, or the rim's colour.
        var back = new Vector3(0, 1, 0);
        var rimTop = Uv(Vector2.UnitY, RimRadius, false);
        int center = mesh.Add(new(Vector3.Zero, back, o.BackImage ? new(0.5f, 0.5f) : rimTop));
        foreach (int i in outline)
            mesh.Add(new(mesh.Vertices[i].Position with { Y = 0 }, back, o.BackImage ? Uv(Point(i), FaceRadius, true) : rimTop));
        for (int i = 0; i < outline.Count; i++)
            mesh.Triangle(center, center + 1 + i, center + 1 + (i + 1) % outline.Count, back);

        // Edge, coloured by the rim at the same place, from the front's outline (raised or not) to the back: smooth
        // round a disc, one flat side after another on a rectangle.
        for (int i = 0; i < outline.Count; i++)
        {
            int i0 = outline[i], i1 = outline[(i + 1) % outline.Count];
            var p0 = mesh.Vertices[i0].Position;
            var p1 = mesh.Vertices[i1].Position;
            var side = Vector3.Normalize(new Vector3(p1.Z - p0.Z, 0, p0.X - p1.X));
            if (Vector3.Dot(side, (p0 + p1) with { Y = 0 }) < 0)
                side = -side;
            var n0 = o.Round ? Vector3.Normalize(p0 with { Y = 0 }) : side;
            var n1 = o.Round ? Vector3.Normalize(p1 with { Y = 0 }) : side;
            int a = mesh.Add(new(p0, n0, Uv(Point(i0), RimRadius, false)));
            int b = mesh.Add(new(p1, n1, Uv(Point(i1), RimRadius, false)));
            int c = mesh.Add(new(p1 with { Y = 0 }, n1, Uv(Point(i1), RimRadius, false)));
            int d = mesh.Add(new(p0 with { Y = 0 }, n0, Uv(Point(i0), RimRadius, false)));
            mesh.Triangle(a, b, c, side);
            mesh.Triangle(a, c, d, side);
        }
        return mesh;
    }

    // A disc of rings from the centre out; returns its outer ring.
    private static List<int> RoundFace(Mesh mesh, Func<Vector2, int> point, bool relief)
    {
        int rings = relief ? ReliefRings : 1;
        int Count(int k) => k == 0 ? 1 : rings == 1 ? Sides : Math.Max(8, (int)MathF.Round(MathF.Tau * k));
        var starts = new int[rings + 1];
        for (int k = 0; k <= rings; k++)
        {
            starts[k] = mesh.Vertices.Count;
            float part = (float)k / rings;
            for (int i = 0, n = Count(k); i < n; i++)
                point(part * new Vector2(MathF.Cos(MathF.Tau * i / n), MathF.Sin(MathF.Tau * i / n)));
        }
        var front = new Vector3(0, -1, 0);
        for (int k = 0; k < rings; k++)
            Stitch(mesh, starts[k], Count(k), starts[k + 1], Count(k + 1), front);
        return [.. Enumerable.Range(starts[rings], Count(rings))];
    }

    // A grid of nearly square cells (aspect: height / width); returns its border, going round.
    private static List<int> RectangleFace(Mesh mesh, Func<Vector2, int> point, bool relief, float aspect)
    {
        int across = 1, up = 1;
        if (relief)
        {
            across = Math.Max(1, (int)MathF.Round(MathF.Sqrt(ReliefPoints / aspect)));
            up = Math.Max(1, (int)MathF.Round(across * aspect));
        }
        int start = mesh.Vertices.Count;
        for (int j = 0; j <= up; j++)
        {
            for (int i = 0; i <= across; i++)
                point(new(-1 + 2f * i / across, -1 + 2f * j / up));
        }
        int At(int i, int j) => start + j * (across + 1) + i;
        var front = new Vector3(0, -1, 0);
        for (int j = 0; j < up; j++)
        {
            for (int i = 0; i < across; i++)
            {
                mesh.Triangle(At(i, j), At(i + 1, j), At(i + 1, j + 1), front);
                mesh.Triangle(At(i, j), At(i + 1, j + 1), At(i, j + 1), front);
            }
        }
        var border = new List<int>();
        for (int i = 0; i < across; i++)
            border.Add(At(i, 0));
        for (int j = 0; j < up; j++)
            border.Add(At(across, j));
        for (int i = across; i > 0; i--)
            border.Add(At(i, up));
        for (int j = up; j > 0; j--)
            border.Add(At(0, j));
        return border;
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
        float x = o.Width * 0.5f, z = Height(o) * 0.5f;
        return (new(-x, -o.Thickness - o.Relief, -z), new(x, 0, z));
    }

    private static float Size(Options o) => Math.Max(o.Width, Height(o));

    // LOD distance of the archetype: further for a bigger plaque.
    public static float LodDistance(Options o) => Math.Clamp(Size(o) * 100f, 100f, 500f);

    // Up to where the relief is drawn; the flat plaque further away.
    public static float ReliefDistance(Options o) => Math.Clamp(Size(o) * 25f, 10f, 100f);

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

    // The collision: the same shape (a disc with fewer sides), closed (front, back, edge), as thick as the relief's top.
    public static string CollisionXml(Options o, int material)
    {
        float halfWidth = o.Width * 0.5f, halfHeight = Height(o) * 0.5f, t = o.Thickness + o.Relief;
        Vector2[] corners = o.Round
            ? [.. Enumerable.Range(0, CollisionSides).Select(i => new Vector2(MathF.Cos(MathF.Tau * i / CollisionSides), MathF.Sin(MathF.Tau * i / CollisionSides)))]
            : [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        var vertices = new StringBuilder();
        void Vertex(float x, float y, float z) => vertices.Append(F(x)).Append(", ").Append(F(y)).Append(", ").Append(F(z)).Append('\n');
        // 0: front centre, 1: back centre, then front and back of each side.
        Vertex(0, -t, 0);
        Vertex(0, 0, 0);
        foreach (var c in corners)
        {
            Vertex(c.X * halfWidth, -t, c.Y * halfHeight);
            Vertex(c.X * halfWidth, 0, c.Y * halfHeight);
        }
        var polygons = new StringBuilder();
        void Triangle(int a, int b, int c) => polygons.Append($"<Triangle m=\"0\" v1=\"{a}\" v2=\"{b}\" v3=\"{c}\" f1=\"0\" f2=\"0\" f3=\"0\" />\n");
        for (int i = 0; i < corners.Length; i++)
        {
            int f0 = 2 + i * 2, b0 = f0 + 1;
            int f1 = 2 + (i + 1) % corners.Length * 2, b1 = f1 + 1;
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
