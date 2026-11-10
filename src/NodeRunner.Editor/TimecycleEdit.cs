using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// An edit of one timecycle zone of a map.
public interface ITimecycleEdit : IEdit
{
    MapData Map { get; }
    int Index { get; }
}

// A timecycle zone changed: moved, resized, or its settings.
public sealed class TimecycleEdit(MapData map, int index, TimecycleModifier before, TimecycleModifier after, string description) : ITimecycleEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public MapData Map => map;
    public int Index => index;
    public TimecycleModifier Before => before;
    public TimecycleModifier After => after;

    public void Undo() => map.SetTimecycleModifier(index, before);
    public void Redo() => map.SetTimecycleModifier(index, after);
}

// A timecycle zone added to a map (the project's own). Undoing marks it deleted: the zones after it keep their number.
public sealed class TimecycleAddEdit : ITimecycleEdit
{
    private TimecycleAddEdit(MapData map, int index, string description)
    {
        Map = map;
        Index = index;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public MapData Map { get; }
    public int Index { get; }
    public TimecycleModifier Zone => Map.TimecycleModifiers[Index] with { IsDeleted = false };

    public static TimecycleAddEdit Apply(MapData map, TimecycleModifier zone, string description) =>
        new(map, map.AddTimecycleModifier(zone with { SourceIndex = -1, IsDeleted = false }), description);

    public void Undo() => Map.SetTimecycleModifier(Index, Map.TimecycleModifiers[Index] with { IsDeleted = true });
    public void Redo() => Map.SetTimecycleModifier(Index, Map.TimecycleModifiers[Index] with { IsDeleted = false });
}

// A timecycle zone deleted (left out of its map file when saved).
public sealed class TimecycleDeleteEdit : ITimecycleEdit
{
    private TimecycleDeleteEdit(MapData map, int index, string description)
    {
        Map = map;
        Index = index;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public MapData Map { get; }
    public int Index { get; }

    public static TimecycleDeleteEdit Apply(MapData map, int index, string description)
    {
        if (index < 0 || index >= map.TimecycleModifiers.Count || map.TimecycleModifiers[index].IsDeleted)
            throw new InvalidDataException("Timecycle zone not found here.");
        var edit = new TimecycleDeleteEdit(map, index, description);
        edit.Redo();
        return edit;
    }

    public void Undo() => Map.SetTimecycleModifier(Index, Map.TimecycleModifiers[Index] with { IsDeleted = false });
    public void Redo() => Map.SetTimecycleModifier(Index, Map.TimecycleModifiers[Index] with { IsDeleted = true });
}
