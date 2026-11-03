using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;

namespace NodeRunner.Editor;

// A node of the road network moved.
public sealed class PathNodeMoveEdit(PathNetwork network, PathNode node, Vector3 before, Vector3 after, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public PathNode Node => node;
    public Vector3 Before { get; } = before;
    public Vector3 After { get; } = after;

    public void Undo() => network.MoveNode(node, Before);
    public void Redo() => network.MoveNode(node, After);
}
