using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Doors given another door model, fitted in the same frame: a swinging door's hinge goes on the jamb of its own side,
// a sliding or garage door is centred on the opening, the bottom stays at the same height. One undo step.
public sealed class DoorReplaceEdit : IEdit
{
    private readonly MapEntity[] _entities;
    private readonly uint[] _beforeTypes;
    private readonly Placement[] _before;

    private DoorReplaceEdit(MapEntity[] entities, uint[] beforeTypes, Placement[] before, uint archetype, Placement[] after, string description)
    {
        _entities = entities;
        _beforeTypes = beforeTypes;
        _before = before;
        Archetype = archetype;
        After = after;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => _entities[^1];
    public IEnumerable<MapEntity> Entities => _entities;
    public IReadOnlyList<MapEntity> Selection => _entities;
    public uint Archetype { get; }
    public IReadOnlyList<Placement> After { get; }

    // Where the new door model goes so that it fills the frame of the old one.
    public static Placement Fit(MapEntity door, Archetype from, Archetype to, string? toName)
    {
        var shape = DoorShape.Of(to, toName);
        float dx = shape.Kind == DoorKind.Hinged
            ? shape.Side > 0 ? from.BoundsMin.X - to.BoundsMin.X : from.BoundsMax.X - to.BoundsMax.X
            : (from.BoundsMin.X + from.BoundsMax.X) / 2 - (to.BoundsMin.X + to.BoundsMax.X) / 2;
        float dz = from.BoundsMin.Z - to.BoundsMin.Z;
        var position = Vector3.Transform(new Vector3(dx, 0, dz), door.Transform);
        return new Placement(position, door.Orientation, door.Scale);
    }

    public static DoorReplaceEdit Apply(IReadOnlyList<MapEntity> doors, uint archetypeHash, IReadOnlyList<Placement> after, string description)
    {
        if (doors.Count == 0 || doors.Count != after.Count || doors.Any(e => !ArchetypeEdit.CanChange(e)))
            throw new ArgumentException("Doors whose model cannot be changed.", nameof(doors));
        var edit = new DoorReplaceEdit([.. doors], [.. doors.Select(e => e.ArchetypeHash)], [.. doors.Select(Placement.Of)],
            archetypeHash, [.. after], description);
        edit.Redo();
        return edit;
    }

    public void Undo()
    {
        for (int i = 0; i < _entities.Length; i++)
        {
            _entities[i].SetArchetype(_beforeTypes[i]);
            _before[i].ApplyTo(_entities[i]);
        }
    }

    public void Redo()
    {
        for (int i = 0; i < _entities.Length; i++)
        {
            _entities[i].SetArchetype(Archetype);
            After[i].ApplyTo(_entities[i]);
        }
    }
}
