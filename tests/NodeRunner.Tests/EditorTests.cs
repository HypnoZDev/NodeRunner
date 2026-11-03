using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class EditorTests
{
    private static MapEntity Bench() => new() { ArchetypeHash = 1, Position = new Vector3(10f, 20f, 30f) };

    private static PlacementEdit Move(MapEntity entity, Vector3 to)
    {
        var before = Placement.Of(entity);
        entity.SetPlacement(to, entity.Orientation, entity.Scale);
        return new PlacementEdit(entity, before, Placement.Of(entity), "Déplacer");
    }

    [Fact]
    public void Undo_and_redo_restore_the_placement()
    {
        var bench = Bench();
        var history = new EditHistory();
        history.Add(Move(bench, new Vector3(11f, 20f, 30f)));
        history.Add(Move(bench, new Vector3(12f, 20f, 30f)));

        history.Undo();
        Assert.Equal(11f, bench.Position.X);
        Assert.Same(bench, history.Undo()?.Subject);
        Assert.Equal(10f, bench.Position.X);
        Assert.False(history.CanUndo);
        Assert.Null(history.Undo());

        history.Redo();
        Assert.Equal(11f, bench.Position.X);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void A_group_turns_around_its_centre()
    {
        var a = new Placement(new Vector3(0f, 0f, 0f), Quaternion.Identity, Vector3.One);
        var b = new Placement(new Vector3(10f, 0f, 2f), Quaternion.Identity, Vector3.One);
        var pivot = GroupTransform.Pivot([a.Position, b.Position]);
        Assert.Equal(new Vector3(5f, 0f, 1f), pivot);

        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var turned = GroupTransform.Rotate(b, pivot, quarter);

        Assert.True(Vector3.Distance(new Vector3(5f, 5f, 2f), turned.Position) < 1e-4f);
        Assert.True(MathF.Abs(Quaternion.Dot(quarter, turned.Orientation)) > 0.9999f);
    }

    [Fact]
    public void A_group_scales_its_spacing_and_each_object()
    {
        var start = new Placement(new Vector3(10f, 4f, 2f), Quaternion.Identity, new Vector3(1f, 1f, 2f));

        var scaled = GroupTransform.Scale(start, new Vector3(0f, 0f, 0f), 2f, 0.5f);

        Assert.Equal(new Vector3(20f, 8f, 1f), scaled.Position);
        Assert.Equal(new Vector3(2f, 2f, 1f), scaled.Scale);
    }

    [Fact]
    public void A_group_move_is_one_undo_step()
    {
        var a = Bench();
        var b = new MapEntity { ArchetypeHash = 2, Position = new Vector3(0f, 0f, 0f) };
        var before = new[] { Placement.Of(a), Placement.Of(b) };
        var after = before.Select(p => GroupTransform.Move(p, new Vector3(1f, 0f, 0f))).ToArray();
        after[0].ApplyTo(a);
        after[1].ApplyTo(b);
        var history = new EditHistory();
        history.Add(new GroupPlacementEdit([a, b], before, after, "Déplacer"));

        var undone = history.Undo();

        Assert.Equal(10f, a.Position.X);
        Assert.Equal(0f, b.Position.X);
        Assert.Equal([a, b], undone!.Selection);
    }

    [Fact]
    public void A_new_change_discards_the_undone_ones()
    {
        var bench = Bench();
        var history = new EditHistory();
        history.Add(Move(bench, new Vector3(11f, 20f, 30f)));
        history.Undo();

        history.Add(Move(bench, new Vector3(50f, 20f, 30f)));

        Assert.False(history.CanRedo);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Each_change_increments_the_revision()
    {
        var bench = Bench();
        int revision = bench.Revision;

        bench.SetPlacement(bench.Position, bench.Orientation, new Vector3(2f, 2f, 1f));

        Assert.Equal(revision + 1, bench.Revision);
        Assert.Equal(2f, bench.Scale.X);
    }

    [Fact]
    public void Ordinary_entities_keep_storing_the_inverse_rotation()
    {
        var bench = Bench();
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);

        bench.SetPlacement(bench.Position, quarter, Vector3.One);

        Assert.Equal(Quaternion.Inverse(quarter), bench.StoredRotation);
        Assert.Equal(quarter, bench.Orientation);
    }

    [Theory]
    [InlineData(0f, 0f, 90f)]
    [InlineData(30f, -20f, 135f)]
    [InlineData(-170f, 45f, -60f)]
    public void Rotation_angles_round_trip(float x, float y, float z)
    {
        var angles = new Vector3(x, y, z);

        var back = EulerAngles.FromQuaternion(EulerAngles.ToQuaternion(angles));

        Assert.Equal(x, back.X, 2);
        Assert.Equal(y, back.Y, 2);
        Assert.Equal(z, back.Z, 2);
    }

    [Fact]
    public void A_rotation_around_z_turns_x_towards_y()
    {
        var q = EulerAngles.ToQuaternion(new Vector3(0f, 0f, 90f));

        var turned = Vector3.Transform(Vector3.UnitX, q);

        Assert.Equal(0f, turned.X, 4);
        Assert.Equal(1f, turned.Y, 4);
    }
}
