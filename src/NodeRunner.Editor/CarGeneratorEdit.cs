using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// An edit of one parking spot of a map.
public interface ICarGeneratorEdit : IEdit
{
    MapData Map { get; }
    int Index { get; }
}

// A parking spot changed: moved, turned, resized, or its car settings.
public sealed class CarGeneratorEdit(MapData map, int index, CarGenerator before, CarGenerator after, string description) : ICarGeneratorEdit
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

// A parking spot added to a map (the project's own). Undoing marks it deleted: the spots after it keep their number.
public sealed class CarGeneratorAddEdit : ICarGeneratorEdit
{
    private CarGeneratorAddEdit(MapData map, int index, string description)
    {
        Map = map;
        Index = index;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public MapData Map { get; }
    public int Index { get; }
    public CarGenerator Spot => Map.CarGenerators[Index] with { IsDeleted = false };

    public static CarGeneratorAddEdit Apply(MapData map, CarGenerator spot, string description) =>
        new(map, map.AddCarGenerator(spot with { SourceIndex = -1, IsDeleted = false }), description);

    public void Undo() => Map.SetCarGenerator(Index, Map.CarGenerators[Index] with { IsDeleted = true });
    public void Redo() => Map.SetCarGenerator(Index, Map.CarGenerators[Index] with { IsDeleted = false });
}

// A parking spot deleted (left out of its map file when saved).
public sealed class CarGeneratorDeleteEdit : ICarGeneratorEdit
{
    private CarGeneratorDeleteEdit(MapData map, int index, string description)
    {
        Map = map;
        Index = index;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public MapData Map { get; }
    public int Index { get; }

    public static CarGeneratorDeleteEdit Apply(MapData map, int index, string description)
    {
        if (index < 0 || index >= map.CarGenerators.Count || map.CarGenerators[index].IsDeleted)
            throw new InvalidDataException("Parking spot not found here.");
        var edit = new CarGeneratorDeleteEdit(map, index, description);
        edit.Redo();
        return edit;
    }

    public void Undo() => Map.SetCarGenerator(Index, Map.CarGenerators[Index] with { IsDeleted = false });
    public void Redo() => Map.SetCarGenerator(Index, Map.CarGenerators[Index] with { IsDeleted = true });
}
