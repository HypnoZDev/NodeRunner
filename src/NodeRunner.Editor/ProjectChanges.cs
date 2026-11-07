using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

public enum ChangeKind { Added, Moved, Deleted }

// One object changed by a project. For a deleted object, Entity is the game's version (its original place).
public sealed record ObjectChange(ChangeKind Kind, MapEntity Entity, MapData Map);

public static class ProjectChanges
{
    // Changes of one map file against the game's version (null for a map file of the project, absent from the game).
    public static List<ObjectChange> Compare(MapData map, IReadOnlyList<MapEntity>? gameEntities)
    {
        var changes = new List<ObjectChange>();
        var current = map.Entities;
        if (gameEntities is null)
        {
            foreach (var entity in current)
                if (!entity.IsDeleted)
                    changes.Add(new(ChangeKind.Added, entity, map));
            return changes;
        }

        var match = MapMerge.Match(gameEntities, current);
        var matched = new bool[current.Count];
        for (int i = 0; i < gameEntities.Count; i++)
        {
            int j = match[i];
            if (j >= 0)
                matched[j] = true;
            if (j < 0 || current[j].IsDeleted)
                changes.Add(new(ChangeKind.Deleted, gameEntities[i], map));
            else if (!MapMerge.Same(Placement.Of(gameEntities[i]), Placement.Of(current[j])))
                changes.Add(new(ChangeKind.Moved, current[j], map));
        }
        for (int j = 0; j < current.Count; j++)
            if (!matched[j] && !current[j].IsDeleted)
                changes.Add(new(ChangeKind.Added, current[j], map));
        return changes;
    }
}
