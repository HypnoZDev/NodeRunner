using System.Numerics;
using NodeRunner.Data.Native.Scenarios;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Tests;

public class ScenarioGridTests
{
    [Fact]
    public void Points_are_sorted_by_cell_and_each_cell_gives_its_end()
    {
        var grid = ScenarioRegionWriter.MakeGrid(
        [
            (new Vector3(40, 1, 0), 0u),       // cell (1, 0)
            (new Vector3(-1, 1, 0), 0u),       // cell (-1, 0)
            (new Vector3(5, 33, 0), 1u << 22), // cell (0, 1), seen from far
            (new Vector3(-10, 2, 0), 0u),      // cell (-1, 0)
        ]);
        Assert.Equal((32f, -1, 0, 1, 1), (grid.CellSize, grid.MinX, grid.MinY, grid.MaxX, grid.MaxY));
        Assert.Equal([1, 3, 0, 2], grid.Order);
        // Row y = 0: cells -1, 0, 1; row y = 1: cells -1, 0 (flagged), 1.
        Assert.Equal([2, 2, 3, 3, 0x8000 | 4, 4], grid.CellEnds.Select(e => (int)e));
    }

    [Fact]
    public void A_spread_region_takes_bigger_cells()
    {
        var grid = ScenarioRegionWriter.MakeGrid([(new Vector3(0, 0, 0), 0u), (new Vector3(2000, 2000, 0), 0u)]);
        Assert.True((grid.MaxX - grid.MinX + 1) * (grid.MaxY - grid.MinY + 1) <= 999);
        Assert.Equal(128f, grid.CellSize);
    }

    [Fact]
    public void Adding_and_deleting_renumbers_the_points_and_can_be_undone()
    {
        var set = new ScenarioSet();
        var region = new ScenarioRegion { Name = "r", Points = [new ScenarioPoint { Position = Vector3.Zero }, new ScenarioPoint { Position = Vector3.One }], OwnPointCount = 1, ClusterSizes = [1] };
        foreach (var (p, i) in region.Points.Select((p, i) => (p, i)))
            (p.Region, p.Index) = (region, i);
        set.Regions.Add(region);

        var add = NodeRunner.Editor.ScenarioAddEdit.Apply(set, region, region.Points[0].Values with { Position = new(5, 5, 5) }, region.Points[0], "ajouter");
        Assert.Equal((3, 2, 1), (region.Points.Count, region.OwnPointCount, add.Point.Index));
        Assert.Equal(2, region.Points[2].Index);
        var delete = NodeRunner.Editor.ScenarioDeleteEdit.Apply(set, region.Points[0], "supprimer")!;
        Assert.Equal(new Vector3(5, 5, 5), region.Points[0].Position);
        var clustered = NodeRunner.Editor.ScenarioDeleteEdit.Apply(set, region.Points[^1], "groupe")!;
        Assert.Equal((1, 1, 0), (region.Points.Count, region.OwnPointCount, region.ClusterSizes[0]));
        clustered.Undo();
        Assert.Equal((2, 1, 1), (region.Points.Count, region.OwnPointCount, region.ClusterSizes[0]));
        delete.Undo();
        add.Undo();
        Assert.Equal((2, 1), (region.Points.Count, region.OwnPointCount));
        Assert.Equal(Vector3.One, region.Points[1].Position);
    }
}
