using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class MapMergeTests
{
    private static MapEntity Entity(uint type, float x, uint guid = 0) =>
        new() { ArchetypeHash = type, Position = new Vector3(x, 0, 0), Guid = guid };

    private static List<MapEntity> Game() => [Entity(1, 0), Entity(2, 10), Entity(2, 20), Entity(3, 30)];

    [Fact]
    public void TakesWhatOnlyTheOtherVersionChanged()
    {
        var ours = Game();
        var theirs = Game();
        theirs[1] = Entity(2, 15);   // Moved.
        theirs.RemoveAt(3);          // Deleted.
        theirs.Add(Entity(9, 99));   // Added.

        var plan = MapMerge.Plan(Game(), ours, theirs);

        Assert.Single(plan.Moves);
        Assert.Same(ours[1], plan.Moves[0].Entity);
        Assert.Equal(15f, plan.Moves[0].Placement.Position.X);
        Assert.Same(ours[3], Assert.Single(plan.Deletions));
        Assert.Equal(9u, Assert.Single(plan.Additions).ArchetypeHash);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void DifferentChangesOnBothSidesAreConflicts()
    {
        var ours = Game();
        ours[0].SetPlacement(new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One);
        ours.RemoveAt(3); // Deleted in an earlier session (absent from the saved file).
        var theirs = Game();
        theirs[0] = Entity(1, 7);
        theirs[3] = Entity(3, 33);

        var plan = MapMerge.Plan(Game(), ours, theirs);

        Assert.True(plan.IsEmpty);
        Assert.Equal(2, plan.Conflicts.Count);
    }

    [Fact]
    public void SameChangeOnBothSidesIsAlreadyThere()
    {
        var ours = Game();
        ours[2].SetPlacement(new Vector3(25, 0, 0), Quaternion.Identity, Vector3.One);
        var theirs = Game();
        theirs[2] = Entity(2, 25);

        var plan = MapMerge.Plan(Game(), ours, theirs);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(1, plan.AlreadyThere);
    }

    [Fact]
    public void MatchesByGuidBeforeOrder()
    {
        List<MapEntity> game = [Entity(2, 10, guid: 100), Entity(2, 20, guid: 200)];
        List<MapEntity> other = [Entity(2, 20, guid: 200)]; // The first one deleted.

        Assert.Equal([-1, 0], MapMerge.Match(game, other));
    }
}
