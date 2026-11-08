using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class ArchetypeEditTests
{
    [Fact]
    public void A_new_type_is_written_to_the_map_file_and_can_be_undone()
    {
        var file = DeletionTests.BuildYmap(0x1000, 0, new DeletionTests.Item(0x30, -1, LodLevel.OrphanHd), new DeletionTests.Item(0x31, -1, LodLevel.OrphanHd));
        var map = new YmapReader().Read(file);
        MapLibrary.Create((map, "test.ymap"));
        var entity = map.Entities[1];
        var position = entity.Position;

        var edit = ArchetypeEdit.Apply([entity], 0xBEEFu, "test");
        Assert.Equal(0xBEEFu, entity.ArchetypeHash);
        Assert.Equal(0x31u, entity.FileArchetypeHash);

        var written = new YmapReader().Read(new YmapWriter().Write(file, map, MapExtentsCalculator.Compute(map, _ => null)).Content);
        Assert.Equal(0x30u, written.Entities[0].ArchetypeHash);
        Assert.Equal(0xBEEFu, written.Entities[1].ArchetypeHash);
        Assert.Equal(position, written.Entities[1].Position);

        edit.Undo();
        var undone = new YmapReader().Read(new YmapWriter().Write(file, map, MapExtents.Of(map)).Content);
        Assert.Equal(0x31u, undone.Entities[1].ArchetypeHash);
    }
}
