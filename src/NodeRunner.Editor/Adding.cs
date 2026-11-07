using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Objects added by the user: new entities of an existing type, in a map made by the user.
public static class Adding
{
    public const uint StaticFlags = 32;

    public const float DefaultLodDistance = 200f;

    // A new object of type archetypeHash: without a lower-detail version ("orphan HD"), drawn up to its type's
    // distance.
    public static MapEntity NewEntity(uint archetypeHash, Archetype? archetype, Vector3 position, Quaternion orientation)
    {
        float lodDistance = archetype is { LodDistance: > 0f } a ? Math.Min(a.LodDistance, 10000f) : DefaultLodDistance;
        return new MapEntity
        {
            ArchetypeHash = archetypeHash,
            Position = position,
            StoredRotation = Quaternion.Inverse(orientation),
            Flags = StaticFlags,
            Guid = (uint)Random.Shared.Next(1, int.MaxValue),
            LodDistance = lodDistance,
            LodLevel = LodLevel.OrphanHd,
        };
    }

    // The definition of a new object of an interior, from the object placed in the world: placed relative to the
    // interior instance, in room.
    public static MapEntity InteriorDefinition(MapEntity placed, MapEntity instance, int room)
    {
        var toLocal = Quaternion.Inverse(instance.Orientation);
        return new MapEntity
        {
            ArchetypeHash = placed.ArchetypeHash,
            Position = Vector3.Transform(placed.Position - instance.Position, toLocal),
            StoredRotation = Quaternion.Inverse(Quaternion.Normalize(toLocal * placed.Orientation)),
            Scale = placed.Scale,
            Flags = placed.Flags,
            Guid = placed.Guid,
            LodDistance = placed.LodDistance,
            LodLevel = placed.LodLevel,
            InteriorRoom = room,
        };
    }

    // The room of an interior instance holding point (world), the smallest when they overlap; -1 if none. Room 0
    // ("limbo") is the outside.
    public static int RoomAt(MapEntity instance, Archetype archetype, Vector3 point)
    {
        Matrix4x4.Invert(instance.Transform, out var toLocal);
        var p = Vector3.Transform(point, toLocal);
        int best = -1;
        float bestVolume = float.MaxValue;
        for (int r = 1; r < archetype.InteriorRooms.Count; r++)
        {
            var room = archetype.InteriorRooms[r];
            if (room.Name.Equals("limbo", StringComparison.OrdinalIgnoreCase) || Vector3.Clamp(p, room.BoundsMin, room.BoundsMax) != p)
                continue;
            var size = room.BoundsMax - room.BoundsMin;
            float volume = size.X * size.Y * size.Z;
            if (volume < bestVolume)
                (best, bestVolume) = (r, volume);
        }
        return best;
    }

    // Where to put an object so that it stands on ground: the bottom of its box at that height (models are usually
    // made with their origin at the base, but not always).
    public static Vector3 StandingOn(Vector3 ground, Archetype? archetype) =>
        archetype is null ? ground : ground with { Z = ground.Z - archetype.BoundsMin.Z };
}

// Objects added to a map: undone by deleting them (they stay in memory), redone by bringing them back.
public sealed class AddEdit : IEdit
{
    private readonly MapLibrary _library;
    private readonly MapEntity[] _entities;

    private AddEdit(MapLibrary library, MapEntity[] entities, string description)
    {
        _library = library;
        _entities = entities;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => _entities[^1];
    public IEnumerable<MapEntity> Entities => _entities;
    public IReadOnlyList<MapEntity> Selection => _entities;

    public static AddEdit Apply(MapLibrary library, MapData map, MapEntity entity, string description) =>
        Apply(library, map, [entity], description);

    public static AddEdit Apply(MapLibrary library, MapData map, IReadOnlyList<MapEntity> entities, string description)
    {
        if (entities.Count == 0)
            throw new ArgumentException("Nothing to add.", nameof(entities));
        foreach (var entity in entities)
            library.AddEntity(map, entity);
        return new AddEdit(library, [.. entities], description);
    }

    public void Undo() => _library.SetDeleted(_entities, true);
    public void Redo() => _library.SetDeleted(_entities, false);
}

// Objects added to an interior (to its archetype, so to every instance of it): undone by deleting them, redone by
// bringing them back.
public sealed class InteriorAddEdit : IEdit
{
    private readonly MapLibrary _library;
    private readonly MapEntity[] _placed;
    private readonly MapEntity[] _copies;

