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
