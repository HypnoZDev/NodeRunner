using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class YbnXmlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr_ybnxml_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // A 2 m cube centred on (10, 20, 1) made of 12 triangles (concrete), then a box child moved by its transform.
    private static string CubeXml(string flags = "MAP_WEAPON, MAP_DYNAMIC, MAP_ANIMAL, MAP_COVER, MAP_VEHICLE")
    {
        var vertices = string.Join("\n", from z in new[] { -1, 1 } from y in new[] { -1, 1 } from x in new[] { -1, 1 } select $"{x}, {y}, {z}");
        int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        var triangles = string.Join("\n", faces.SelectMany(f => new[]
        {
            $"<Triangle m=\"0\" v1=\"{f[0]}\" v2=\"{f[1]}\" v3=\"{f[2]}\" f1=\"0\" f2=\"0\" f3=\"0\" />",
            $"<Triangle m=\"0\" v1=\"{f[0]}\" v2=\"{f[2]}\" v3=\"{f[3]}\" f1=\"0\" f2=\"0\" f3=\"0\" />",
        }));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <BoundsFile>
             <Bounds type="Composite">
              <Margin value="0.005" />
              <Children>
               <Item type="GeometryBVH">
                <Margin value="0.005" />
                <CompositeTransform>
                 1, 0, 0, 0
                 0, 1, 0, 0
                 0, 0, 1, 0
                 0, 0, 0, 1
                </CompositeTransform>
                <CompositeFlags1>{flags}</CompositeFlags1>
                <CompositeFlags2>PED, RAGDOLL</CompositeFlags2>
                <GeometryCenter x="10" y="20" z="1" />
                <Materials>
                 <Item>
                  <Type value="1" />
                  <Flags>FLAG_STAIRS, FLAG_NO_DECAL</Flags>
                 </Item>
                </Materials>
                <Vertices>
            {vertices}
                </Vertices>
                <Polygons>
            {triangles}
                </Polygons>
               </Item>
               <Item type="None" />
               <Item type="Box">
                <BoxMin x="-0.5" y="-0.5" z="-0.5" />
                <BoxMax x="0.5" y="0.5" z="0.5" />
                <MaterialIndex value="3" />
                <CompositeTransform>1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 5, 1</CompositeTransform>
               </Item>
              </Children>
             </Bounds>
            </BoundsFile>
            """;
    }

    [Fact]
    public void ConvertsAComposite()
    {
        byte[] file = YbnXml.Convert(CubeXml());
        var (header, content) = ResourceFile.Read(file);
        Assert.Equal(43, header.Version);

        var mesh = YbnReader.Read(content)!;
        Assert.Equal(12 + 12, mesh.TriangleCount);
        Assert.Equal(new Vector3(-0.5f, -0.5f, 0), mesh.BoundsMin, new Vec3Comparer(0.001f));
        Assert.Equal(new Vector3(11, 21, 5.5f), mesh.BoundsMax, new Vec3Comparer(0.001f));
        Assert.All(mesh.Materials.Take(12), m => Assert.Equal(1, m));
        Assert.All(mesh.Materials.Skip(12), m => Assert.Equal(3, m));

        // Every cube triangle keeps its 3 vertices on the cube's corners.
        for (int i = 0; i < 12 * 3; i++)
        {
            var v = mesh.Vertices[mesh.Indices[i]] - new Vector3(10, 20, 1);
            Assert.True(MathF.Abs(MathF.Abs(v.X) - 1) < 0.001f && MathF.Abs(MathF.Abs(v.Y) - 1) < 0.001f && MathF.Abs(MathF.Abs(v.Z) - 1) < 0.001f);
        }
    }

    [Fact]
    public void RejectsBadInput()
    {
        Assert.Throws<InvalidDataException>(() => YbnXml.Convert(CubeXml("MAP_WEAPON, NOT_A_FLAG")));
        Assert.Throws<InvalidDataException>(() => YbnXml.Convert(CubeXml().Replace("v3=\"3\"", "v3=\"99\"")));
        Assert.Throws<InvalidDataException>(() => YbnXml.Convert("<Drawable />"));
        Assert.Throws<InvalidDataException>(() => YbnXml.Convert("not xml"));
    }

    [Fact]
    public void ImportConvertsXmlExports()
    {
        string stream = Path.Combine(_root, @"mymap\stream");
        Directory.CreateDirectory(stream);
        File.WriteAllText(Path.Combine(stream, "mymap_col.ybn.xml"), CubeXml());
        File.WriteAllText(Path.Combine(stream, "broken.ybn.xml"), "<BoundsFile>");
        File.WriteAllText(Path.Combine(stream, "prop.ydr.xml"), "<Drawable />");
        File.WriteAllText(Path.Combine(stream, "mymap.ymap"), "ymap");

        var result = MappingImport.Import(Path.Combine(_root, "mymap"), Path.Combine(_root, "projects"), MapVersion.Multiplayer, null,
            new Dictionary<string, Func<XmlExport, byte[]>> { [".ybn"] = e => YbnXml.Convert(e.Text) });

        Assert.Equal(2, result.Copied);
        Assert.Equal(1, result.Converted);
        Assert.Equal(2, result.Ignored.Count);
        Assert.NotNull(YbnReader.Read(ResourceFile.Read(File.ReadAllBytes(Path.Combine(result.Project.StreamFolder, "mymap_col.ybn"))).Content));
        Assert.False(File.Exists(Path.Combine(result.Project.StreamFolder, "prop.ydr")));
        Assert.False(File.Exists(Path.Combine(result.Project.StreamFolder, "broken.ybn")));
    }

    private sealed class Vec3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
