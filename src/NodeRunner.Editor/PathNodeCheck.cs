using NodeRunner.Data.Paths;

namespace NodeRunner.Editor;

// Problems in the road network files written by an export (the areas changed by hand): what makes cars or the game
// misbehave once the .ynd ships.
public static class PathNodeCheck
{
    public enum Kind
    {
        // A vehicle node without any link: no car ever reaches it.
        Isolated,
        // A link towards a node that does not exist (deleted, or a file not shipped): the game can crash.
        MissingTarget,
        // A link with no way back: the game expects every link in both nodes.
        OneWay,
        // A vehicle link with no lane either way: cars do not use it.
        NoLane,
        // A link between a road node and a pedestrian node.
        MixedKinds,
    }

    public readonly record struct Issue(Kind Kind, PathNode Node, PathNodeId? Target = null);

    public static List<Issue> Run(PathNetwork network, IEnumerable<int> areas)
    {
        var issues = new List<Issue>();
        foreach (int areaId in areas.Distinct().Order())
        {
            if (network.GetArea(areaId) is not { } area)
                continue;
            foreach (var node in area.Nodes)
            {
                var links = area.LinksOf(node).ToList();
                if (links.Count == 0 && !node.IsPedestrian)
                    issues.Add(new Issue(Kind.Isolated, node));
                foreach (var link in links)
                {
                    if (network.GetArea(link.Target.AreaId) is not { } targetArea)
                        continue; // Area not loaded (DLC...): nothing to compare with.
                    if (network.GetNode(link.Target) is not { } target)
                    {
                        issues.Add(new Issue(Kind.MissingTarget, node, link.Target));
                        continue;
                    }
                    if (!targetArea.LinksOf(target).Any(l => l.Target == node.Id))
                        issues.Add(new Issue(Kind.OneWay, node, link.Target));
                    if (node.IsPedestrian != target.IsPedestrian)
                    {
                        // Reported once per pair.
                        if (!node.IsPedestrian)
                            issues.Add(new Issue(Kind.MixedKinds, node, link.Target));
                    }
                    else if (!node.IsPedestrian && link.LanesForward == 0 && link.LanesBackward == 0
                             && (node.Id.AreaId, node.Id.NodeId).CompareTo((target.Id.AreaId, target.Id.NodeId)) < 0)
                        issues.Add(new Issue(Kind.NoLane, node, link.Target));
                }
            }
        }
        return issues;
    }
}
