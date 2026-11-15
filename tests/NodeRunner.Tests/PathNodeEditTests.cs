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
        network.SetArea(new PathNodeArea { AreaId = 1, Nodes = a, Links = aLinks, VehicleNodeCount = 3, PedestrianNodeCount = 1 }, "nodes1.ynd");
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

    // Inside area 1 (x from -7680 to -7168, y from -8192 to -7680).
    private static readonly Vector3 InArea1 = new(-7400f, -8000f, 10f);

    [Fact]
    public void Adding_a_vehicle_node_goes_before_pedestrian_nodes_and_links_follow()
    {
        var network = LinkedNetwork();
        var ped = network.GetNode(new(1, 3))!;
        var last = network.GetNode(new(1, 2))!;
        var other = network.GetNode(new(2, 0))!;
        // A pedestrian node linked to the pedestrian node, to check the renumbering of links to it.
        var walker = PathNodeAddEdit.Apply(network, InArea1, true, default, 0, ped, 0, 0, "piéton")!;
        Assert.Equal(new PathNodeId(1, 4), walker.Node.Id);

        var history = new EditHistory();
        var edit = PathNodeAddEdit.Apply(network, InArea1, false, new PathNodeValues(2, 0, 4, 0, 8, 2), 0x1234, last, 2, 1, "Ajouter")!;
        history.Add(edit);
        var area = network.GetArea(1)!;
        var added = edit.Node;
        Assert.Equal(new PathNodeId(1, 3), added.Id);
        Assert.False(added.IsJunction);
        Assert.Equal(new PathNodeId(1, 4), ped.Id);
        Assert.Equal(new PathNodeId(1, 5), walker.Node.Id);
        Assert.Equal((4, 2), (area.VehicleNodeCount, area.PedestrianNodeCount));
        Assert.Equal([new PathNodeId(1, 5)], area.LinksOf(ped).Select(l => l.Target));
        Assert.Equal([new PathNodeId(1, 4)], area.LinksOf(walker.Node).Select(l => l.Target));
        var toNew = area.LinksOf(last).Single(l => l.Target == added.Id);
        var back = area.LinksOf(added).Single();
        Assert.Equal(last.Id, back.Target);
        Assert.Equal((2, 1), (back.LanesForward, back.LanesBackward));
        Assert.Equal((1, 2), (toNew.LanesForward, toNew.LanesBackward));
        Assert.Equal(back.Length, toNew.Length);
        Assert.Equal([new PathNodeId(1, 1), new PathNodeId(2, 0), added.Id], area.LinksOf(last).Select(l => l.Target));
        Assert.Equal([new PathNodeId(1, 2)], network.GetArea(2)!.LinksOf(other).Select(l => l.Target));

        var reread = Rewrite(area, (_, l) => l.Length);
        Assert.Equal(area.Links, reread.Links);

        history.Undo();
        Assert.Equal(5, area.Nodes.Count);
        Assert.Equal(new PathNodeId(1, 3), ped.Id);
        Assert.Equal(2, area.LinksOf(last).Count());
        history.Redo();
        Assert.Equal(new PathNodeId(1, 3), added.Id);
        Assert.Equal(3, area.LinksOf(last).Count());
    }

    [Fact]
    public void Linking_two_nodes_adds_both_ways_once()
    {
        var network = LinkedNetwork();
        var first = network.GetNode(new(1, 0))!;
        var other = network.GetNode(new(2, 0))!;
        var edit = PathLinkAddEdit.Apply(network, first, other, 1, 1, "Relier")!;
        Assert.Equal([new PathNodeId(1, 1), new PathNodeId(2, 0)], network.GetArea(1)!.LinksOf(first).Select(l => l.Target));
        Assert.Equal(2, network.GetArea(2)!.LinksOf(other).Count());
        Assert.Null(PathLinkAddEdit.Apply(network, other, first, 1, 1, "encore"));
        Assert.Null(PathLinkAddEdit.Apply(network, first, first, 1, 1, "soi-même"));
        edit.Undo();
        Assert.Single(network.GetArea(1)!.LinksOf(first));
    }

    [Fact]
    public void Link_lanes_are_set_both_ways_keeping_the_other_bits()
    {
        var network = LinkedNetwork();
        var first = network.GetNode(new(1, 0))!;
        var other = network.GetNode(new(2, 0))!;
        PathLinkAddEdit.Apply(network, first, other, 1, 1, "Relier");
        var area1 = network.GetArea(1)!;
        var area2 = network.GetArea(2)!;
        var links = area1.Links.ToList();
        int at = links.FindIndex(l => l.Target == other.Id);
        links[at] = links[at] with { Flags2 = (byte)(links[at].Flags2 | 0x03) };
        area1.Links = links;

        var edit = PathLinkLanesEdit.Apply(network, first, other, 3, 0, "Voies")!;
        var there = area1.LinksOf(first).Single(l => l.Target == other.Id);
        var back = area2.LinksOf(other).Single(l => l.Target == first.Id);
        Assert.Equal((3, 0), (there.LanesForward, there.LanesBackward));
        Assert.True(there.IsShortcut && there.DontUseForNavigation);
        Assert.Equal((0, 3), (back.LanesForward, back.LanesBackward));
        Assert.Null(PathLinkLanesEdit.Apply(network, first, other, 3, 0, "pareil"));
        Assert.Null(PathLinkLanesEdit.Apply(network, first, network.GetNode(new(1, 2))!, 1, 1, "pas relié"));

        edit.Undo();
        Assert.Equal(1, area2.LinksOf(other).Single(l => l.Target == first.Id).LanesForward);
        edit.Redo();
        Assert.Equal(3, area1.LinksOf(first).Single(l => l.Target == other.Id).LanesForward);
    }

    [Fact]
    public void A_node_is_added_in_a_new_area_but_not_outside_the_map()
    {
        var network = new PathNetwork();
        var added = network.AddNode(InArea1, false, default, 0);
        Assert.NotNull(added);
        Assert.Single(network.GetArea(1)!.Nodes);
        Assert.Contains(1, network.ChangedAreas);
        network.Restore(added.Value.Before);
        Assert.Empty(network.GetArea(1)!.Nodes);
        Assert.Null(network.AddNode(new Vector3(9000f, 0f, 0f), false, default, 0));
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
