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
