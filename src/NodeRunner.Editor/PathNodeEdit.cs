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
