using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class SavingTests
{
    [Fact]
    public void Resource_file_keeps_header_and_content()
    {
        var header = new ResourceHeader(2, 2048, 0x20000000);
        var content = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();

        var file = ResourceFile.Write(header, content);
        var (readHeader, readContent) = ResourceFile.Read(file);

        Assert.Equal("RSC7", System.Text.Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal(header, readHeader);
        Assert.Equal(content, readContent);
        Assert.True(file.Length < content.Length); // Compressed.
    }

    [Fact]
    public void History_knows_when_the_map_is_saved()
    {
        var bench = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero };
        var history = new EditHistory();
        Assert.False(history.HasUnsavedChanges);

        history.Add(Move(bench, new Vector3(1, 0, 0)));
        Assert.True(history.HasUnsavedChanges);

        history.MarkSaved();
        Assert.False(history.HasUnsavedChanges);

        history.Undo();
        Assert.True(history.HasUnsavedChanges); // The saved file has the object moved.
        history.Redo();
        Assert.False(history.HasUnsavedChanges);
        Assert.Single(history.All);
    }

    [Fact]
    public void Extents_grow_to_include_a_moved_object_and_never_shrink()
    {
        var moved = new MapEntity { ArchetypeHash = 1, Position = Vector3.Zero, LodDistance = 100f };
        var still = new MapEntity { ArchetypeHash = 2, Position = Vector3.Zero, LodDistance = 100f };
        var map = new MapData
        {
            NameHash = 1,
            StreamingExtentsMin = new Vector3(-200), StreamingExtentsMax = new Vector3(200),
            EntitiesExtentsMin = new Vector3(-10), EntitiesExtentsMax = new Vector3(10),
            Entities = [moved, still],
        };
        var box = new Archetype { NameHash = 1, BoundsMin = new Vector3(-1), BoundsMax = new Vector3(1) };

        Assert.Equal(MapExtents.Of(map), MapExtentsCalculator.Compute(map, _ => box)); // Nothing moved.

        moved.SetPlacement(new Vector3(500, 0, 0), Quaternion.Identity, Vector3.One);
        var extents = MapExtentsCalculator.Compute(map, _ => box);

        Assert.Equal(501f, extents.EntitiesMax.X, 0.001f);
        Assert.Equal(601f, extents.StreamingMax.X, 0.001f);
        Assert.Equal(map.EntitiesExtentsMin, extents.EntitiesMin);
        Assert.Equal(map.StreamingExtentsMin, extents.StreamingMin);
    }

    private static PlacementEdit Move(MapEntity entity, Vector3 to)
    {
        var before = Placement.Of(entity);
        entity.SetPlacement(to, entity.Orientation, entity.Scale);
        return new PlacementEdit(entity, before, Placement.Of(entity), "Déplacer");
    }
}
