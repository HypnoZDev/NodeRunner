using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using Xunit;

namespace NodeRunner.Tests;

public class MloCheckTests
{
    private static readonly Vector3[] Door = [new(0, 0, 0), new(1, 0, 0), new(1, 0, 2), new(0, 0, 2)];

    private static MapEntity Entity(uint model, int room, int portal = -1) =>
        new() { ArchetypeHash = model, Position = Vector3.One, InteriorRoom = room, InteriorPortal = portal };

    // Two rooms, a door to the outside and one between them; every object in a room.
    private static Archetype Interior(IReadOnlyList<InteriorPortal>? portals = null, IReadOnlyList<MapEntity>? entities = null,
        IReadOnlyList<InteriorEntitySet>? sets = null, int rooms = 2) => new()
    {
        NameHash = 1,
        Kind = ArchetypeKind.Interior,
        InteriorRooms = [new(Vector3.Zero, Vector3.Zero), .. Enumerable.Range(0, rooms).Select(_ => new InteriorRoom(Vector3.Zero, new Vector3(4, 4, 3)))],
        InteriorPortals = portals ?? [new(1, 0, 0) { Corners = Door }, new(1, 2, 0) { Corners = [.. Door.Select(c => c + Vector3.UnitY)] }],
        InteriorEntities = entities ?? [Entity(10, 1), Entity(10, 2), Entity(11, -1, portal: 0)],
        InteriorEntitySets = sets ?? [],
    };

    private static List<MloCheck.Kind> Kinds(Archetype interior) =>
        [.. MloCheck.Check(interior, model => model is 10 or 11).Select(i => i.Kind)];

    [Fact]
    public void AValidInteriorHasNothingToReport() => Assert.Empty(Kinds(Interior()));

    [Fact]
    public void BrokenPortalsAreErrors()
    {
        var kinds = Kinds(Interior(portals:
        [
            new(1, 0, 0) { Corners = Door },
            new(1, 2, 0) { Corners = Door.Take(3).ToArray() },
            new(2, 1, 0) { Corners = [Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX, 3 * Vector3.UnitX] },
            new(1, 2, 0) { Corners = [Door[0], Door[1], Door[2] + new Vector3(0, 0.5f, 0), Door[3]] },
            new(1, 7, 0) { Corners = Door },
        ]));
        Assert.Equal([MloCheck.Kind.PortalCorners, MloCheck.Kind.PortalFlat, MloCheck.Kind.PortalTwisted, MloCheck.Kind.PortalUnknownRoom], kinds);
        Assert.All(kinds, k => Assert.True(MloCheck.IsError(k)));
    }

    [Fact]
    public void ObjectsOutsideAnyRoomAreErrors()
    {
        var kinds = Kinds(Interior(entities: [Entity(10, -1), Entity(10, 5), Entity(11, -1, portal: 4)]));
        Assert.Equal([MloCheck.Kind.EntityNoRoom, MloCheck.Kind.EntityUnknownRoom, MloCheck.Kind.EntityUnknownRoom], kinds);
    }

    [Fact]
    public void LayoutAndModelWarnings()
    {
        var kinds = Kinds(Interior(portals: [new(1, 2, 0) { Corners = Door }, new(2, 1, 0) { Corners = [.. Door.Reverse()] }],
            entities: [Entity(99, 1), Entity(99, 2)], rooms: 3));
        Assert.Equal([MloCheck.Kind.PortalDuplicate, MloCheck.Kind.RoomWithoutPortal, MloCheck.Kind.NoExteriorPortal, MloCheck.Kind.MissingModel], kinds);
        Assert.DoesNotContain(kinds, MloCheck.IsError);
    }

    [Fact]
    public void EntitySetsAndRoomCount()
    {
        var sets = new InteriorEntitySet[] { new(5, []), new(5, [Entity(10, -1)]) };
        var kinds = Kinds(Interior(sets: sets));
        // An object of an entity set may have no room: it is not reported.
        Assert.Equal([MloCheck.Kind.EntitySetEmpty, MloCheck.Kind.EntitySetDuplicate], kinds);

        var big = Interior(portals: [.. Enumerable.Range(1, 32).Select(r => new InteriorPortal(r, 0, 0) { Corners = Door })], rooms: 32);
        Assert.Equal([MloCheck.Kind.TooManyRooms], Kinds(big));
    }
}
