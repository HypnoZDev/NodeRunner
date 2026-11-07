using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// What goes when objects are deleted "cleanly": the objects, the higher-detail versions that replace them up close
// (their children in the level-of-detail tree), and their lower-detail versions left standing for nothing (a parent
// whose children are all deleted).
public sealed class DeletionPlan
{
    public required IReadOnlyList<MapEntity> Targets { get; init; }

    public MapEntity Target => Targets[^1];

    // Everything to delete: the objects chosen first, then their children, then their parents left without children.
    public required IReadOnlyList<MapEntity> Entities { get; init; }

    public required IReadOnlyList<MapEntity> Children { get; init; }
    public required IReadOnlyList<MapEntity> Parents { get; init; }

    public required IReadOnlyList<MapEntity> KeptParents { get; init; }

    public IEnumerable<MapData> Maps => Entities.Select(e => e.Map).OfType<MapData>().Distinct();

    public static DeletionPlan For(MapEntity target) => For([target]);

    public static DeletionPlan For(IReadOnlyList<MapEntity> targets)
    {
        if (targets.Count == 0)
            throw new ArgumentException("Nothing to delete.", nameof(targets));
        if (targets.Any(t => !t.CanDelete))
            throw new InvalidOperationException("Objects inside an interior are defined by the interior's archetype.");

        // Every object chosen and its children first: a parent goes only if all its children go.
        var going = new HashSet<MapEntity>();
        var chosen = targets.Where(going.Add).ToList();
        var children = new List<MapEntity>();
        foreach (var target in chosen)
            AddChildren(target, children, going);

        var parents = new List<MapEntity>();
        var kept = new List<MapEntity>();
        foreach (var target in chosen)
        {
            for (var parent = target.Parent; parent is not null && !parent.IsDeleted && !going.Contains(parent); parent = parent.Parent)
            {
                if (!parent.Children.All(c => c.IsDeleted || going.Contains(c)))
                {
                    if (!kept.Contains(parent))
                        kept.Add(parent);
                    break;
                }
                parents.Add(parent);
                going.Add(parent);
            }
        }
        kept.RemoveAll(going.Contains);

        return new DeletionPlan
        {
            Targets = chosen,
            Entities = [.. chosen, .. children, .. parents],
            Children = children,
            Parents = parents,
            KeptParents = kept,
        };
    }

    private static void AddChildren(MapEntity entity, List<MapEntity> into, HashSet<MapEntity> going)
    {
        foreach (var child in entity.Children)
        {
            if (child.IsDeleted || !going.Add(child))
                continue;
            into.Add(child);
            AddChildren(child, into, going);
        }
    }
}

// Objects deleted; undoing brings them all back.
public sealed class DeleteEdit(MapLibrary library, DeletionPlan plan, string description) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => plan.Target;
    public IEnumerable<MapEntity> Entities => plan.Entities;
    public IReadOnlyList<MapEntity> Selection => plan.Targets;

    public static DeleteEdit Apply(MapLibrary library, DeletionPlan plan, string description)
    {
        var edit = new DeleteEdit(library, plan, description);
        edit.Redo();
        return edit;
    }

    public void Undo() => library.SetDeleted(plan.Entities, false);
    public void Redo() => library.SetDeleted(plan.Entities, true);
}
