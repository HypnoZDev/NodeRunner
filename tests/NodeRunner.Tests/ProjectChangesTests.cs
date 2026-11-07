using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class ProjectChangesTests
{
    private static MapEntity Entity(uint type, float x) => new() { ArchetypeHash = type, Position = new Vector3(x, 0, 0) };

    private static List<MapEntity> Game() => [Entity(1, 0), Entity(2, 10), Entity(3, 20)];

    [Fact]
    public void ListsAddedMovedAndDeletedObjects()
    {
        var current = Game();
        current[0].SetPlacement(new Vector3(4, 0, 0), Quaternion.Identity, Vector3.One);
        current.RemoveAt(2);
        current.Add(Entity(9, 50));
        var map = new MapData { NameHash = 1, Entities = current };

        var changes = ProjectChanges.Compare(map, Game());

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Kind == ChangeKind.Moved && c.Entity.ArchetypeHash == 1);
        Assert.Contains(changes, c => c.Kind == ChangeKind.Deleted && c.Entity.ArchetypeHash == 3 && c.Entity.Position.X == 20);
        Assert.Contains(changes, c => c.Kind == ChangeKind.Added && c.Entity.ArchetypeHash == 9);
    }

    [Fact]
    public void UnchangedMapHasNoChanges() =>
        Assert.Empty(ProjectChanges.Compare(new MapData { NameHash = 1, Entities = Game() }, Game()));

    [Fact]
    public void EveryObjectOfAProjectMapIsAdded() =>
        Assert.Equal(3, ProjectChanges.Compare(new MapData { NameHash = 1, Entities = Game() }, null).Count(c => c.Kind == ChangeKind.Added));
}
