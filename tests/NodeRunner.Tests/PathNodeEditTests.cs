using System.Numerics;
using NodeRunner.Data.Native.Paths;
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

    [Fact]
    public void Writer_keeps_an_unchanged_file_and_writes_moved_nodes()
    {
        var source = YndReaderTests.BuildSampleYnd();
        var area = new YndReader().Read(source, 529);
        var writer = new YndWriter();
        Assert.Equal(source, writer.Write(source, area, (_, link) => link.Length));

        area.Nodes[0].Position = new Vector3(110f, -190.25f, 31f);
        var written = writer.Write(source, area, (_, _) => 20);
        var reread = new YndReader().Read(written, 529);
        Assert.Equal(new Vector3(110f, -190.25f, 31f), reread.Nodes[0].Position);
        Assert.Equal(20, reread.Links[0].Length);
        Assert.Equal(area.Nodes[0].StreetNameHash, reread.Nodes[0].StreetNameHash);
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