    private InteriorAddEdit(MapLibrary library, MapEntity[] placed, string description)
    {
        _library = library;
        _placed = placed;
        _copies = [.. placed.SelectMany(p => p.Definition!.Copies)];
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => _placed[^1];
    public IEnumerable<MapEntity> Entities => _copies;
    public IReadOnlyList<MapEntity> Selection => _placed;

    // definitions: placed relative to instance (InteriorDefinition); returns the edit, whose Selection holds their
    // copies in instance.
    public static InteriorAddEdit Apply(MapLibrary library, MapEntity instance, IReadOnlyList<MapEntity> definitions, string description)
    {
        if (definitions.Count == 0)
            throw new ArgumentException("Nothing to add.", nameof(definitions));
        return new InteriorAddEdit(library, [.. definitions.Select(d => library.AddToInterior(instance, d))], description);
    }

    public void Undo() => _library.SetDeleted(_copies, true);
    public void Redo() => _library.SetDeleted(_copies, false);
}

// Objects of an interior moved into one of its entity sets, or out of their set (MapLibrary.MoveToEntitySet); undone
// by bringing the old definitions back.
public sealed class EntitySetMoveEdit : IEdit
{
    private readonly MapLibrary _library;
    private readonly MapEntity[] _old;
    private readonly MapEntity[] _new;
    private readonly MapEntity[] _oldPlaced;
    private readonly MapEntity[] _newPlaced;

    private EntitySetMoveEdit(MapLibrary library, MapEntity[] oldPlaced, MapEntity[] newPlaced, string description)
    {
        _library = library;
        _oldPlaced = oldPlaced;
        _newPlaced = newPlaced;
        _old = [.. oldPlaced.SelectMany(p => p.Definition!.Copies)];
        _new = [.. newPlaced.SelectMany(p => p.Definition!.Copies)];
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => Selection[^1];
    public IEnumerable<MapEntity> Entities => _old.Concat(_new);
    public IReadOnlyList<MapEntity> Selection => _newPlaced[0].IsDeleted ? _oldPlaced : _newPlaced;

    // setIndex: as in the instance's EntitySets, -1 = out of any set.
    public static EntitySetMoveEdit Apply(MapLibrary library, IReadOnlyList<MapEntity> placed, int setIndex, string description)
    {
        if (placed.Count == 0)
            throw new ArgumentException("Nothing to move.", nameof(placed));
        return new EntitySetMoveEdit(library, [.. placed], [.. placed.Select(p => library.MoveToEntitySet(p, setIndex))], description);
    }

    public void Undo()
    {
        _library.SetDeleted(_new, true, countAsDeleted: false);
        _library.SetDeleted(_old, false, countAsDeleted: false);
    }

    public void Redo()
    {
        _library.SetDeleted(_old, true, countAsDeleted: false);
        _library.SetDeleted(_new, false, countAsDeleted: false);
    }
}

// An object copied (Ctrl+C): its type, its place in the copied group, how it is turned and scaled.
public readonly record struct CopiedObject(uint ArchetypeHash, Vector3 Offset, Quaternion Orientation, Vector3 Scale);

// Copy and paste of objects (Ctrl+C / Ctrl+V).
public static class ObjectCopy
{
    public static IReadOnlyList<CopiedObject> Copy(IReadOnlyList<MapEntity> entities, Func<uint, Archetype?> archetypes)
    {
        if (entities.Count == 0)
            return [];
        var pivot = GroupTransform.Pivot(entities.Select(e => e.Position));
        float bottom = entities.Min(e => GroundSnap.WorldBox(e, archetypes(e.ArchetypeHash)) is { } box ? box.Min.Z : e.Position.Z);
        var origin = pivot with { Z = bottom };
        return [.. entities.Select(e => new CopiedObject(e.ArchetypeHash, e.Position - origin, e.Orientation, e.Scale))];
    }

    public static IReadOnlyList<MapEntity> Paste(IReadOnlyList<CopiedObject> copied, Vector3 at, Func<uint, Archetype?> archetypes)
    {
        var pasted = new List<MapEntity>(copied.Count);
        foreach (var c in copied)
        {
            var entity = Adding.NewEntity(c.ArchetypeHash, archetypes(c.ArchetypeHash), at + c.Offset, c.Orientation);
            entity.SetPlacement(entity.Position, entity.Orientation, c.Scale);
            pasted.Add(entity);
        }
        return pasted;
    }
}
