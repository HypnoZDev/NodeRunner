using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class DuplicateGuidsTests
{
    private static MapEntity Entity(uint type, uint guid) => new() { ArchetypeHash = type, Position = default, Guid = guid };

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
}
