using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A change to an interior's own definition (not to a placed object): its archetype file (.ytyp) has to be written.
public interface IInteriorEdit
{
    // The interior instance the change was made from.
    MapEntity Interior { get; }
}

// A room of an interior changed (box, name, settings), in every instance of the interior.
public sealed class RoomEdit(MapLibrary library, MapEntity interior, int index, InteriorRoom before, InteriorRoom after, string description)
    : IEdit, IInteriorEdit
{
    public MapEntity Interior { get; } = interior;

    public int Index { get; } = index;

    public string Description { get; } = description;
    public MapEntity? Subject => Interior;
    public IEnumerable<MapEntity> Entities => [];

    // Applies the change and returns the step to record.
    public static RoomEdit Apply(MapLibrary library, MapEntity interior, int index, InteriorRoom after, string description)
    {
        var archetype = library.GetArchetype(interior.ArchetypeHash) ?? throw new ArgumentException("Unknown interior.", nameof(interior));
        var before = archetype.InteriorRooms[index];
        library.SetRoom(interior.ArchetypeHash, index, after);
        return new RoomEdit(library, interior, index, before, after, description);
    }

    public void Undo() => library.SetRoom(Interior.ArchetypeHash, Index, before);

    public void Redo() => library.SetRoom(Interior.ArchetypeHash, Index, after);
}

// A portal of an interior changed (corners, rooms, settings), in every instance of the interior.
public sealed class PortalEdit(MapLibrary library, MapEntity interior, int index, InteriorPortal before, InteriorPortal after, string description)
    : IEdit, IInteriorEdit
{
    public MapEntity Interior { get; } = interior;

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
