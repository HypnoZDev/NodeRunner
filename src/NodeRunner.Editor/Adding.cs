using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

/// <summary>Objects added by the user: new entities of an existing type, in a map made by the user.</summary>
public static class Adding
{
    /// <summary>Static object (does not fall or break), as map tools set it for an added prop.</summary>
    public const uint StaticFlags = 32;

    /// <summary>Draw distance when the type gives none (the map tools' default).</summary>
    public const float DefaultLodDistance = 200f;

    /// <summary>
    /// A new object of type <paramref name="archetypeHash"/>: without a lower-detail version ("orphan HD"), drawn up to
    /// its type's distance.
    /// </summary>
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

    /// <summary>
    /// Where to put an object so that it stands on <paramref name="ground"/>: the bottom of its box at that height
    /// (models are usually made with their origin at the base, but not always).
    /// </summary>
    public static Vector3 StandingOn(Vector3 ground, Archetype? archetype) =>
        archetype is null ? ground : ground with { Z = ground.Z - archetype.BoundsMin.Z };
}

/// <summary>Objects added to a map: undone by deleting them (they stay in memory), redone by bringing them back.</summary>
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

    /// <summary>Adds the object to the map and returns the step to add to the history.</summary>
    public static AddEdit Apply(MapLibrary library, MapData map, MapEntity entity, string description) =>
        Apply(library, map, [entity], description);

    /// <summary>Adds the objects to the map (one undo step) and returns the step to add to the history.</summary>
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

/// <summary>An object copied (Ctrl+C): its type, its place in the copied group, how it is turned and scaled.</summary>
public readonly record struct CopiedObject(uint ArchetypeHash, Vector3 Offset, Quaternion Orientation, Vector3 Scale);

/// <summary>
/// Copy and paste of objects (Ctrl+C / Ctrl+V). The copy remembers each object relative to the base of the group: the
/// centre of the box around their positions, at the height of the lowest bottom of their boxes. Pasting puts that base
/// on a point (the ground under the centre of the view) and keeps the group as it was: spacing, heights, rotations, scales.
/// The pasted objects are new ones (static, without a lower-detail version), like an added object.
/// </summary>
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
