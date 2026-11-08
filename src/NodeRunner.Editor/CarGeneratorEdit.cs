using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A parking spot changed: moved, turned, resized, or its car settings.
public sealed class CarGeneratorEdit(MapData map, int index, CarGenerator before, CarGenerator after, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public MapData Map => map;
    public int Index => index;
    public CarGenerator Before => before;
    public CarGenerator After => after;

    public void Undo() => map.SetCarGenerator(index, before);
    public void Redo() => map.SetCarGenerator(index, after);
}
