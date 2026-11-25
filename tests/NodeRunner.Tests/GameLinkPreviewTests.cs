using System.Numerics;
using System.Text.Json.Nodes;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class GameLinkPreviewTests
{
    [Fact]
    public void Moved_object_hides_the_old_one_and_shows_the_new_one()
    {
        var entity = Adding.NewEntity(0x9A3C1B2Du, null, new Vector3(10, 20, 30), Quaternion.Identity);
        var preview = new GameLinkPreview();
        preview.Watch(entity);
        Assert.True(preview.Refresh());
        Assert.Empty(preview.Items(Vector3.Zero));

        var before = Placement.Of(entity);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        entity.SetPlacement(new Vector3(12, 20, 30), turned, Vector3.One);
        preview.Observe(new PlacementEdit(entity, before, Placement.Of(entity), "Déplacer"), undone: false);
        Assert.True(preview.Refresh());
        Assert.False(preview.Refresh());

        var item = Assert.IsType<JsonObject>(Assert.Single(preview.Items(Vector3.Zero)));
        Assert.Equal(unchecked((int)0x9A3C1B2Du), (int)item["hm"]!);
        Assert.Equal(10.0, (double)item["hx"]!);
        Assert.Equal(12.0, (double)item["x"]!);
        Assert.Equal(Math.Round(turned.W, 4), (double)item["qw"]!, 4);

        entity.SetPlacement(before.Position, before.Orientation, before.Scale);
        Assert.True(preview.Refresh());
        Assert.Empty(preview.Items(Vector3.Zero));
    }

    [Fact]
    public void Added_then_undone_object_shows_nothing()
    {
        var library = new MapLibrary();
        var map = library.CreateCustomMap("test_props.ymap");
        var entity = Adding.NewEntity(0x1234, null, new Vector3(1, 2, 3), Quaternion.Identity);
        var history = new EditHistory();
        var preview = new GameLinkPreview();
        history.Added += e => preview.Observe(e, undone: false);
        history.Undone += e => preview.Observe(e, undone: true);

        history.Add(AddEdit.Apply(library, map, entity, "Ajouter"));
        var item = Assert.IsType<JsonObject>(Assert.Single(preview.Items(Vector3.Zero)));
        Assert.Null(item["hm"]);
        Assert.Equal(0x1234, (int)item["m"]!);

        history.Undo();
        Assert.Empty(preview.Items(Vector3.Zero));

        preview.Reset();
        Assert.Equal(0, preview.Count);
    }
}
