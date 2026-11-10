using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A distant light (in its distlodlights file) and its details (in the lodlights file that is its child), if any.
public readonly record struct LodLightValues(DistantLodLight Light, LodLight? Details)
{
    public static LodLightValues Of(MapData distant, MapData? lod, int index) =>
        new(distant.DistantLights[index], lod is not null && index < lod.LodLights.Count ? lod.LodLights[index] : null);

    public void ApplyTo(MapData distant, MapData? lod, int index)
    {
        if (distant.DistantLights[index] != Light)
            distant.SetDistantLight(index, Light);
        if (Details is { } details && lod is not null && index < lod.LodLights.Count && lod.LodLights[index] != details)
            lod.SetLodLight(index, details);
    }
}

// An edit of one distant light: both of its files are saved.
public interface ILodLightEdit : IEdit
{
    MapData Distant { get; }
    MapData? Lod { get; }
    int Index { get; }
}

// A distant light changed: moved, its colour, intensity, hours, reach...
public sealed class LodLightEdit(MapData distant, MapData? lod, int index, LodLightValues before, LodLightValues after, string description) : ILodLightEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public MapData Distant => distant;
    public MapData? Lod => lod;
    public int Index => index;
    public LodLightValues Before => before;
    public LodLightValues After => after;

    public void Undo() => before.ApplyTo(distant, lod, index);
    public void Redo() => after.ApplyTo(distant, lod, index);
}

// A distant light added with its details (to the project's light files, distant and lod). Undoing marks it deleted in
// both: the lights after it keep their number.
public sealed class LodLightAddEdit : ILodLightEdit
{
    private LodLightAddEdit(MapData distant, MapData lod, int index, string description)
    {
        Distant = distant;
        Lod = lod;
        Index = index;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public MapData Distant { get; }
    public MapData? Lod { get; }
    public int Index { get; }
    public LodLightValues Values => LodLightValues.Of(Distant, Lod, Index);

    public static LodLightAddEdit Apply(MapData distant, MapData lod, DistantLodLight light, LodLight details, string description)
    {
        if (distant.DistantLights.Count != lod.LodLights.Count)
            throw new InvalidDataException("The two light files do not have the same number of lights.");
        int index = distant.AddDistantLight(light);
        lod.AddLodLight(details);
        return new LodLightAddEdit(distant, lod, index, description);
    }

    public void Undo() => SetDeleted(true);
    public void Redo() => SetDeleted(false);

    private void SetDeleted(bool deleted)
    {
        Distant.SetLightDeleted(Index, deleted);
        Lod!.SetLightDeleted(Index, deleted);
    }
}

// A distant light deleted, with its details: left out of both files when saved.
public sealed class LodLightDeleteEdit : ILodLightEdit
{
    private LodLightDeleteEdit(MapData distant, MapData? lod, int index, string description)
    {
        Distant = distant;
        Lod = lod;
        Index = index;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public MapData Distant { get; }
    public MapData? Lod { get; }
    public int Index { get; }

    public static LodLightDeleteEdit Apply(MapData distant, MapData? lod, int index, string description)
    {
        if (index < 0 || index >= distant.DistantLights.Count || distant.IsLightDeleted(index))
            throw new InvalidDataException("Distant light not found here.");
        var edit = new LodLightDeleteEdit(distant, lod, index, description);
        edit.Redo();
        return edit;
    }

    public void Undo() => SetDeleted(false);
    public void Redo() => SetDeleted(true);

    private void SetDeleted(bool deleted)
    {
        Distant.SetLightDeleted(Index, deleted);
        if (Lod is { } lod && Index < lod.LodLights.Count)
            lod.SetLightDeleted(Index, deleted);
    }
}
