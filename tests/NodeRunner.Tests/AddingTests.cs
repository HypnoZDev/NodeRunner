using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class AddingTests
{
    [Fact]
    public void Adding_can_be_undone_and_redone()
    {
        var library = new MapLibrary();
        var map = library.CreateCustomMap("test_props.ymap");
        var entity = Adding.NewEntity(0x1234, null, new Vector3(10, 20, 30), Quaternion.Identity);
        var history = new EditHistory();
        history.Add(AddEdit.Apply(library, map, entity, "Ajouter"));

        Assert.True(map.IsCustom);
        Assert.Same(map, entity.Map);
        Assert.Equal(LodLevel.OrphanHd, entity.LodLevel);
        Assert.Equal(Adding.StaticFlags, entity.Flags);
        Assert.Equal(Adding.DefaultLodDistance, entity.LodDistance);
        Assert.Single(map.KeptEntities);
        Assert.Contains(entity, library.EntitiesAround(new Vector2(10, 20), 5f));

        history.Undo();
        Assert.Empty(map.KeptEntities);
        history.Redo();
        Assert.Single(map.KeptEntities);
        Assert.Same(map, library.FindCustomMap("TEST_PROPS.ymap"));
    }

    [Fact]
    public void Custom_map_boxes_fit_its_objects_only()
    {
        var library = new MapLibrary();
        var map = library.CreateCustomMap("test_props.ymap");
        Assert.Equal(default, MapExtentsCalculator.Compute(map, _ => null));

        library.AddEntity(map, Adding.NewEntity(1, null, new Vector3(100, 200, 10), Quaternion.Identity));
        var extents = MapExtentsCalculator.Compute(map, _ => null);
        Assert.Equal(new Vector3(100, 200, 10), extents.EntitiesMin);
        Assert.Equal(new Vector3(100, 200, 10), extents.EntitiesMax);
        Assert.Equal(new Vector3(100 - Adding.DefaultLodDistance, 200 - Adding.DefaultLodDistance, 10 - Adding.DefaultLodDistance), extents.StreamingMin);
    }

    [Fact]
    public void Pasting_keeps_the_group_and_puts_its_lowest_bottom_on_the_point()
    {
        var archetype = new Archetype { NameHash = 1, BoundsMin = new Vector3(-1, -1, -0.5f), BoundsMax = new Vector3(1, 1, 2) };
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f);
        var a = Adding.NewEntity(1, archetype, new Vector3(0, 0, 10), Quaternion.Identity);
        var b = Adding.NewEntity(2, null, new Vector3(4, 2, 13), turned);
        b.SetPlacement(b.Position, b.Orientation, new Vector3(2, 2, 1));

        var copied = ObjectCopy.Copy([a, b], h => h == 1 ? archetype : null);
        var pasted = ObjectCopy.Paste(copied, new Vector3(100, 200, 50), h => h == 1 ? archetype : null);

        // Base: centre (2, 1) at the bottom of a's box (z 9.5).
        Assert.Equal(new Vector3(98, 199, 50.5f), pasted[0].Position);
        Assert.Equal(new Vector3(102, 201, 53.5f), pasted[1].Position);
        Assert.True(MathF.Abs(Quaternion.Dot(turned, pasted[1].Orientation)) > 0.9999f);
        Assert.Equal(new Vector3(2, 2, 1), pasted[1].Scale);
        Assert.Equal(LodLevel.OrphanHd, pasted[1].LodLevel);
    }

    [Fact]
    public void Pasting_several_objects_is_one_undo_step()
    {
        var library = new MapLibrary();
        var map = library.CreateCustomMap("test_props.ymap");
        var entities = new[] { Adding.NewEntity(1, null, Vector3.Zero, Quaternion.Identity), Adding.NewEntity(2, null, Vector3.One, Quaternion.Identity) };
        var history = new EditHistory();
        history.Add(AddEdit.Apply(library, map, entities, "Coller"));

        Assert.Equal(2, map.KeptEntities.Count());
        history.Undo();
        Assert.Empty(map.KeptEntities);
        Assert.Equal(entities, history.Redo()!.Selection);
    }

    [Fact]
    public void Standing_on_the_ground_uses_the_bottom_of_the_model()
    {
        var archetype = new Archetype { NameHash = 1, BoundsMin = new Vector3(-1, -1, -0.5f), BoundsMax = new Vector3(1, 1, 2) };
        Assert.Equal(new Vector3(5, 6, 7.5f), Adding.StandingOn(new Vector3(5, 6, 7), archetype));
    }
}
