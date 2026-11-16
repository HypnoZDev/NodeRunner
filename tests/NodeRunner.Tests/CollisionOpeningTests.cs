using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class CollisionOpeningTests
{
    private static readonly Opening Door = new(new Vector3(0, 0, 0), -Vector3.UnitY, 1.2f, 2.3f);

    private static string File(string vertices, string polygons) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <BoundsFile>
         <Bounds type="Composite">
          <Children>
           <Item type="GeometryBVH">
            <CompositeTransform>1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1</CompositeTransform>
            <CompositeFlags1>MAP_WEAPON, MAP_DYNAMIC, MAP_ANIMAL, MAP_COVER, MAP_VEHICLE</CompositeFlags1>
            <CompositeFlags2>PED, RAGDOLL</CompositeFlags2>
            <GeometryCenter x="0" y="0" z="0" />
            <Materials><Item><Type value="4" /></Item></Materials>
            <Vertices>
        {vertices}
            </Vertices>
            <Polygons>
        {polygons}
            </Polygons>
           </Item>
          </Children>
         </Bounds>
        </BoundsFile>
        """;

    private static CollisionMesh Cut(string xml, out int polygonsBefore, out int polygonsAfter)
    {
        var (header, content) = ResourceFile.Read(YbnXml.Convert(xml));
        polygonsBefore = YbnReader.Read(content)!.Polygons.Distinct().Count();
        var (cut, flags) = YbnReader.Edit(content, header.SystemFlags, new HashSet<int>(), new Dictionary<int, byte>(), null, [Door]);
        var (_, read) = ResourceFile.Read(ResourceFile.Write(header with { SystemFlags = flags }, cut));
        var mesh = YbnReader.Read(read)!;
        polygonsAfter = mesh.Polygons.Distinct().Count();
        return mesh;
    }

    // Facing area left in the doorway.
    private static float LeftInHole(CollisionMesh mesh)
    {
        float area = 0;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var a = Door.Local(mesh.Vertices[mesh.Indices[t * 3]]);
            var b = Door.Local(mesh.Vertices[mesh.Indices[t * 3 + 1]]);
            var c = Door.Local(mesh.Vertices[mesh.Indices[t * 3 + 2]]);
            var n = Vector3.Cross(b - a, c - a);
            var center = (a + b + c) / 3;
            if (n.Length() > 1e-6f && MathF.Abs(n.Z) / n.Length() > 0.25f
                && MathF.Abs(center.X) < 0.55f && center.Y > 0.05f && center.Y < 2.25f && MathF.Abs(center.Z) < 1f)
                area += n.Length() / 2;
        }
        return area;
    }

    [Fact]
    public void Wall_with_two_faces_gets_a_doorway_with_sides()
    {
        // Front face y = 0 and back face y = 0.3, 4 m wide, 3 m high.
        string vertices = "-2, 0, 0\n2, 0, 0\n2, 0, 3\n-2, 0, 3\n-2, 0.3, 0\n2, 0.3, 0\n2, 0.3, 3\n-2, 0.3, 3";
        string polygons = """
            <Triangle m="0" v1="0" v2="1" v3="2" f1="0" f2="0" f3="0" />
            <Triangle m="0" v1="0" v2="2" v3="3" f1="0" f2="0" f3="0" />
            <Triangle m="0" v1="4" v2="6" v3="5" f1="0" f2="0" f3="0" />
            <Triangle m="0" v1="4" v2="7" v3="6" f1="0" f2="0" f3="0" />
            """;
        var mesh = Cut(File(vertices, polygons), out _, out _);

        Assert.Equal(0f, LeftInHole(mesh), 3);
        // The sides of the doorway face into it.
        int sides = 0;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var a = Door.Local(mesh.Vertices[mesh.Indices[t * 3]]);
            var b = Door.Local(mesh.Vertices[mesh.Indices[t * 3 + 1]]);
            var c = Door.Local(mesh.Vertices[mesh.Indices[t * 3 + 2]]);
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var center = (a + b + c) / 3;
            if (MathF.Abs(MathF.Abs(center.X) - 0.6f) < 0.01f && MathF.Abs(n.X) > 0.9f)
            {
                sides++;
                Assert.True(n.X * center.X < 0, "side of the doorway turned away from it");
            }
        }
        Assert.Equal(4, sides);
        Assert.All(mesh.Materials, m => Assert.Equal(4, m));
    }

    [Fact]
    public void Box_wall_is_split_around_the_doorway()
    {
        // A box 4 m wide, 0.3 m thick and 3 m high centred on (0, 0.15, 1.5): stored corners c+X+Y+Z, c+X-Y-Z, c-X+Y-Z, c-X-Y+Z.
        string vertices = "2, 0.3, 3\n2, 0, 0\n-2, 0.3, 0\n-2, 0, 3";
        string polygons = """<Box m="0" v1="0" v2="1" v3="2" v4="3" />""";
        var mesh = Cut(File(vertices, polygons), out int before, out int after);

        Assert.Equal(1, before);
        Assert.Equal(3, after); // left, right, above
        Assert.Equal(0f, LeftInHole(mesh), 3);
        // The pieces still reach the ends and the top of the wall.
        Assert.Equal(-2f, mesh.Vertices.Min(v => v.X), 2);
        Assert.Equal(2f, mesh.Vertices.Max(v => v.X), 2);
        Assert.Equal(3f, mesh.Vertices.Max(v => v.Z), 2);
    }

    [Fact]
    public void Rail_across_the_doorway_keeps_both_ends()
    {
        string vertices = "-3, -0.5, 1\n3, -0.5, 1";
        string polygons = """<Capsule m="0" v1="0" v2="1" radius="0.05" />""";
        var mesh = Cut(File(vertices, polygons), out _, out int after);

        Assert.Equal(2, after);
        Assert.DoesNotContain(mesh.Vertices, v => MathF.Abs(v.X) < 0.6f && MathF.Abs(v.Y + 0.5f) < 0.06f && MathF.Abs(v.Z - 1) < 0.06f);
    }

    [Fact]
    public void Openings_are_kept_in_the_project()
    {
        var edits = new CollisionEdits();
        var opening = CollisionOpening.Of(Door);
        edits.AddOpening("x64/levels/gta5/a.ybn", opening);

        var again = CollisionEdits.FromJson(edits.ToJson());

        var kept = Assert.Single(again.For("x64/levels/gta5/a.ybn")!.Openings);
        Assert.Equal(Door, kept.Value);
        edits.RemoveOpening("x64/levels/gta5/a.ybn", opening);
        Assert.Null(edits.For("x64/levels/gta5/a.ybn"));
    }
}
