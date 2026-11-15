namespace NodeRunner.Data.Paths;

// The content of some areas at one moment, put back to undo or redo a deletion.
public sealed class PathAreasState
{
    internal sealed record AreaState(PathNodeArea Area, PathNode[] Nodes, PathLink[] Links, int Vehicles, int Pedestrians,
        (PathNodeId Id, int First, int Count)[] Numbers);

    internal List<AreaState> Areas { get; } = [];

    public IEnumerable<int> AreaIds => Areas.Select(a => a.Area.AreaId);
}

public sealed partial class PathNetwork
{
    // Removes a node, its links and the links of other nodes to it. The game finds a node by its number in its file:
    // the following nodes are renumbered and every link to them follows. Null when the node is not in the network.
    public (PathAreasState Before, PathAreasState After)? DeleteNode(PathNode node)
    {
        var id = node.Id;
        if (GetArea(id.AreaId) is not { } area || GetNode(id) != node)
            return null;
        var touched = Areas.Where(a => a == area || a.Links.Any(l => l.Target.AreaId == id.AreaId)).ToList();
        var before = Capture(touched);

        var nodes = area.Nodes.Where(n => n != node).ToList();
        area.Nodes = nodes;
        if (node.IsPedestrian)
            area.PedestrianNodeCount--;
        else
            area.VehicleNodeCount--;
        foreach (var other in touched)
            RebuildLinks(other, link => link.Target == id ? null
                : link.Target.AreaId == id.AreaId && link.Target.NodeId > id.NodeId
                    ? link with { Target = new PathNodeId(id.AreaId, (ushort)(link.Target.NodeId - 1)) }
                    : link);
        for (int i = 0; i < nodes.Count; i++)
            nodes[i].Id = new PathNodeId(id.AreaId, (ushort)i);

        return (before, Done(touched));
    }

    // Removes the links between two nodes, both ways. Null when they are not linked.
    public (PathAreasState Before, PathAreasState After)? DeleteLink(PathNode a, PathNode b)
    {
        if (GetArea(a.Id.AreaId) is not { } areaA || GetArea(b.Id.AreaId) is not { } areaB
            || !areaA.LinksOf(a).Any(l => l.Target == b.Id) && !areaB.LinksOf(b).Any(l => l.Target == a.Id))
            return null;
        var touched = new[] { areaA, areaB }.Distinct().ToList();
        var before = Capture(touched);
        RebuildLinks(areaA, link => link.Target == b.Id ? null : link, a);
        RebuildLinks(areaB, link => link.Target == a.Id ? null : link, b);
        return (before, Done(touched));
    }

    // Puts back the areas of a state (undo / redo of a deletion).
    public void Restore(PathAreasState state)
    {
        foreach (var saved in state.Areas)
        {
            var area = saved.Area;
            area.Nodes = [.. saved.Nodes];
            area.Links = [.. saved.Links];
            area.VehicleNodeCount = saved.Vehicles;
            area.PedestrianNodeCount = saved.Pedestrians;
            for (int i = 0; i < saved.Nodes.Length; i++)
                (saved.Nodes[i].Id, saved.Nodes[i].FirstLink, saved.Nodes[i].LinkCount) = saved.Numbers[i];
            _changed.Add(area.AreaId);
        }
        Revision++;
    }

    private PathAreasState Done(List<PathNodeArea> touched)
    {
        foreach (var area in touched)
            _changed.Add(area.AreaId);
        Revision++;
        return Capture(touched);
    }

    private static PathAreasState Capture(IEnumerable<PathNodeArea> areas)
    {
        var state = new PathAreasState();
        foreach (var area in areas)
            state.Areas.Add(new PathAreasState.AreaState(area, [.. area.Nodes], [.. area.Links], area.VehicleNodeCount,
                area.PedestrianNodeCount, [.. area.Nodes.Select(n => (n.Id, n.FirstLink, n.LinkCount))]));
        return state;
    }

    // Rewrites an area's links: change returns the new link, or null to drop it; only the links of "only" when given.
    // Each node's links stay together, in node order.
    private static void RebuildLinks(PathNodeArea area, Func<PathLink, PathLink?> change, PathNode? only = null)
    {
        var old = area.Links;
        var links = new List<PathLink>(old.Count);
        foreach (var node in area.Nodes)
        {
            int first = links.Count;
            for (int i = 0; i < node.LinkCount; i++)
            {
                int index = node.FirstLink + i;
                if (index >= old.Count)
                    continue;
                if ((only is null || only == node ? change(old[index]) : old[index]) is { } link)
                    links.Add(link);
            }
            node.FirstLink = first;
            node.LinkCount = links.Count - first;
        }
        area.Links = links;
    }
}
