using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;

namespace NodeRunner.Editor;

// An edit of one node of the road network (selected again on undo / redo).
public interface IPathNodeEdit : IEdit
{
    PathNode Node { get; }
}

// A node of the road network moved.
public sealed class PathNodeMoveEdit(PathNetwork network, PathNode node, Vector3 before, Vector3 after, string description) : IPathNodeEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public PathNode Node => node;
    public Vector3 Before { get; } = before;
    public Vector3 After { get; } = after;

    public void Undo() => network.MoveNode(node, Before);
    public void Redo() => network.MoveNode(node, After);
}

// Speed, density, special type or other flags of a node changed.
public sealed class PathNodeValuesEdit(PathNetwork network, PathNode node, PathNodeValues before, PathNodeValues after, string description) : IPathNodeEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public PathNode Node => node;
    public PathNodeValues Before { get; } = before;
    public PathNodeValues After { get; } = after;

    public void Undo() => network.SetValues(node, Before);
    public void Redo() => network.SetValues(node, After);
}

// A node deleted with its links; the next nodes of its file renumbered (see PathNetwork.DeleteNode).
public sealed class PathNodeDeleteEdit : IPathNodeEdit
{
    private readonly PathNetwork _network;
    private readonly PathAreasState _before, _after;

    private PathNodeDeleteEdit(PathNetwork network, PathNode node, PathNodeId id, PathAreasState before, PathAreasState after, string description)
    {
        _network = network;
        Node = node;
        Id = id;
        (_before, _after) = (before, after);
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public PathNode Node { get; }

    // The node's number before its deletion.
    public PathNodeId Id { get; }

    public static PathNodeDeleteEdit? Apply(PathNetwork network, PathNode node, string description)
    {
        var id = node.Id;
        return network.DeleteNode(node) is var (before, after) ? new PathNodeDeleteEdit(network, node, id, before, after, description) : null;
    }

    public void Undo() => _network.Restore(_before);
    public void Redo() => _network.Restore(_after);
}

// The links between two nodes deleted, both ways.
public sealed class PathLinkDeleteEdit : IPathNodeEdit
{
    private readonly PathNetwork _network;
    private readonly PathAreasState _before, _after;

    private PathLinkDeleteEdit(PathNetwork network, PathNode node, PathNode other, PathAreasState before, PathAreasState after, string description)
    {
        _network = network;
        Node = node;
        Other = other;
        (_before, _after) = (before, after);
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public PathNode Node { get; }
    public PathNode Other { get; }

    public static PathLinkDeleteEdit? Apply(PathNetwork network, PathNode node, PathNode other, string description) =>
        network.DeleteLink(node, other) is var (before, after) ? new PathLinkDeleteEdit(network, node, other, before, after, description) : null;

    public void Undo() => _network.Restore(_before);
    public void Redo() => _network.Restore(_after);
}

// A node added, linked or not to an existing node, in one undo step.
public sealed class PathNodeAddEdit : IPathNodeEdit
{
    private readonly PathNetwork _network;
    private readonly List<(PathAreasState Before, PathAreasState After)> _steps;

    private PathNodeAddEdit(PathNetwork network, PathNode node, List<(PathAreasState, PathAreasState)> steps, string description)
    {
        _network = network;
        Node = node;
        _steps = steps;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public PathNode Node { get; }
    public bool Pedestrian => Node.IsPedestrian;
    public PathNodeValues Values { get; private init; }
    public uint StreetName => Node.StreetNameHash;
    public System.Numerics.Vector3 Position { get; private init; }

    // The node it was linked to, by its number before the addition, and the lanes towards it and back.
    public PathNodeId? LinkedTo { get; private init; }
    public int LanesForward { get; private init; }
    public int LanesBackward { get; private init; }

    public static PathNodeAddEdit? Apply(PathNetwork network, System.Numerics.Vector3 position, bool pedestrian, PathNodeValues values,
        uint streetName, PathNode? linkTo, int lanesForward, int lanesBackward, string description)
    {
        var linkedTo = linkTo?.Id;
        if (network.AddNode(position, pedestrian, values, streetName) is not var (node, before, after))
            return null;
        var steps = new List<(PathAreasState, PathAreasState)> { (before, after) };
        if (linkTo is not null)
        {
            if (network.AddLink(node, linkTo, lanesForward, lanesBackward) is not { } link)
            {
                network.Restore(before);
                return null;
            }
            steps.Add(link);
        }
        return new PathNodeAddEdit(network, node, steps, description)
        {
            Values = values,
            Position = position,
            LinkedTo = linkedTo,
            LanesForward = lanesForward,
            LanesBackward = lanesBackward,
        };
    }

    public void Undo()
    {
        for (int i = _steps.Count - 1; i >= 0; i--)
            _network.Restore(_steps[i].Before);
    }

    public void Redo()
    {
        foreach (var step in _steps)
            _network.Restore(step.After);
    }
}

// Two nodes linked both ways.
public sealed class PathLinkAddEdit : IPathNodeEdit
{
    private readonly PathNetwork _network;
    private readonly PathAreasState _before, _after;

    private PathLinkAddEdit(PathNetwork network, PathNode node, PathNode other, int forward, int backward, PathAreasState before, PathAreasState after, string description)
    {
        _network = network;
        (Node, Other, LanesForward, LanesBackward) = (node, other, forward, backward);
        (_before, _after) = (before, after);
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public PathNode Node { get; }
    public PathNode Other { get; }
    public int LanesForward { get; }
    public int LanesBackward { get; }

    public static PathLinkAddEdit? Apply(PathNetwork network, PathNode node, PathNode other, int lanesForward, int lanesBackward, string description) =>
        network.AddLink(node, other, lanesForward, lanesBackward) is var (before, after)
            ? new PathLinkAddEdit(network, node, other, lanesForward, lanesBackward, before, after, description)
            : null;

    public void Undo() => _network.Restore(_before);
    public void Redo() => _network.Restore(_after);
}
