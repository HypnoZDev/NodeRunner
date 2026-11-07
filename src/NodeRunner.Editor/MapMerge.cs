using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// An object both versions changed in different ways: ours is kept.
public sealed record MergeConflict(uint ArchetypeHash, Vector3 Position, string Reason);

// What taking the changes of another version of a map file brings into ours.
public sealed class MergePlan
{
    public List<(MapEntity Entity, Placement Placement)> Moves { get; } = [];
    public List<MapEntity> Deletions { get; } = [];

    // Objects the other version added to the game's file: re-created as new objects.
    public List<(uint ArchetypeHash, Placement Placement)> Additions { get; } = [];

    public List<MergeConflict> Conflicts { get; } = [];

    // Changes of the other version ours already has.
    public int AlreadyThere { get; set; }

    public bool IsEmpty => Moves.Count == 0 && Deletions.Count == 0 && Additions.Count == 0;
}

// Three-way merge of a map file: the game's version (base), ours (in memory) and another one (a file of another
// project or resource made from the same game file). Each object is compared with the game's: a change made on one
// side only is taken, the same change on both sides is fine, different changes are a conflict.
public static class MapMerge
{
    public static MergePlan Plan(IReadOnlyList<MapEntity> gameEntities, IReadOnlyList<MapEntity> ours, IReadOnlyList<MapEntity> theirs)
    {
        var plan = new MergePlan();
        var ourMatch = Match(gameEntities, ours);
        var theirMatch = Match(gameEntities, theirs);

        for (int i = 0; i < gameEntities.Count; i++)
        {
            var original = gameEntities[i];
            var mine = ourMatch[i] >= 0 ? ours[ourMatch[i]] : null;
            var other = theirMatch[i] >= 0 ? theirs[theirMatch[i]] : null;
            var basePlacement = Placement.Of(original);
            bool theyDeleted = other is null;
            bool theyMoved = other is not null && !Same(basePlacement, Placement.Of(other));
            if (!theyDeleted && !theyMoved)
                continue;

            bool weDeleted = mine is null || mine.IsDeleted;
            bool weMoved = mine is { IsDeleted: false } && !Same(basePlacement, Placement.Of(mine));

            if (theyDeleted)
            {
                if (weDeleted)
                    plan.AlreadyThere++;
                else if (weMoved)
                    plan.Conflicts.Add(new(original.ArchetypeHash, original.Position, L("déplacé ici, supprimé dans l'autre version")));
                else if (mine!.CanEdit)
                    plan.Deletions.Add(mine);
            }
            else
            {
                var theirPlacement = Placement.Of(other!);
                if (weDeleted)
                    plan.Conflicts.Add(new(original.ArchetypeHash, original.Position, L("supprimé ici, déplacé dans l'autre version")));
                else if (weMoved)
                {
                    if (Same(Placement.Of(mine!), theirPlacement))
                        plan.AlreadyThere++;
                    else
                        plan.Conflicts.Add(new(original.ArchetypeHash, original.Position, L("déplacé différemment dans les deux versions")));
                }
                else if (mine!.CanEdit)
                    plan.Moves.Add((mine, theirPlacement));
            }
        }

        var matched = new HashSet<int>(theirMatch.Where(m => m >= 0));
        for (int j = 0; j < theirs.Count; j++)
        {
            if (!matched.Contains(j))
                plan.Additions.Add((theirs[j].ArchetypeHash, Placement.Of(theirs[j])));
        }
        return plan;
    }

