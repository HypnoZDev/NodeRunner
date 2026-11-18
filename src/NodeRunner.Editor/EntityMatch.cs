using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Pairs the objects of a game map file with those of another version of it (ours in memory, or another file), one to
// one, to tell which ones were deleted, moved or added. Several objects of one type are told apart by: their index in
// the game file (when the version was loaded from it), a unique guid, an unchanged placement, then the nearest leftover
// when that is clear. What stays undecided is Ambiguous: never treated as deleted or moved.
public static class EntityMatch
{
    public sealed record Result(int[] Match, bool[] Ambiguous, bool[] OtherAmbiguous)
    {
        // Game objects with no counterpart: deleted for sure.
        public IEnumerable<int> Removed => Enumerable.Range(0, Match.Length).Where(i => Match[i] < 0 && !Ambiguous[i]);
    }

    // A leftover is paired with its nearest when the next candidate is at least this much farther.
    private const float ClearRatio = 2f;

    // inMemory: other is a map being edited, whose objects without SourceIndex were added (created, pasted: a copy can
    // carry the guid of the object it comes from). fromGameFile: it was loaded from this very game file, so its
    // objects' SourceIndex is their index in game.
    public static Result Find(IReadOnlyList<MapEntity> game, IReadOnlyList<MapEntity> other, bool inMemory = false, bool fromGameFile = false)
    {
        var match = Enumerable.Repeat(-1, game.Count).ToArray();
        var ambiguous = new bool[game.Count];
        var otherAmbiguous = new bool[other.Count];
        var used = new bool[other.Count];

        bool IsNew(int j) => inMemory && other[j].SourceIndex < 0;
        if (inMemory && fromGameFile)
        {
            for (int j = 0; j < other.Count; j++)
            {
                int i = other[j].SourceIndex;
                if (i >= 0 && i < game.Count && match[i] < 0 && game[i].ArchetypeHash == other[j].ArchetypeHash)
                {
                    match[i] = j;
                    used[j] = true;
                }
            }
            return new Result(match, ambiguous, otherAmbiguous);
        }

        // A unique guid on both sides.
        var gameKeys = UniqueKeys(game);
        foreach (var (key, j) in UniqueKeys(other))
        {
            if (!IsNew(j) && gameKeys.TryGetValue(key, out int i))
            {
                match[i] = j;
                used[j] = true;
            }
        }

        var leftovers = new Dictionary<uint, (List<int> Game, List<int> Other)>();
        List<int> Group(uint type, bool isGame)
        {
            if (!leftovers.TryGetValue(type, out var lists))
                leftovers[type] = lists = ([], []);
            return isGame ? lists.Game : lists.Other;
        }
        for (int i = 0; i < game.Count; i++)
        {
            if (match[i] < 0)
                Group(game[i].ArchetypeHash, true).Add(i);
        }
        for (int j = 0; j < other.Count; j++)
        {
            if (!used[j] && !IsNew(j))
                Group(other[j].ArchetypeHash, false).Add(j);
        }

        foreach (var (games, others) in leftovers.Values)
        {
            // Unchanged placement (identical objects at the same place are interchangeable).
            foreach (int i in games.ToList())
            {
                var placement = Placement.Of(game[i]);
                int k = others.FindIndex(j => MapMerge.Same(placement, Placement.Of(other[j])));
                if (k < 0)
                    continue;
                match[i] = others[k];
                others.RemoveAt(k);
                games.Remove(i);
            }
            // Moved: the nearest, when it is clearly the one.
            bool progress = true;
            while (progress && games.Count > 0 && others.Count > 0)
            {
                progress = false;
                if (games.Count == 1 && others.Count == 1)
                {
                    match[games[0]] = others[0];
                    games.Clear();
                    others.Clear();
                    break;
                }
                foreach (int i in games.ToList())
                {
                    int j = Nearest(game[i].Position, others, other, out bool clear);
                    if (!clear || Nearest(other[j].Position, games, game, out bool clearBack) != i || !clearBack)
                        continue;
                    match[i] = j;
                    games.Remove(i);
                    others.Remove(j);
                    progress = true;
                }
            }
            if (games.Count > 0 && others.Count > 0)
            {
                foreach (int i in games)
                    ambiguous[i] = true;
                foreach (int j in others)
                    otherAmbiguous[j] = true;
            }
        }
        return new Result(match, ambiguous, otherAmbiguous);
    }

    private static int Nearest(Vector3 position, List<int> candidates, IReadOnlyList<MapEntity> entities, out bool clear)
    {
        int best = -1;
        float first = float.MaxValue, second = float.MaxValue;
        foreach (int k in candidates)
        {
            float d = Vector3.Distance(position, entities[k].Position);
            if (d < first)
            {
                second = first;
                first = d;
                best = k;
            }
            else if (d < second)
                second = d;
        }
        clear = candidates.Count == 1 || second >= first * ClearRatio && second - first > 0.5f;
        return best;
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
}
