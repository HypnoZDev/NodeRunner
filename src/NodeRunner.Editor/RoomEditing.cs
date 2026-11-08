using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A change to an interior's own definition (not to a placed object): its archetype file (.ytyp) has to be written.
public interface IInteriorEdit
{
    // The interior instance the change was made from.
    MapEntity Interior { get; }
}

// A room of an interior changed (box, name, settings), in every instance of the interior.
public sealed class RoomEdit(MapLibrary library, MapEntity interior, int index, InteriorRoom before, InteriorRoom after, string description,
    IReadOnlyList<PortalChange>? portals = null) : IEdit, IInteriorEdit
{
    // Portals that moved with the room (those lying on a dragged wall).
    public IReadOnlyList<PortalChange> Portals { get; } = portals ?? [];

    public MapEntity Interior { get; } = interior;

    public InteriorRoom After => after;

    public int Index { get; } = index;

    public string Description { get; } = description;
    public MapEntity? Subject => Interior;
    public IEnumerable<MapEntity> Entities => [];

    // Applies the change and returns the step to record.
    public static RoomEdit Apply(MapLibrary library, MapEntity interior, int index, InteriorRoom after, string description,
        IReadOnlyList<(int Index, InteriorPortal After)>? portals = null)
    {
        var archetype = library.GetArchetype(interior.ArchetypeHash) ?? throw new ArgumentException("Unknown interior.", nameof(interior));
        var before = archetype.InteriorRooms[index];
        var changes = (portals ?? []).Select(p => new PortalChange(p.Index, archetype.InteriorPortals[p.Index], p.After)).ToList();
        var edit = new RoomEdit(library, interior, index, before, after, description, changes);
        edit.Redo();
        return edit;
    }

    public void Undo()
    {
        library.SetRoom(Interior.ArchetypeHash, Index, before);
        foreach (var p in Portals)
            library.SetPortal(Interior.ArchetypeHash, p.Index, p.Before);
    }

    public void Redo()
    {
        library.SetRoom(Interior.ArchetypeHash, Index, after);
        foreach (var p in Portals)
            library.SetPortal(Interior.ArchetypeHash, p.Index, p.After);
    }
}

public sealed record PortalChange(int Index, InteriorPortal Before, InteriorPortal After);

// A portal of an interior changed (corners, rooms, settings), in every instance of the interior.
public sealed class PortalEdit(MapLibrary library, MapEntity interior, int index, InteriorPortal before, InteriorPortal after, string description)
    : IEdit, IInteriorEdit
{
    public MapEntity Interior { get; } = interior;

    public InteriorPortal After => after;

    public int Index { get; } = index;

    public string Description { get; } = description;
    public MapEntity? Subject => Interior;
    public IEnumerable<MapEntity> Entities => [];

    // Applies the change and returns the step to record.
    public static PortalEdit Apply(MapLibrary library, MapEntity interior, int index, InteriorPortal after, string description)
    {
        var archetype = library.GetArchetype(interior.ArchetypeHash) ?? throw new ArgumentException("Unknown interior.", nameof(interior));
        var before = archetype.InteriorPortals[index];
        library.SetPortal(interior.ArchetypeHash, index, after);
        return new PortalEdit(library, interior, index, before, after, description);
    }

    public void Undo() => library.SetPortal(Interior.ArchetypeHash, Index, before);

    public void Redo() => library.SetPortal(Interior.ArchetypeHash, Index, after);
}

// Objects of an interior put in another room, in every instance of the interior.
public sealed class EntityRoomEdit : IEdit
{
    private readonly MapLibrary _library;
    private readonly MapEntity[] _placed;
    private readonly int[] _before;
    private readonly int[] _after;

    private EntityRoomEdit(MapLibrary library, MapEntity[] placed, int[] before, int[] after, string description)
    {
        _library = library;
        _placed = placed;
        _before = before;
        _after = after;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => _placed[^1];
    public IEnumerable<MapEntity> Entities => _placed;
    public IReadOnlyList<MapEntity> Selection => _placed;

    // The new room of each object.
    public IReadOnlyList<int> Rooms => _after;

    public static EntityRoomEdit Apply(MapLibrary library, IReadOnlyList<MapEntity> placed, IReadOnlyList<int> rooms, string description)
    {
        if (placed.Count == 0 || rooms.Count != placed.Count)
            throw new ArgumentException("One room per object expected.", nameof(rooms));
        int[] before = [.. placed.Select(p => p.InteriorRoom)];
        var edit = new EntityRoomEdit(library, [.. placed], before, [.. rooms], description);
        edit.Redo();
        return edit;
    }

    public void Undo()
    {
        for (int i = _placed.Length - 1; i >= 0; i--)
            _library.SetEntityRoom(_placed[i], _before[i]);
    }

    public void Redo()
    {
        for (int i = 0; i < _placed.Length; i++)
            _library.SetEntityRoom(_placed[i], _after[i]);
    }
}

// A room or portal added to an interior or removed from it (objects and other portals renumbered), in every instance.
public sealed class InteriorLayoutEdit(MapLibrary library, MapEntity interior, InteriorLayout before, InteriorLayout after, string description,
    bool isPortal, int index, bool added) : IEdit, IInteriorEdit
{
    public MapEntity Interior { get; } = interior;

    public string Description { get; } = description;
    public MapEntity? Subject => Interior;
    public IEnumerable<MapEntity> Entities => [];

    // The room or portal added or removed.
    public bool IsPortal { get; } = isPortal;
    public int Index { get; } = index;
    public bool Added { get; } = added;

    public InteriorLayout After => after;

    // Runs the change (which returns the index of the room or portal) and returns the step to record.
    public static InteriorLayoutEdit Apply(MapLibrary library, MapEntity interior, Func<int> change, string description, bool isPortal, bool added)
    {
        var before = library.GetLayout(interior.ArchetypeHash);
        int index = change();
        return new InteriorLayoutEdit(library, interior, before, library.GetLayout(interior.ArchetypeHash), description, isPortal, index, added);
    }

    public void Undo() => library.SetLayout(Interior.ArchetypeHash, before);

    public void Redo() => library.SetLayout(Interior.ArchetypeHash, after);
}
