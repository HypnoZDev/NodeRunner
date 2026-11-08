using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Objects given another type (model), same place.
public sealed class ArchetypeEdit : IEdit
{
    private readonly MapEntity[] _entities;
    private readonly uint[] _before;

    private ArchetypeEdit(MapEntity[] entities, uint[] before, uint after, string description)
    {
        _entities = entities;
        _before = before;
        Archetype = after;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => _entities[^1];
    public IEnumerable<MapEntity> Entities => _entities;
    public IReadOnlyList<MapEntity> Selection => _entities;

    // The new type.
    public uint Archetype { get; }

    // Whether this object's type can be changed: an object of a map file, not an interior nor an object inside one.
    public static bool CanChange(MapEntity entity) => entity.Interior is null && !entity.IsInterior && !entity.IsDeleted;

    public static ArchetypeEdit Apply(IReadOnlyList<MapEntity> entities, uint archetypeHash, string description)
    {
        if (entities.Count == 0 || entities.Any(e => !CanChange(e)))
            throw new ArgumentException("Objects whose type cannot be changed.", nameof(entities));
        var edit = new ArchetypeEdit([.. entities], [.. entities.Select(e => e.ArchetypeHash)], archetypeHash, description);
        edit.Redo();
        return edit;
    }

    public void Undo()
    {
        for (int i = 0; i < _entities.Length; i++)
            _entities[i].SetArchetype(_before[i]);
    }

    public void Redo()
    {
        foreach (var entity in _entities)
            entity.SetArchetype(Archetype);
    }
}
