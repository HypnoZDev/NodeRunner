using System.Numerics;
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
}
