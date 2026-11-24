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
          <WaveQuads>
            <Item>
              <minX value="1664" />
              <maxX value="1988" />
              <minY value="-120" />
              <maxY value="132" />
              <Amplitude value="1.5" />
              <XDirection value="-0.603208" />
              <YDirection value="-0.797584" />
            </Item>
          </WaveQuads>
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
    public void Reads_the_calm_zones_and_the_zones_of_waves_rolling_in()
    {
        var (_, calming, waves) = WaterMap.Read(new MemoryStream(Encoding.UTF8.GetBytes(Xml)));

        Assert.Equal([new WaterCalming(1752, 2076, 216, 800, 0.05f)], calming);
        var wave = Assert.Single(waves);
        Assert.Equal((1664f, 1988f, -120f, 132f, 1.5f), (wave.MinX, wave.MaxX, wave.MinY, wave.MaxY, wave.Amplitude));
        Assert.Equal(1f, wave.Direction.Length(), 3);
        Assert.True(wave.Direction.X < 0 && wave.Direction.Y < 0);
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

    [Fact]
    public void Height_is_the_highest_surface_covering_a_point_and_the_ocean_beyond_the_map()
    {
        var map = new WaterMap { OceanBounds = new Vector4(-100, -100, 100, 100), OceanHeight = 0f };
        map.Quads.Add(new WaterQuad(-50, 50, -50, 50, 0f, WaterQuadShape.Rectangle));
        // A lake higher up, cut along its diagonal: only its south-west half.
        map.Quads.Add(new WaterQuad(0, 10, 0, 10, 30f, WaterQuadShape.WithoutNorthEast));

        Assert.Equal(0f, map.HeightAt(-20, -20));
        Assert.Equal(30f, map.HeightAt(2, 2));
        Assert.Equal(0f, map.HeightAt(8, 8)); // The cut-off half: only the sea below.
        Assert.Null(map.HeightAt(70, 70)); // Land inside the map.
        Assert.Equal(0f, map.HeightAt(500, 0)); // Open ocean.
    }
}
