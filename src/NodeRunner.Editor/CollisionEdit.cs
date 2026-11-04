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

    public static CollisionEdit Remove(CollisionEdits edits, IReadOnlyList<CollisionPolygon> polygons)
    {
        var edit = new CollisionEdit(edits, polygons.Count == 1 ? "supprimer une surface de collision" : $"supprimer {polygons.Count} surfaces de collision", polygons);
        foreach (var (p, _, material) in edit._before)
            edit._after.Add((p, true, material));
        return edit;
    }

    public static CollisionEdit SetMaterial(CollisionEdits edits, IReadOnlyList<CollisionPolygon> polygons, byte material, string name)
    {
        var edit = new CollisionEdit(edits, $"matériau de collision : {name}", polygons);
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
