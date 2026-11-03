using System.Numerics;
using NodeRunner.Data.NavMeshes;

namespace NodeRunner.Tests;

public class NavMeshCatalogTests
{
    // Checked against the bounds stored in the game's own files, e.g. navmesh[102][102] = (-900,-900)..(-750,-750).
    [Theory]
    [InlineData(-800f, -800f, 102, 102)]
    [InlineData(0f, -1100f, 120, 96)]
    [InlineData(1300f, -700f, 144, 105)]
    [InlineData(149.9f, 0.1f, 120, 120)]
    public void Finds_tile_containing_position(float x, float y, int expectedX, int expectedY)
    {
        Assert.Equal(new NavMeshTileKey(expectedX, expectedY), NavMeshCatalog.TileAt(new Vector2(x, y)));
    }

    [Fact]
    public void Tile_center_is_inside_tile()
    {
        var key = new NavMeshTileKey(102, 102);
        Assert.Equal(new Vector2(-825f, -825f), NavMeshCatalog.TileCenter(key));
        Assert.Equal(key, NavMeshCatalog.TileAt(NavMeshCatalog.TileCenter(key)));
    }
}
