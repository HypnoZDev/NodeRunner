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
