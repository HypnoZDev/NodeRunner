using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Tests;

public class FingerprintTests
{
    private static MapData Map(uint name, params uint[] types) => new()
    {
        NameHash = name,
        Entities = [.. types.Select((t, i) => new MapEntity { ArchetypeHash = t, Position = new Vector3(i, 0, 0), SourceIndex = i })],
    };

    private static MapLibrary Library(bool reversed, params (MapData, string)[] maps) =>
        MapLibrary.FromMaps([new Archetype { NameHash = 1 }, new Archetype { NameHash = 2 }], reversed ? maps.Reverse() : maps);

    [Fact]
    public void Same_game_content_gives_the_same_fingerprint_whatever_the_order_and_the_edits()
    {
        var first = Library(false, (Map(10, 1, 2), "a.ymap"), (Map(20, 2), "b.ymap"));
        var second = Library(true, (Map(10, 1, 2), "a.ymap"), (Map(20, 2), "b.ymap"));
        Assert.Equal(first.ContentFingerprint(), second.ContentFingerprint());

        string before = first.ContentFingerprint();
        first.Maps[0].Entities[0].SetPlacement(new Vector3(50, 0, 0), Quaternion.Identity, Vector3.One);
        Assert.Equal(before, first.ContentFingerprint());
    }

    [Fact]
    public void Same_counts_but_other_content_gives_another_fingerprint()
    {
        // Same number of maps, objects and types: the old check (counts only) took these for the same game.
        var first = Library(false, (Map(10, 1, 2), "a.ymap"), (Map(20, 2), "b.ymap"));
        var second = Library(false, (Map(10, 2, 1), "a.ymap"), (Map(20, 2), "b.ymap"));
        Assert.NotEqual(first.ContentFingerprint(), second.ContentFingerprint());
    }
}
