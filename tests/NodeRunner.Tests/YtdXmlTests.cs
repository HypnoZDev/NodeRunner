using System.Buffers.Binary;
using System.IO.Compression;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class YtdXmlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr_ytdxml_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // A .dds image: fourCC ("DXT1"...), "DX10" + dxgi format, or 32-bit A8R8G8B8 masks when fourCc is null.
    private static byte[] Dds(int width, int height, int levels, string? fourCc, int pixelBytes, uint dxgi = 0)
    {
        bool dx10 = fourCc == "DX10";
        var dds = new byte[128 + (dx10 ? 20 : 0) + pixelBytes];
        var s = dds.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x1007 | (levels > 1 ? 0x20000u : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)levels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 32);
        if (fourCc is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(s[84..], (uint)(fourCc[0] | fourCc[1] << 8 | fourCc[2] << 16 | fourCc[3] << 24));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0x41);
            BinaryPrimitives.WriteUInt32LittleEndian(s[88..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(s[92..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(s[96..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(s[100..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(s[104..], 0xFF000000);
        }
        if (dx10)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[128..], dxgi);
            BinaryPrimitives.WriteUInt32LittleEndian(s[132..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(s[140..], 1);
        }
        int start = dds.Length - pixelBytes;
        for (int i = 0; i < pixelBytes; i++)
            dds[start + i] = (byte)(i * 7 + 3);
        return dds;
    }

    private static string Xml(params string[] names) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <TextureDictionary>
        {string.Concat(names.Select(n => $"""
         <Item>
          <Name>{n}</Name>
          <Unk32 value="128" />
          <Usage>DIFFUSE</Usage>
          <UsageFlags>NOT_HALF, UNK24</UsageFlags>
          <ExtraFlags value="1" />
          <Width value="16" />
          <Height value="8" />
          <MipLevels value="2" />
          <Format>D3DFMT_DXT1</Format>
          <FileName>{n}.dds</FileName>
         </Item>
        """))}
        </TextureDictionary>
        """;

    private static Dictionary<string, byte[]> Images() => new(StringComparer.OrdinalIgnoreCase)
    {
        // 16 x 8 DXT1, 2 levels: 4 x 2 blocks + 2 x 1 blocks of 8 bytes.
        ["mymap_txd/Wall_Diffuse.dds"] = Dds(16, 8, 2, "DXT1", 64 + 16),
        ["mymap_txd/sign.dds"] = Dds(4, 4, 1, null, 64),
        ["mymap_txd/glass.dds"] = Dds(8, 4, 1, "DX10", 32, dxgi: 98),
    };

    [Fact]
    public void ConvertsADictionary()
    {
        var images = Images();
        var export = new XmlExport("mymap_txd.ytd.xml", Xml("Wall_Diffuse", "sign", "glass"), path => images.GetValueOrDefault(path));
        var (header, content) = ResourceFile.Read(YtdXml.Convert(export));
        Assert.Equal(13, header.Version);

        var textures = YtdReader.Read(content, header).Values.ToDictionary(t => t.Name);
        Assert.Equal(3, textures.Count);
        var wall = textures["wall_diffuse"];
        Assert.Equal((16, 8, 2, TextureFormat.Bc1), (wall.Width, wall.Height, wall.MipLevels, wall.Format));
        Assert.Equal(images["mymap_txd/Wall_Diffuse.dds"].AsSpan(128).ToArray(), wall.Data);
        Assert.Equal(TextureFormat.B8G8R8A8, textures["sign"].Format);
        Assert.Equal(images["mymap_txd/sign.dds"].AsSpan(128).ToArray(), textures["sign"].Data);
        Assert.Equal(TextureFormat.Bc7, textures["glass"].Format);
        Assert.Equal(images["mymap_txd/glass.dds"].AsSpan(148).ToArray(), textures["glass"].Data);
    }

    [Fact]
    public void RejectsBadInput()
    {
        var images = Images();
        XmlExport Export(string xml) => new("mymap_txd.ytd.xml", xml, path => images.GetValueOrDefault(path));
        Assert.Throws<InvalidDataException>(() => YtdXml.Convert(Export(Xml("missing"))));
        Assert.Throws<InvalidDataException>(() => YtdXml.Convert(Export(Xml("sign", "SIGN"))));
        Assert.Throws<InvalidDataException>(() => YtdXml.Convert(Export(Xml("sign").Replace("DIFFUSE", "NOT_A_USAGE"))));
        Assert.Throws<InvalidDataException>(() => YtdXml.Convert(Export("<Drawable />")));
        images["mymap_txd/sign.dds"] = Dds(4, 4, 1, "DXT9", 64);
        Assert.Throws<InvalidDataException>(() => YtdXml.Convert(Export(Xml("sign"))));
        images["mymap_txd/sign.dds"] = Dds(64, 64, 1, "DXT1", 100);
        Assert.Throws<InvalidDataException>(() => YtdXml.Convert(Export(Xml("sign"))));
    }

    private static readonly Dictionary<string, Func<XmlExport, byte[]>> Converters = new() { [".ytd"] = YtdXml.Convert };

    [Fact]
    public void ImportReadsImagesFromAFolder()
    {
        string stream = Path.Combine(_root, @"mymap\stream");
        Directory.CreateDirectory(stream);
        File.WriteAllText(Path.Combine(stream, "mymap_txd.ytd.xml"), Xml("Wall_Diffuse", "sign"));
        foreach (var (path, bytes) in Images())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(stream, path))!);
            File.WriteAllBytes(Path.Combine(stream, path), bytes);
        }

        var result = MappingImport.Import(Path.Combine(_root, "mymap"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null, Converters);
        Assert.Equal(1, result.Converted);
        Assert.Empty(result.Ignored);
        var (header, content) = ResourceFile.Read(File.ReadAllBytes(Path.Combine(result.Project.StreamFolder, "mymap_txd.ytd")));
        Assert.Equal(2, YtdReader.Read(content, header).Count);
        Assert.False(Directory.Exists(Path.Combine(result.Project.StreamFolder, "mymap_txd")));
    }

    [Fact]
    public void ImportReadsImagesFromAZip()
    {
        Directory.CreateDirectory(_root);
        string zipPath = Path.Combine(_root, "mymap.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
            Add("mymap/stream/mymap_txd.ytd.xml", System.Text.Encoding.UTF8.GetBytes(Xml("Wall_Diffuse")));
            Add("mymap/stream/other.ytd.xml", System.Text.Encoding.UTF8.GetBytes(Xml("glass")));
            foreach (var (path, bytes) in Images())
                Add("mymap/stream/" + path, bytes);
        }

        var result = MappingImport.Import(zipPath, Path.Combine(_root, "projects"), MapVersion.Multiplayer, null, Converters);
        Assert.Equal(1, result.Converted);
        Assert.Single(result.Ignored); // other.ytd.xml: its images are in "other/", not there
        Assert.True(File.Exists(Path.Combine(result.Project.StreamFolder, "mymap_txd.ytd")));
    }
}
