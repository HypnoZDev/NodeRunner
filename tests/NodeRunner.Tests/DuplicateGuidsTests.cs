using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.Tests;

public class DuplicateGuidsTests
{
    private static MapEntity Entity(uint type, uint guid, int source = -1) =>
        new() { ArchetypeHash = type, Position = default, Guid = guid, SourceIndex = source };

    [Fact]
    public void FindsGuidsUsedTwiceInOneFileOrAcrossFiles()
    {
        var duplicates = DuplicateGuids.Find([
            ("a.ymap", [Entity(1, 10), Entity(1, 10), Entity(2, 0), Entity(2, 0)]),
            ("b.ymap", [Entity(3, 20)]),
            ("c.ymap", [Entity(3, 20), Entity(4, 30)]),
        ]);

        Assert.Equal(2, duplicates.Count);
        Assert.Equal(["a.ymap", "a.ymap"], duplicates[0].Files);
        Assert.Equal(["b.ymap", "c.ymap"], duplicates[1].Files);
    }

    [Fact]
    public void The_copies_are_all_but_one_and_the_game_file_keeps_its_guid()
    {
        var first = Entity(1, 10, 0);
        var second = Entity(1, 10, 1);
        var inGame = Entity(3, 20, 5);
        var copied = Entity(3, 20, 0);
        var copies = DuplicateGuids.Copies([
            (false, [first, second, copied, Entity(2, 0), Entity(2, 0)]),
            (true, [inGame, Entity(4, 30)]),
        ]);

        Assert.Equal(2, copies.Count);
        Assert.Contains(second, copies);
        Assert.Contains(copied, copies);

        var guids = DuplicateGuids.NewGuids(3, new HashSet<uint> { 10, 20, 30 }, new Random(1));
        Assert.Equal(3, guids.Distinct().Count());
        Assert.DoesNotContain(guids, g => g is 0 or 10 or 20 or 30);
    }

    [Fact]
    public void A_new_guid_is_written_to_the_file_undone_and_shared_online()
    {
        var file = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd), new DeletionTests.Item(0x31, -1, LodLevel.OrphanHd));
        var map = new YmapReader().Read(file);
        var library = MapLibrary.Create((map, "test.ymap"));
        uint before = map.Entities[1].Guid;

        var edit = GuidEdit.Apply([map.Entities[1]], [0x12345u], "test");
        var written = new YmapReader().Read(new YmapWriter().Write(file, map, MapExtents.Of(map)).Content);
        Assert.Equal(0x12345u, written.Entities[1].Guid);
        Assert.Equal(map.Entities[0].Guid, written.Entities[0].Guid);

        var codecs = EditCodecs.ForEditor();
        var message = codecs.Encode(edit, library);
        edit.Undo();
        Assert.Equal(before, map.Entities[1].Guid);
        Assert.IsType<GuidEdit>(codecs.Apply(message, new EditContext { Library = library }));
        Assert.Equal(0x12345u, map.Entities[1].Guid);
    }
}
