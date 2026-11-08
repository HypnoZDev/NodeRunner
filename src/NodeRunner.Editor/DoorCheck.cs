using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Door problems found before an export.
public static class DoorCheck
{
    // Archetype flag of the props that move (doors that open); 0x4000000 is only set on doors (measured).
    public const uint DynamicFlag = 0x20000;
    public const uint DoorFlag = 0x4000000;

    private const float SamePlace = 0.2f;
    private const float Moved = 0.05f;

    public readonly record struct MovedDoor(MapEntity Original, MapEntity Current);

    // The doors of a game map file changed by the export: removed, or moved (matched by guid and type).
    public static (List<MapEntity> Removed, List<MovedDoor> Moved) Changed(IReadOnlyList<MapEntity> original,
        IEnumerable<MapEntity> current, Func<uint, string?> names)
    {
        var kept = new Dictionary<(uint, uint), MapEntity>();
        foreach (var entity in current)
            kept.TryAdd((entity.Guid, entity.ArchetypeHash), entity);
        var removed = new List<MapEntity>();
        var moved = new List<MovedDoor>();
        foreach (var door in original.Where(e => Doors.IsDoor(names(e.ArchetypeHash))))
        {
            if (!kept.TryGetValue((door.Guid, door.ArchetypeHash), out var now))
                removed.Add(door);
            else if (Vector3.Distance(door.Position, now.Position) > Moved)
                moved.Add(new MovedDoor(door, now));
        }
        return (removed, moved);
    }

    // Pairs of doors at the same place (e.g. a new door placed without deleting the game's one), each pair once.
    public static List<(MapEntity A, MapEntity B)> Duplicates(IEnumerable<MapEntity> doors,
        Func<Vector2, float, IEnumerable<MapEntity>> around, Func<uint, string?> names)
    {
        var pairs = new List<(MapEntity, MapEntity)>();
        var seen = new HashSet<(MapEntity, MapEntity)>();
        foreach (var door in doors)
        {
            foreach (var other in around(new Vector2(door.Position.X, door.Position.Y), SamePlace))
            {
                if (other == door || other.IsDeleted || other.LodLevel is not (LodLevel.Hd or LodLevel.OrphanHd)
                    || !Doors.IsDoor(names(other.ArchetypeHash)) || Vector3.Distance(door.Position, other.Position) > SamePlace)
                    continue;
                if (!seen.Contains((other, door)) && seen.Add((door, other)))
                    pairs.Add((door, other));
            }
        }
        return pairs;
    }

    // A door model of the project (imported or made in another tool) that the game will not let open.
    public static bool IsFrozen(Archetype archetype, string? file) =>
        file is not null && file.StartsWith(GameContent.LooseFilesRoot, StringComparison.OrdinalIgnoreCase)
        && (archetype.Flags & DynamicFlag) == 0;
}
