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

// Game files written out as XML must read back into the same files.
public class XmlExportTests
{
    private static readonly ImageProp.Options Plaque = new("export_test", 1.2f, 0.04f, BackImage: true);

    private static byte[] Image(string name)
    {
        int size = 16, levels = 3;
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(i % 251), 90, 40, 255);
        return DdsFile.Write(new TextureData
        {
            NameHash = Joaat.Hash(name), Name = name, Width = size, Height = size, MipLevels = levels,
            Format = TextureFormat.Bc1, Data = TextureCodec.Encode(rgba, size, size, TextureFormat.Bc1, levels),
        });
    }

    private static Func<string, byte[]?> Files(XmlConversion conversion)
    {
        var files = conversion.Files.ToDictionary(f => f.Path, f => f.Data, StringComparer.OrdinalIgnoreCase);
        return path => files.GetValueOrDefault(path);
    }

    [Fact]
    public void Model_reads_back_the_same()
    {
        var image = Image(Plaque.Name);
        var first = YdrXml.Convert(new XmlExport(Plaque.Name + ".ydr.xml", ImageProp.DrawableXml(Plaque, Plaque.Name, material: 3),
            f => f.EndsWith(".dds", StringComparison.Ordinal) ? image : null));
        var (header, content) = ResourceFile.Read(first);
        var names = new ShaderNameCatalog();
        var exported = YdrXml.Export(Plaque.Name + ".ydr", content, header, names);

        Assert.Equal(Plaque.Name + ".ydr.xml", exported.FileName);
        var (path, dds) = Assert.Single(exported.Files);
        Assert.Equal($"{Plaque.Name}/{Plaque.Name}.dds", path);
        Assert.Equal(image, dds);
        Assert.Contains("<Bounds type=", exported.Text);

        var second = YdrXml.Convert(new XmlExport(exported.FileName, exported.Text, Files(exported)));
        var (header2, content2) = ResourceFile.Read(second);
        Assert.Equal(exported.Text, YdrXml.Export(Plaque.Name + ".ydr", content2, header2, names).Text);
    }

    [Fact]
    public void Shader_names_come_from_the_catalogue()
    {
        var image = Image(Plaque.Name);
        var (header, content) = ResourceFile.Read(YdrXml.Convert(new XmlExport(Plaque.Name + ".ydr.xml",
            ImageProp.DrawableXml(Plaque, Plaque.Name, material: 3), f => f.EndsWith(".dds", StringComparison.Ordinal) ? image : null)));

        string unnamed = YdrXml.Export("a.ydr", content, header, new ShaderNameCatalog()).Text;
        Assert.Contains("<FileName>hash_", unnamed);

        var names = new ShaderNameCatalog();
        foreach (string name in new[] { "default", "default.sps", "normal_spec", "normal_spec.sps", "spec", "spec.sps", "DiffuseSampler" })
            names.Add(name);
        string named = YdrXml.Export("a.ydr", content, header, names).Text;
        Assert.Contains("<Item name=\"DiffuseSampler\" type=\"Texture\">", named);
        Assert.DoesNotContain("<Name>hash_", named);
    }

    [Fact]
    public void Collision_reads_back_the_same()
    {
        var (_, content) = ResourceFile.Read(YdrXml.Convert(new XmlExport(Plaque.Name + ".ydr.xml",
            ImageProp.DrawableXml(Plaque, Plaque.Name, material: 3), _ => Image(Plaque.Name))));
        var bounds = System.Xml.Linq.XDocument.Parse(YdrXml.Export("a.ydr", content, ResourceFile.Read(YdrXml.Convert(
            new XmlExport(Plaque.Name + ".ydr.xml", ImageProp.DrawableXml(Plaque, Plaque.Name, material: 3), _ => Image(Plaque.Name)))).Header,
            new ShaderNameCatalog()).Text).Root!.Element("Bounds")!;
        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<BoundsFile>{bounds}</BoundsFile>";

        var (header, file) = ResourceFile.Read(YbnXml.Convert(xml));
        var exported = YbnXml.Export("a.ybn", file, header);
        var (header2, file2) = ResourceFile.Read(YbnXml.Convert(exported.Text));
        Assert.Equal(exported.Text, YbnXml.Export("a.ybn", file2, header2).Text);
        Assert.Contains("<Polygons>", exported.Text);
    }

    [Fact]
    public void Texture_dictionary_reads_back_the_same()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TextureDictionary>
             <Item>
              <Name>Sign_Diffuse</Name>
              <Unk32 value="128" />
              <Usage>DIFFUSE</Usage>
              <UsageFlags>NOT_HALF, UNK24</UsageFlags>
              <ExtraFlags value="0" />
              <FileName>Sign_Diffuse.dds</FileName>
             </Item>
            </TextureDictionary>
            """;
        var image = Image("Sign_Diffuse");
        var (header, content) = ResourceFile.Read(YtdXml.Convert(new XmlExport("signs.ytd.xml", xml, f => f == "signs/Sign_Diffuse.dds" ? image : null)));

        var exported = YtdXml.Export("signs.ytd", content, header);
        Assert.Contains("<Name>sign_diffuse</Name>", exported.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<UsageFlags>NOT_HALF, UNK24</UsageFlags>", exported.Text);
        Assert.Contains("<Format>D3DFMT_DXT1</Format>", exported.Text);
        Assert.Equal(image, Assert.Single(exported.Files).Data);

        var (header2, content2) = ResourceFile.Read(YtdXml.Convert(new XmlExport(exported.FileName, exported.Text, Files(exported))));
        Assert.Equal(exported.Text, YtdXml.Export("signs.ytd", content2, header2).Text);
    }
}