    // For each game entity, the index of the same object in other (-1 if gone). Same type and guid first (when the pair
    // is unique on both sides), then same type in file order: saved files keep the order of the game's.
    public static int[] Match(IReadOnlyList<MapEntity> gameEntities, IReadOnlyList<MapEntity> other)
    {
        var result = Enumerable.Repeat(-1, gameEntities.Count).ToArray();
        var used = new bool[other.Count];

        var gameKeys = UniqueKeys(gameEntities);
        var otherKeys = UniqueKeys(other);
        foreach (var (key, i) in gameKeys)
        {
            if (otherKeys.TryGetValue(key, out int j))
            {
                result[i] = j;
                used[j] = true;
            }
        }

        var free = new Dictionary<uint, Queue<int>>();
        for (int j = 0; j < other.Count; j++)
        {
            if (used[j])
                continue;
            uint hash = other[j].ArchetypeHash;
            if (!free.TryGetValue(hash, out var queue))
                free[hash] = queue = new Queue<int>();
            queue.Enqueue(j);
        }
        for (int i = 0; i < gameEntities.Count; i++)
        {
            if (result[i] < 0 && free.TryGetValue(gameEntities[i].ArchetypeHash, out var queue) && queue.Count > 0)
                result[i] = queue.Dequeue();
        }
        return result;
    }

    private static Dictionary<(uint, uint), int> UniqueKeys(IReadOnlyList<MapEntity> entities)
    {
        var keys = new Dictionary<(uint, uint), int>();
        var repeated = new HashSet<(uint, uint)>();
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Guid == 0)
                continue;
            var key = (entities[i].ArchetypeHash, entities[i].Guid);
            if (!keys.TryAdd(key, i))
                repeated.Add(key);
        }
        foreach (var key in repeated)
            keys.Remove(key);
        return keys;
    }

    // Equal up to the precision lost when a file is written.
    public static bool Same(Placement a, Placement b) =>
        Vector3.DistanceSquared(a.Position, b.Position) < 1e-6f
        && Vector3.DistanceSquared(a.Scale, b.Scale) < 1e-8f
        && MathF.Abs(Quaternion.Dot(a.Orientation, b.Orientation)) > 0.99999f;
}

// The changes of another version taken in one undo step: objects moved, deleted and added.
public sealed class MergeEdit : IEdit
{
    private readonly MapLibrary _library;
    private readonly List<(MapEntity Entity, Placement Before, Placement After)> _moves;
    private readonly MapEntity[] _deleted;
    private readonly MapEntity[] _added;

    private MergeEdit(MapLibrary library, List<(MapEntity, Placement, Placement)> moves, MapEntity[] deleted, MapEntity[] added, string description)
    {
        _library = library;
        _moves = moves;
        _deleted = deleted;
        _added = added;
        Description = description;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public IEnumerable<MapEntity> Entities => _moves.Select(m => m.Entity).Concat(_deleted).Concat(_added);
    public IReadOnlyList<MapEntity> Selection => [];

    public IReadOnlyList<(MapEntity Entity, Placement Before, Placement After)> Moves => _moves;
    public IReadOnlyList<MapEntity> Deleted => _deleted;
    public IReadOnlyList<MapEntity> Added => _added;

    // added: new objects already made for plan.Additions, put in addTo (the project's own map).
    public static MergeEdit Apply(MapLibrary library, MergePlan plan, MapData? addTo, IReadOnlyList<MapEntity> added, string description)
    {
        var moves = plan.Moves.Select(m => (m.Entity, Placement.Of(m.Entity), m.Placement)).ToList();
        if (addTo is not null)
        {
            foreach (var entity in added)
                library.AddEntity(addTo, entity);
        }
        var edit = new MergeEdit(library, moves, [.. plan.Deletions], addTo is null ? [] : [.. added], description);
        foreach (var (entity, _, after) in moves)
            after.ApplyTo(entity);
        library.SetDeleted(edit._deleted, true);
        return edit;
    }

    public void Undo()
    {
        foreach (var (entity, before, _) in _moves)
            before.ApplyTo(entity);
        _library.SetDeleted(_deleted, false);
        _library.SetDeleted(_added, true);
    }

    public void Redo()
    {
        foreach (var (entity, _, after) in _moves)
            after.ApplyTo(entity);
        _library.SetDeleted(_deleted, true);
        _library.SetDeleted(_added, false);
    }
}
