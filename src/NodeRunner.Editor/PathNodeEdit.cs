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
