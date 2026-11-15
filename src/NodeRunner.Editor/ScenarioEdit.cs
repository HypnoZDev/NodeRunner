using NodeRunner.Data.Maps;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Editor;

// A scenario point moved, turned or changed (type, hours, probability...).
public sealed class ScenarioEdit(ScenarioSet set, ScenarioPoint point, ScenarioValues before, ScenarioValues after, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public ScenarioPoint Point => point;
    public ScenarioValues Before { get; } = before;
    public ScenarioValues After { get; } = after;

    public void Undo() => set.SetValues(point, Before);
    public void Redo() => set.SetValues(point, After);
}

// A scenario point added to a region (a copy of another point's models, group... when it had a template).
public sealed class ScenarioAddEdit : IEdit
{
    private readonly ScenarioSet _set;
    private readonly ScenarioRegion _region;
    private readonly int _index;

    private ScenarioAddEdit(ScenarioSet set, ScenarioRegion region, ScenarioPoint point, int templateIndex, string description)
    {
        (_set, _region, Point, _index) = (set, region, point, point.Index);
        TemplateIndex = templateIndex;
        Values = point.Values;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public ScenarioPoint Point { get; }
    public string RegionName => _region.Name;
    public int Index => _index;
    // The template's number before the addition, or -1.
    public int TemplateIndex { get; }
    public ScenarioValues Values { get; }

    public static ScenarioAddEdit Apply(ScenarioSet set, ScenarioRegion region, ScenarioValues values, ScenarioPoint? template, string description, int? index = null)
    {
        int templateIndex = template?.Index ?? -1;
        var point = set.AddPoint(region, values, template, index);
        return new ScenarioAddEdit(set, region, point, templateIndex, description);
    }

    public void Undo() => _set.DeletePoint(Point);
    public void Redo() => _set.InsertPoint(_region, _index, -1, Point, Values);
}

// One of a region's own points deleted.
public sealed class ScenarioDeleteEdit : IEdit
{
    private readonly ScenarioSet _set;
    private readonly ScenarioRegion _region;

    private readonly int _group;

    private ScenarioDeleteEdit(ScenarioSet set, ScenarioRegion region, ScenarioPoint point, int index, int group, string description)
    {
        (_set, _region, Point, Index, _group) = (set, region, point, index, group);
        Values = point.Values;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public ScenarioPoint Point { get; }
    public string RegionName => _region.Name;
    public int Index { get; }
    public ScenarioValues Values { get; }

    public static ScenarioDeleteEdit? Apply(ScenarioSet set, ScenarioPoint point, string description) =>
        point.Region is { } region && set.DeletePoint(point) is var (index, group)
            ? new ScenarioDeleteEdit(set, region, point, index, group, description)
            : null;

    public void Undo() => _set.InsertPoint(_region, Index, _group, Point, Values);
    public void Redo() => _set.DeletePoint(Point);
}
