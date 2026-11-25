using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class LodExportTests
{
    [Fact]
    public void Objects_are_linked_to_their_lod_in_the_parent_map()
    {
        var file = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd), new DeletionTests.Item(0x31, -1, LodLevel.OrphanHd));
        var linked = YmapWriter.LinkToParent(new MapFileContent(file, 0), 0x2000, new Dictionary<int, int> { [1] = 4 });
        var map = new YmapReader().Read(linked.Content);
        Assert.Equal(0x2000u, map.ParentHash);
        Assert.Equal(LodLevel.OrphanHd, map.Entities[0].LodLevel);
        Assert.False(map.Entities[0].LodInParentMap);
        Assert.Equal(LodLevel.Hd, map.Entities[1].LodLevel);
        Assert.Equal(4, map.Entities[1].ParentIndex);
        Assert.True(map.Entities[1].LodInParentMap);
    }

    [Fact]
    public void Lods_know_their_children()
    {
        var file = DeletionTests.BuildYmap(0x2000, 0, new DeletionTests.Item(0x40, -1, LodLevel.Lod));
        var written = YmapWriter.SetChildren(new MapFileContent(file, 0), new Dictionary<int, (int, float)> { [0] = (1, 150f) });
        var lod = new YmapReader().Read(written.Content).Entities[0];
        Assert.Equal(1, lod.ChildCount);
        Assert.Equal(150f, lod.ChildLodDistance);
    }

    // A drawable with a 20 x 20 m grid of 400 cells, a tiny triangle, a medium level and a collision.
    private static string Drawable()
    {
        static string F(float v) => v.ToString(CultureInfo.InvariantCulture);
        var rows = new StringBuilder();
        for (int y = 0; y <= 20; y++)
            for (int x = 0; x <= 20; x++)
                rows.Append($"{F(x)} {F(y)} 0   0 0 1   255 255 255 255   {F(x / 20f)} {F(y / 20f)}\n");
        var indices = new List<int>();
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                int a = y * 21 + x, b = a + 1, c = a + 22, d = a + 21;
                indices.AddRange([a, b, c, a, c, d]);
            }
        }
        string Geometry(string data, string index) => $"""
            <Item>
             <ShaderIndex value="0" />
             <VertexBuffer>
              <Flags value="0" />
              <Layout type="GTAV1"><Position /><Normal /><Colour0 /><TexCoord0 /></Layout>
              <Data>
            {data}</Data>
             </VertexBuffer>
             <IndexBuffer><Data>{index}</Data></IndexBuffer>
            </Item>
            """;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Drawable>
             <Name>slab</Name>
             <BoundingBoxMin x="0" y="0" z="0" />
             <BoundingBoxMax x="20" y="20" z="1" />
             <LodDistHigh value="100" />
             <LodDistMed value="300" />
             <FlagsHigh value="1" />
             <FlagsMed value="1" />
             <DrawableModelsHigh>
              <Item>
               <Geometries>
                {Geometry(rows.ToString(), string.Join(' ', indices))}
                {Geometry("0 0 0.5   0 0 1   255 255 255 255   0 0\n0.1 0 0.5   0 0 1   255 255 255 255   0 0\n0 0.1 0.5   0 0 1   255 255 255 255   0 0\n", "0 1 2")}
               </Geometries>
              </Item>
             </DrawableModelsHigh>
             <DrawableModelsMedium><Item /></DrawableModelsMedium>
             <Bounds type="Composite" />
            </Drawable>
            """;
    }

    [Fact]
    public void The_lod_model_is_lighter_and_has_no_collision_or_lower_levels()
    {
        var lod = XDocument.Parse(LodModel.DrawableXml(Drawable(), "slab_nrlod")).Root!;
        Assert.Equal("slab_nrlod", lod.Element("Name")!.Value);
        Assert.Null(lod.Element("Bounds"));
        Assert.Null(lod.Element("DrawableModelsMedium"));
        Assert.Equal("9998", (string?)lod.Element("LodDistHigh")!.Attribute("value"));
        Assert.Equal("0", (string?)lod.Element("FlagsMed")!.Attribute("value"));
        var geometry = Assert.Single(lod.Descendants("Geometries").Elements("Item")); // The tiny triangle is left out.
        int indices = geometry.Element("IndexBuffer")!.Element("Data")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.InRange(indices / 3, 2, 200);
        int vertices = geometry.Element("VertexBuffer")!.Element("Data")!.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        Assert.True(vertices < 441);
    }

    [Fact]
    public void Only_big_objects_get_a_lod()
    {
        Assert.False(LodModel.Wanted(new Archetype { NameHash = 1, BoundsMax = new(2f, 1f, 1f), AssetType = ArchetypeAssetType.Drawable }));
        Assert.True(LodModel.Wanted(new Archetype { NameHash = 1, BoundsMax = new(20f, 10f, 8f), AssetType = ArchetypeAssetType.Drawable }));
        Assert.False(LodModel.Wanted(new Archetype { NameHash = 1, BoundsMax = new(20f, 10f, 8f), Kind = ArchetypeKind.Interior }));
        Assert.Equal("my_house_nrlod", LodModel.Name("My_House", 5));
        Assert.Equal("nrlod_0000abcd", LodModel.Name(null, 0xABCD));
        Assert.True(LodModel.LodDistance(200f) > 200f);
    }
}
