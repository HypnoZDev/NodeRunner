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
        ImagePropMaker.Flatten(rgba, size);
        Assert.Equal(new byte[] { 220, 180, 60, 255 }, rgba[..4]);
    }

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
