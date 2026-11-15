using System.Numerics;
using NodeRunner.Data.Native.Paths;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Paths;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class PathNodeEditTests
{
    [Fact]
    public void Moving_a_node_changes_the_revision_and_can_be_undone()
    {
        var network = new PathNetwork();
        var node = new PathNode { Id = new PathNodeId(5, 0), Position = new Vector3(1f, 2f, 3f) };
        var history = new EditHistory();

        network.MoveNode(node, new Vector3(4f, 2f, 3f));
        history.Add(new PathNodeMoveEdit(network, node, new Vector3(1f, 2f, 3f), node.Position, "Déplacer"));
        Assert.Equal(1, network.Revision);

        history.Undo();
        Assert.Equal(new Vector3(1f, 2f, 3f), node.Position);
        history.Redo();
        Assert.Equal(new Vector3(4f, 2f, 3f), node.Position);
        Assert.Equal(3, network.Revision);
    }

    internal static PathNodeArea Rewrite(PathNodeArea area, Func<PathNode, PathLink, int> length) =>
        new YndReader().Read(ResourceFile.Read(new YndWriter().Write(area, length, 1)).Content, area.AreaId);

    [Fact]
    public void Writer_rebuilds_an_unchanged_area_and_writes_moved_nodes()
    {
        var area = new YndReader().Read(YndReaderTests.BuildSampleYnd(), 529);
        var same = Rewrite(area, (_, link) => link.Length);
        Assert.Equal(area.Links, same.Links);
        Assert.Equal(area.Nodes.Select(n => (n.Id, n.Position, n.Values, n.StreetNameHash, n.FirstLink, n.LinkCount)),
            same.Nodes.Select(n => (n.Id, n.Position, n.Values, n.StreetNameHash, n.FirstLink, n.LinkCount)));
        Assert.Equal((1, 1), (same.VehicleNodeCount, same.PedestrianNodeCount));

        area.Nodes[0].Position = new Vector3(110f, -190.25f, 31f);
        var reread = Rewrite(area, (_, _) => 20);
        Assert.Equal(new Vector3(110f, -190.25f, 31f), reread.Nodes[0].Position);
        Assert.Equal(20, reread.Links[0].Length);
    }

    [Fact]
    public void Changing_values_marks_the_area_and_can_be_undone()
    {
        var network = new PathNetwork();
        var node = new PathNode { Id = new PathNodeId(7, 0), Position = Vector3.Zero, LinkCountExtra = 3, Flags2 = 0x04 };
        var history = new EditHistory();
        var before = node.Values;
        var after = before.WithSpeed(2).WithDensity(12).WithSpecial(PathNodeSpecialType.TrafficLightStop)
            .With(PathNodeFlag.Highway, true).With(PathNodeFlag.SwitchedOff, true);

        network.SetValues(node, after);
        history.Add(new PathNodeValuesEdit(network, node, before, after, "Modifier"));
        Assert.Contains(7, network.ChangedAreas);
        Assert.Equal(2, node.SpeedCategory);
        Assert.Equal(12, node.Density);
        Assert.Equal(PathNodeSpecialType.TrafficLightStop, node.Special);
        Assert.True(node.IsHighway && node.IsSwitchedOff && node.IsJunction);
        Assert.Equal(1, node.LinkCountExtra & 1);

        history.Undo();
        Assert.Equal(before, node.Values);
        Assert.False(after.With(PathNodeFlag.SwitchedOff, false).Has(PathNodeFlag.SwitchedOff));
    }

    [Fact]
    public void Writer_writes_node_values_and_keeps_link_counts()
    {
        var source = YndReaderTests.BuildSampleYnd();
        var area = new YndReader().Read(source, 529);
        var node = area.Nodes[0];
        int linkCount = node.LinkCount;
        node.Values = node.Values.WithSpeed(3).WithDensity(5).With(PathNodeFlag.Tunnel, true).With(PathNodeFlag.NoGps, true);

        var reread = Rewrite(area, (_, link) => link.Length);
        Assert.Equal(node.Values, reread.Nodes[0].Values);
        Assert.Equal(linkCount, reread.Nodes[0].LinkCount);
        Assert.Equal(area.Nodes[1].Values, reread.Nodes[1].Values);
    }

    // Area 1: nodes 0-1-2 in a line (vehicle), 3 pedestrian; area 2: node 0 linked both ways to 1:2.
    private static PathNetwork LinkedNetwork()
    {
        static PathNode N(int area, int id, bool ped = false) => new() { Id = new((ushort)area, (ushort)id), Position = new(id, area, 0), IsPedestrian = ped };
        static PathLink L(int area, int id) => new(new((ushort)area, (ushort)id), 0, 0, 0x20, 5);
        var network = new PathNetwork();
        PathNode[] a = [N(1, 0), N(1, 1), N(1, 2), N(1, 3, ped: true)];
        PathLink[] aLinks = [L(1, 1), L(1, 0), L(1, 2), L(1, 1), L(2, 0)];
        (a[0].FirstLink, a[0].LinkCount) = (0, 1);
        (a[1].FirstLink, a[1].LinkCount) = (1, 2);
        (a[2].FirstLink, a[2].LinkCount) = (3, 2);
        (a[3].FirstLink, a[3].LinkCount) = (5, 0);
        network.SetArea(new PathNodeArea { AreaId = 1, Nodes = a, Links = aLinks, VehicleNodeCount = 3, PedestrianNodeCount = 1 });
        var b = N(2, 0);
        b.LinkCount = 1;
        network.SetArea(new PathNodeArea { AreaId = 2, Nodes = [b], Links = [L(1, 2)], VehicleNodeCount = 1 });
        return network;
    }

    [Fact]
    public void Deleting_a_node_renumbers_the_next_ones_and_their_links_and_can_be_undone()
    {
        var network = LinkedNetwork();
        var middle = network.GetNode(new(1, 1))!;
        var last = network.GetNode(new(1, 2))!;
        var other = network.GetNode(new(2, 0))!;
        var history = new EditHistory();
        history.Add(PathNodeDeleteEdit.Apply(network, middle, "Supprimer")!);

        var area = network.GetArea(1)!;
        Assert.Equal(3, area.Nodes.Count);
        Assert.Equal(new PathNodeId(1, 1), last.Id);
        Assert.Equal(2, area.VehicleNodeCount);
        Assert.Empty(area.LinksOf(area.Nodes[0]));
        Assert.Equal([new PathNodeId(2, 0)], area.LinksOf(last).Select(l => l.Target));
        Assert.Equal([new PathNodeId(1, 1)], network.GetArea(2)!.LinksOf(other).Select(l => l.Target));
        Assert.Equal(new[] { 1, 2 }, network.ChangedAreas.Order());

        // Written and read back the same.
        var reread = Rewrite(area, (_, l) => l.Length);
        Assert.Equal(area.Links, reread.Links);
        Assert.Equal(area.Nodes.Select(n => (n.Id, n.FirstLink, n.LinkCount)), reread.Nodes.Select(n => (n.Id, n.FirstLink, n.LinkCount)));

        history.Undo();
        Assert.Equal(4, area.Nodes.Count);
        Assert.Same(middle, network.GetNode(new(1, 1)));
        Assert.Equal(new PathNodeId(1, 2), last.Id);
        Assert.Equal(5, area.Links.Count);
        Assert.Equal([new PathNodeId(1, 2)], network.GetArea(2)!.LinksOf(other).Select(l => l.Target));
        history.Redo();
        Assert.Equal(new PathNodeId(1, 1), last.Id);
    }

    [Fact]
    public void Deleting_a_link_removes_both_ways()
    {
        var network = LinkedNetwork();
        var last = network.GetNode(new(1, 2))!;
        var other = network.GetNode(new(2, 0))!;
        var edit = PathLinkDeleteEdit.Apply(network, last, other, "Supprimer le lien")!;
        Assert.Equal([new PathNodeId(1, 1)], network.GetArea(1)!.LinksOf(last).Select(l => l.Target));
        Assert.Empty(network.GetArea(2)!.LinksOf(other));
        Assert.Equal(4, network.GetArea(1)!.Links.Count);
        Assert.Null(PathLinkDeleteEdit.Apply(network, last, other, "encore"));
        edit.Undo();
        Assert.Equal(2, network.GetArea(1)!.LinksOf(last).Count());
    }

    [Fact]
    public void Junctions_are_written_with_their_nodes_and_dropped_with_them()
    {
        var area = new YndReader().Read(YndReaderTests.BuildSampleYnd(), 529);
        var junction = new PathJunction([1, 2, 3, 4, 5, 6, 7, 8], 2, 3, [9, 8, 7, 6, 5, 4]);
        var withJunction = new PathNodeArea
        {
            AreaId = 529, Links = area.Links, VehicleNodeCount = 1, PedestrianNodeCount = 1,
            Nodes = [area.Nodes[0], new PathNode { Id = new(529, 1), Position = Vector3.Zero, IsPedestrian = true, Junction = junction }],
        };
        var reread = Rewrite(withJunction, (_, l) => l.Length);
        Assert.Null(reread.Nodes[0].Junction);
        var back = reread.Nodes[1].Junction!;
        Assert.Equal(junction.Header, back.Header);
        Assert.Equal((2, 3), (back.Width, back.Depth));
        Assert.Equal(junction.Heights, back.Heights);
    }

    [Fact]
    public void Link_length_is_the_game_value_without_its_target()
    {
        var network = new PathNetwork();
        var a = new PathNode { Id = new PathNodeId(5, 0), Position = Vector3.Zero, GamePosition = Vector3.Zero };
        var link = new PathLink(new PathNodeId(5, 1), 0, 0, 0, Length: 20);
        Assert.Equal(20, network.LinkLength(a, link));
    }
}
