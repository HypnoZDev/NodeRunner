using System.Numerics;
using NodeRunner.App.World;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class ImagePropTests
{
    private static readonly ImageProp.Options Plaque = new("badge_test", 1.2f, 0.04f, BackImage: true);

    [Fact]
    public void Every_triangle_faces_outwards()
    {
        var triangles = ImageProp.Triangles(Plaque);
        var middle = new Vector3(0, -Plaque.Thickness / 2, 0);
        for (int i = 0; i < triangles.Count; i += 3)
        {
            var (a, b, c) = (triangles[i].Position, triangles[i + 1].Position, triangles[i + 2].Position);
            var normal = Vector3.Cross(b - a, c - a);
            Assert.True(Vector3.Dot(normal, (a + b + c) / 3 - middle) > 0, $"triangle {i / 3} turned inwards");
            Assert.True(Vector3.Dot(normal, triangles[i].Normal) > 0, $"triangle {i / 3} against its vertex normal");
        }
        var (min, max) = ImageProp.Box(Plaque);
        Assert.All(triangles, v => Assert.Equal(v.Position, Vector3.Clamp(v.Position, min, max)));
    }

    [Fact]
    public void Front_shows_the_image_the_right_way_round()
    {
        var front = ImageProp.Triangles(Plaque).Where(v => v.Normal.Y < -0.9f).ToList();
        // The right of the plaque (seen from the front, -y) is the right of the image; its top the top of the image.
        var right = front.MaxBy(v => v.Position.X);
        var top = front.MaxBy(v => v.Position.Z);
        Assert.True(right.TexCoord.X > 0.9f);
        Assert.True(top.TexCoord.Y < 0.1f);
        var back = ImageProp.Triangles(Plaque).Where(v => v.Normal.Y > 0.9f).ToList();
        // Seen from behind, +x is on the left.
        Assert.True(back.MaxBy(v => v.Position.X).TexCoord.X < 0.1f);
    }

    [Fact]
    public void Model_converts_with_its_texture()
    {
        int size = 64, levels = 5;
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(i % 251), 200, 40, 255);
        var dds = DdsFile.Write(new TextureData
        {
            NameHash = Joaat.Hash(Plaque.Name),
            Name = Plaque.Name,
            Width = size,
            Height = size,
            MipLevels = levels,
            Format = TextureFormat.Bc1,
            Data = TextureCodec.Encode(rgba, size, size, TextureFormat.Bc1, levels),
        });
        var bytes = YdrXml.Convert(new XmlExport(Plaque.Name + ".ydr.xml", ImageProp.DrawableXml(Plaque, Plaque.Name, material: 3),
            f => f.EndsWith(".dds", StringComparison.Ordinal) ? dds : null));

        var (header, content) = ResourceFile.Read(bytes);
        var model = DrawableReader.Read(content, header, Plaque.Name, ".ydr").Values.Single();
        Assert.Equal(ImageProp.Triangles(Plaque).Count / 3, model.TriangleCount);
        var texture = Assert.Single(model.EmbeddedTextures.Values);
        Assert.Equal((size, TextureFormat.Bc1), (texture.Width, texture.Format));
        Assert.Equal(Plaque.Name, model.Meshes.Single().DiffuseTextureName);
    }

    [Fact]
    public void Collision_is_a_closed_disc()
    {
        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<BoundsFile>\n{ImageProp.CollisionXml(Plaque, material: 3)}\n</BoundsFile>";
        var (_, content) = ResourceFile.Read(YbnXml.Convert(xml));
        var mesh = YbnReader.Read(content)!;
        Assert.Equal(16 * 4, mesh.Indices.Count / 3);
        Assert.Equal(new Vector3(-0.6f, -0.04f, -0.6f), mesh.BoundsMin, new Close(0.01f));
        Assert.Equal(new Vector3(0.6f, 0, 0.6f), mesh.BoundsMax, new Close(0.01f));
    }

    [Fact]
    public void Transparent_pixels_take_the_rim_colour()
    {
        int size = 64;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                float d = Vector2.Distance(new(x, y), new((size - 1) / 2f)) / size;
                // A gold disc on a transparent square.
                if (d < 0.5f)
                    (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (220, 180, 60, 255);
            }
        }
        ImagePropMaker.Flatten(rgba, size, size, round: true);
        Assert.Equal(new byte[] { 220, 180, 60, 255 }, rgba[..4]);
    }

    // A gold disc (half the image wide) on navy.
    private static byte[] Badge(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool gold = Vector2.Distance(new(x, y), new((size - 1) / 2f)) / size < 0.25f;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = gold ? ((byte)220, (byte)180, (byte)60, (byte)255) : ((byte)20, (byte)30, (byte)70, (byte)255);
            }
        }
        return rgba;
    }

    private static readonly ImageProp.Options Raised = Plaque with { Relief = 0.02f };

    [Fact]
    public void Relief_raises_the_bright_parts()
    {
        var height = ImagePropMaker.Relief(Badge(128), 128, 128, raiseDark: false, round: true);
        Assert.Equal(1f, height(new(0.5f, 0.5f)), 0.01f);
        Assert.Equal(0f, height(new(0.5f, 0.1f)), 0.01f);
        var dark = ImagePropMaker.Relief(Badge(128), 128, 128, raiseDark: true, round: true);
        Assert.Equal(0f, dark(new(0.5f, 0.5f)), 0.01f);

        var mesh = ImageProp.Build(Raised, height);
        float t = Raised.Thickness;
        Assert.Equal(-t - Raised.Relief, mesh.Vertices[0].Position.Y, 0.001f);
        Assert.Equal(-t, mesh.Vertices.Where(v => v.Position.Y < -t / 2).MaxBy(v => v.Position.Z).Position.Y, 0.001f);
        var (min, max) = ImageProp.Box(Raised);
        Assert.All(mesh.Vertices, v => Assert.Equal(v.Position, Vector3.Clamp(v.Position, min, max)));
    }

    [Fact]
    public void Relief_faces_outwards_and_fits_one_model()
    {
        var mesh = ImageProp.Build(Raised, uv => uv.X > 0.5f ? 1 : 0);
        Assert.InRange(mesh.Vertices.Count, 10_000, ushort.MaxValue);
        float t = Raised.Thickness;
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Vertices[mesh.Indices[i]].Position, mesh.Vertices[mesh.Indices[i + 1]].Position, mesh.Vertices[mesh.Indices[i + 2]].Position);
            var normal = Vector3.Cross(b - a, c - a);
            if (a.Y <= -t + 1e-5f && b.Y <= -t + 1e-5f && c.Y <= -t + 1e-5f)
                Assert.True(normal.Y < 0, $"front triangle {i / 3} turned inwards");
            else if (a.Y == 0 && b.Y == 0 && c.Y == 0)
                Assert.True(normal.Y > 0, $"back triangle {i / 3} turned inwards");
            else
                Assert.True(Vector3.Dot(normal, (a + b + c) with { Y = 0 }) > 0, $"edge triangle {i / 3} turned inwards");
        }
        // Every point of the front is used: no hole between the rings.
        var used = mesh.Indices.ToHashSet();
        Assert.Equal(mesh.Vertices.Count, used.Count);
    }

    [Fact]
    public void Relief_model_converts_with_a_flat_one_further_away()
    {
        Func<Vector2, float> height = uv => uv.X > 0.5f ? 1 : 0;
        string xml = ImageProp.DrawableXml(Raised, Raised.Name, material: 3, height);
        Assert.Contains("<DrawableModelsMedium>", xml);
        var bytes = YdrXml.Convert(new XmlExport(Raised.Name + ".ydr.xml", xml,
            f => f.EndsWith(".dds", StringComparison.Ordinal) ? Dds() : null));
        var (header, content) = ResourceFile.Read(bytes);
        var model = DrawableReader.Read(content, header, Raised.Name, ".ydr").Values.Single();
        Assert.Equal(ImageProp.Build(Raised, height).Indices.Count / 3, model.TriangleCount);
    }

    [Fact]
    public void Shape_follows_the_image()
    {
        // A round badge: transparent corners, an opaque rim.
        var badge = new byte[64 * 64 * 4];
        for (int i = 0; i < badge.Length; i += 4)
        {
            int x = i / 4 % 64, y = i / 4 / 64;
            badge[i + 3] = Vector2.Distance(new(x, y), new(31.5f)) < 31 ? (byte)255 : (byte)0;
        }
        Assert.Equal((true, 1f), ImagePropMaker.Shape(badge, 64, 64));
        var square = Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray();
        Assert.Equal((false, 1f), ImagePropMaker.Shape(square, 64, 64));
        var banner = Enumerable.Repeat((byte)255, 200 * 100 * 4).ToArray();
        Assert.Equal((false, 0.5f), ImagePropMaker.Shape(banner, 200, 100));
    }

    [Fact]
    public void Rectangle_keeps_the_image_proportions()
    {
        var banner = Plaque with { Round = false, Aspect = 0.5f, Relief = 0.02f };
        var (min, max) = ImageProp.Box(banner);
        Assert.Equal(new Vector3(-0.6f, -0.06f, -0.3f), min, new Close(0.001f));
        Assert.Equal(new Vector3(0.6f, 0, 0.3f), max, new Close(0.001f));

        foreach (var height in new Func<Vector2, float>?[] { null, uv => uv.X > 0.5f ? 1 : 0 })
        {
            var mesh = ImageProp.Build(banner, height);
            Assert.InRange(mesh.Vertices.Count, 4, ushort.MaxValue);
            Assert.All(mesh.Vertices, v => Assert.Equal(v.Position, Vector3.Clamp(v.Position, min, max)));
            float t = banner.Thickness;
            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                var (a, b, c) = (mesh.Vertices[mesh.Indices[i]].Position, mesh.Vertices[mesh.Indices[i + 1]].Position, mesh.Vertices[mesh.Indices[i + 2]].Position);
                var normal = Vector3.Cross(b - a, c - a);
                if (a.Y <= -t + 1e-5f && b.Y <= -t + 1e-5f && c.Y <= -t + 1e-5f)
                    Assert.True(normal.Y < 0, $"front triangle {i / 3} turned inwards");
                else if (a.Y == 0 && b.Y == 0 && c.Y == 0)
                    Assert.True(normal.Y > 0, $"back triangle {i / 3} turned inwards");
                else
                    Assert.True(Vector3.Dot(normal, (a + b + c) with { Y = 0 }) > 0, $"edge triangle {i / 3} turned inwards");
            }
        }
        // The image's corner on the plaque's corner: top left of the image, seen from the front.
        var corner = ImageProp.Build(banner).Vertices.First(v => v.Normal.Y < -0.9f && v.Position.X < 0 && v.Position.Z > 0);
        Assert.True(corner.TexCoord.X < 0.01f && corner.TexCoord.Y < 0.01f);

        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<BoundsFile>\n{ImageProp.CollisionXml(banner, material: 3)}\n</BoundsFile>";
        var (_, content) = ResourceFile.Read(YbnXml.Convert(xml));
        var collision = YbnReader.Read(content)!;
        Assert.Equal(4 * 4, collision.Indices.Count / 3);
        Assert.Equal(new Vector3(0.6f, 0, 0.3f), collision.BoundsMax, new Close(0.01f));
    }

    private static byte[] Dds() => DdsFile.Write(new TextureData
    {
        NameHash = Joaat.Hash(Plaque.Name),
        Name = Plaque.Name,
        Width = 4,
        Height = 4,
        MipLevels = 1,
        Format = TextureFormat.Bc1,
        Data = TextureCodec.Encode(new byte[4 * 4 * 4], 4, 4, TextureFormat.Bc1, 1),
    });

    [Theory]
    [InlineData("Secret Service.png", "secret_service")]
    [InlineData("2024-logo!.jpg", "prop_2024_logo")]
    [InlineData("___.png", "prop")]
    public void Names_come_from_the_file(string file, string name) => Assert.Equal(name, ImageProp.SuggestName(file));

    private sealed class Close(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
