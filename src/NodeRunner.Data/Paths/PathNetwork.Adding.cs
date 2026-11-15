using System.Numerics;

namespace NodeRunner.Data.Paths;

public sealed partial class PathNetwork
{
    // The link count of a node is stored on 5 bits.
    public const int MaxLinksPerNode = 31;

    // Adds a node in the file of the area at its position (a new file where the game has none). Vehicle nodes go after
    // the file's vehicle nodes, pedestrian ones at the end: the pedestrian nodes after a new vehicle node are
    // renumbered and every link to them follows. The new node is never a junction (no junction data is made). Null
    // outside the map.
    public (PathNode Node, PathAreasState Before, PathAreasState After)? AddNode(Vector3 position, bool pedestrian, PathNodeValues values, uint streetName)
    {
        int areaId = PathNodeArea.AreaIdAt(new Vector2(position.X, position.Y));
        if (areaId < 0)
            return null;
        var area = _areas[areaId] ??= new PathNodeArea { AreaId = areaId, Nodes = [], Links = [] };
        int index = pedestrian ? area.Nodes.Count : area.VehicleNodeCount;
        var touched = Areas.Where(a => a == area || a.Links.Any(l => l.Target.AreaId == areaId)).ToList();
        var before = Capture(touched);

        if (index < area.Nodes.Count)
            foreach (var other in touched)
                RebuildLinks(other, link => link.Target.AreaId == areaId && link.Target.NodeId >= index
                    ? link with { Target = new PathNodeId((ushort)areaId, (ushort)(link.Target.NodeId + 1)) }
                    : link);
        var node = new PathNode
        {
            Id = new PathNodeId((ushort)areaId, (ushort)index),
            Position = position,
            GamePosition = position,
            IsPedestrian = pedestrian,
            StreetNameHash = streetName,
            Values = values with { Flags2 = (byte)(values.Flags2 & ~0x04) },
        };
        var nodes = area.Nodes.ToList();
        nodes.Insert(index, node);
        area.Nodes = nodes;
        if (pedestrian)
            area.PedestrianNodeCount++;
        else
            area.VehicleNodeCount++;
        for (int i = index; i < nodes.Count; i++)
            nodes[i].Id = new PathNodeId((ushort)areaId, (ushort)i);
        RenumberLinks(area);
        return (node, before, Done(touched));
    }

    // Links two nodes both ways, as the game does: same length, lanes swapped on the way back. Null when they are
    // already linked or one of them has no room for another link.
    public (PathAreasState Before, PathAreasState After)? AddLink(PathNode a, PathNode b, int lanesForward, int lanesBackward)
    {
        if (a == b || GetArea(a.Id.AreaId) is not { } areaA || GetArea(b.Id.AreaId) is not { } areaB
            || GetNode(a.Id) != a || GetNode(b.Id) != b
            || a.LinkCount >= MaxLinksPerNode || b.LinkCount >= MaxLinksPerNode
            || areaA.LinksOf(a).Any(l => l.Target == b.Id) || areaB.LinksOf(b).Any(l => l.Target == a.Id))
            return null;
        // Measured between the game's positions: LinkLength scales it like the other links when either end moves.
        int length = Math.Clamp((int)MathF.Round(Vector3.Distance(a.GamePosition, b.GamePosition)), 1, 255);
        var touched = new[] { areaA, areaB }.Distinct().ToList();
        var before = Capture(touched);
        InsertLink(areaA, a, new PathLink(b.Id, 0, 0, Lanes(lanesForward, lanesBackward), (byte)length));
        InsertLink(areaB, b, new PathLink(a.Id, 0, 0, Lanes(lanesBackward, lanesForward), (byte)length));
        return (before, Done(touched));
    }

    private static byte Lanes(int forward, int backward) => (byte)((Math.Clamp(forward, 0, 7) << 5) | (Math.Clamp(backward, 0, 7) << 2));

    private static void InsertLink(PathNodeArea area, PathNode node, PathLink link)
    {
        var links = area.Links.ToList();
        links.Insert(node.FirstLink + node.LinkCount, link);
        area.Links = links;
        node.LinkCount++;
        RenumberLinks(area);
    }

    // Each node's links follow the previous node's.
    private static void RenumberLinks(PathNodeArea area)
    {
        int first = 0;
        foreach (var node in area.Nodes)
        {
            node.FirstLink = first;
            first += node.LinkCount;
        }
    }
}
