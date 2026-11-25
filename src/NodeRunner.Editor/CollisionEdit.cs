using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A collision polygon of a game file (numbered as in CollisionMesh.Polygons).
public readonly record struct CollisionPolygon(string Path, int Polygon);

// Collision polygons removed, or given another material.
public sealed class CollisionEdit : IEdit
{
    private readonly CollisionEdits _edits;
    private readonly List<(CollisionPolygon Polygon, bool Removed, byte? Material)> _before = [];
    private readonly List<(CollisionPolygon Polygon, bool Removed, byte? Material)> _after = [];

    private CollisionEdit(CollisionEdits edits, string description, IReadOnlyList<CollisionPolygon> polygons)
    {
        _edits = edits;
        Description = description;
        Polygons = polygons;
        foreach (var p in polygons)
            _before.Add((p, edits.IsRemoved(p.Path, p.Polygon), edits.MaterialOf(p.Path, p.Polygon)));
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public IReadOnlyList<CollisionPolygon> Polygons { get; }

    public IReadOnlyList<(CollisionPolygon Polygon, bool Removed, byte? Material)> After => _after;

    // The same change made elsewhere (another computer): polygons brought to these states.
    public static CollisionEdit ToStates(CollisionEdits edits, string description, IReadOnlyList<(CollisionPolygon Polygon, bool Removed, byte? Material)> after)
    {
        var edit = new CollisionEdit(edits, description, [.. after.Select(a => a.Polygon)]);
        edit._after.AddRange(after);
        edit.Redo();
        return edit;
    }

    public static CollisionEdit Remove(CollisionEdits edits, IReadOnlyList<CollisionPolygon> polygons)
    {
        var edit = new CollisionEdit(edits, polygons.Count == 1 ? L("supprimer une surface de collision") : L($"supprimer {polygons.Count} surfaces de collision"), polygons);
        foreach (var (p, _, material) in edit._before)
            edit._after.Add((p, true, material));
        return edit;
    }

    public static CollisionEdit SetMaterial(CollisionEdits edits, IReadOnlyList<CollisionPolygon> polygons, byte material, string name)
    {
        var edit = new CollisionEdit(edits, L($"matériau de collision : {name}"), polygons);
        foreach (var (p, removed, _) in edit._before)
            edit._after.Add((p, removed, material));
        return edit;
    }

    public void Undo() => Apply(_before);
    public void Redo() => Apply(_after);

    private void Apply(List<(CollisionPolygon Polygon, bool Removed, byte? Material)> states)
    {
        foreach (var group in states.GroupBy(s => s.Polygon.Path))
        {
            _edits.SetRemoved(group.Key, group.Where(s => s.Removed).Select(s => s.Polygon.Polygon), removed: true);
            _edits.SetRemoved(group.Key, group.Where(s => !s.Removed).Select(s => s.Polygon.Polygon), removed: false);
            foreach (var s in group)
                _edits.SetMaterial(group.Key, s.Polygon.Polygon, s.Material);
        }
    }
}

// Options of collision polygons (CollisionFlags) switched on or off; null = the game's own options.
public sealed class CollisionFlagsEdit : IEdit
{
    private readonly CollisionEdits _edits;
    private readonly List<(CollisionPolygon Polygon, ushort? Flags)> _before = [];

    private CollisionFlagsEdit(CollisionEdits edits, string description, IReadOnlyList<(CollisionPolygon Polygon, ushort? Flags)> after)
    {
        _edits = edits;
        Description = description;
        After = after;
        foreach (var (p, _) in after)
            _before.Add((p, edits.FlagsOf(p.Path, p.Polygon)));
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public IReadOnlyList<(CollisionPolygon Polygon, ushort? Flags)> After { get; }

    // One option switched on (or off) on polygons whose present options are given (as shown: the game's, or changed
    // before); back to null when they are the game's own again.
    public static CollisionFlagsEdit Switch(CollisionEdits edits, IReadOnlyList<(CollisionPolygon Polygon, ushort Shown, ushort Game)> polygons,
        ushort flag, bool on, string name)
    {
        var after = polygons.Select(p =>
        {
            ushort value = on ? (ushort)(p.Shown | flag) : (ushort)(p.Shown & ~flag);
            return (p.Polygon, value == p.Game ? (ushort?)null : value);
        }).ToList();
        return new CollisionFlagsEdit(edits, on ? L($"collision : {name} activé") : L($"collision : {name} désactivé"), after);
    }

    // The same change made elsewhere (another computer): polygons brought to these options.
    public static CollisionFlagsEdit ToStates(CollisionEdits edits, string description, IReadOnlyList<(CollisionPolygon Polygon, ushort? Flags)> after)
    {
        var edit = new CollisionFlagsEdit(edits, description, after);
        edit.Redo();
        return edit;
    }

    public void Redo()
    {
        foreach (var (p, flags) in After)
            _edits.SetFlags(p.Path, p.Polygon, flags);
    }

    public void Undo()
    {
        foreach (var (p, flags) in _before)
            _edits.SetFlags(p.Path, p.Polygon, flags);
    }
}

// Door-sized openings cut through collision files (one per file, in the file's space).
public sealed class CollisionOpeningEdit(CollisionEdits edits, string description, IReadOnlyList<(string Path, CollisionOpening Opening)> openings) : IEdit
{
    public string Description { get; } = description;
    public MapEntity? Subject => null;
    public IReadOnlyList<(string Path, CollisionOpening Opening)> Openings { get; } = openings;

    public void Redo()
    {
        foreach (var (path, opening) in Openings)
            edits.AddOpening(path, opening);
    }

    public void Undo()
    {
        foreach (var (path, opening) in Openings.Reverse())
            edits.RemoveOpening(path, opening);
    }
}

// Collision polygons moved or turned (one move per file, in the file's space).
public sealed class CollisionMoveEdit : IEdit
{
    private readonly CollisionEdits _edits;

    public CollisionMoveEdit(CollisionEdits edits, string description, IReadOnlyList<(string Path, CollisionMove Move)> moves)
    {
        _edits = edits;
        Description = description;
        Moves = moves;
    }

    public string Description { get; }
    public MapEntity? Subject => null;
    public IReadOnlyList<(string Path, CollisionMove Move)> Moves { get; }

    public IEnumerable<CollisionPolygon> Polygons => Moves.SelectMany(m => m.Move.Polygons.Select(p => new CollisionPolygon(m.Path, p)));

    public void Redo()
    {
        foreach (var (path, move) in Moves)
            _edits.AddMove(path, move);
    }

    public void Undo()
    {
        foreach (var (path, move) in Moves.Reverse())
            _edits.RemoveMove(path, move);
    }
}
