using System.Numerics;
using System.Text;
using NodeRunner.Data.Water;

namespace NodeRunner.Tests;

public class WaterTests
{
    private const string Xml = """
        <?xml version="1.0" encoding = "UTF-8"?>
        <WaterData>
          <WaterQuads>
            <Item>
              <minX value="-1592" />
              <maxX value="-1304" />
              <minY value="-1744" />
              <maxY value="-1624" />
              <Type value="0" />
              <IsInvisible value="false" />
              <z value="0.0" />
              <a1 value="26" />
            </Item>
            <Item>
              <minX value="10" />
              <maxX value="20" />
              <minY value="30" />
              <maxY value="40" />
              <Type value="2" />
              <IsInvisible value="false" />
              <z value="30.5" />
            </Item>
            <Item>
              <minX value="0" />
              <maxX value="1" />
              <minY value="0" />
              <maxY value="1" />
              <Type value="0" />
              <IsInvisible value="true" />
              <z value="0" />
            </Item>
          </WaterQuads>
          <CalmingQuads>
            <Item>
              <minX value="1752" />
              <maxX value="2076" />
              <minY value="216" />
              <maxY value="800" />
              <fDampening value="0.05" />
            </Item>
          </CalmingQuads>
        </WaterData>
        """;

    [Fact]
    public void Reads_the_visible_water_surfaces()
    {
        var quads = WaterMap.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Xml)));

        Assert.Equal(2, quads.Count);
        Assert.Equal(new WaterQuad(-1592, -1304, -1744, -1624, 0, WaterQuadShape.Rectangle), quads[0]);
        Assert.Equal(new WaterQuad(10, 20, 30, 40, 30.5f, WaterQuadShape.WithoutSouthEast), quads[1]);
    }

    [Fact]
    public void Rectangles_have_two_triangles_and_cut_corners_one()
    {
        var rectangle = new WaterQuad(0, 2, 0, 1, 5, WaterQuadShape.Rectangle).Triangles().ToList();
        Assert.Equal(6, rectangle.Count);
        Assert.All(rectangle, c => Assert.Equal(5, c.Z));

        foreach (var (shape, missing) in new[]
        {
            (WaterQuadShape.WithoutNorthEast, new Vector3(2, 1, 0)),
            (WaterQuadShape.WithoutSouthEast, new Vector3(2, 0, 0)),
            (WaterQuadShape.WithoutSouthWest, new Vector3(0, 0, 0)),
            (WaterQuadShape.WithoutNorthWest, new Vector3(0, 1, 0)),
        })
        {
            var triangle = new WaterQuad(0, 2, 0, 1, 0, shape).Triangles().ToList();
            Assert.Equal(3, triangle.Count);
            Assert.DoesNotContain(missing, triangle);
            Assert.Equal(3, triangle.Distinct().Count());
        }
    }
}
