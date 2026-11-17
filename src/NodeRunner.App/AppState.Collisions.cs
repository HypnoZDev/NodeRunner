using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    // Hand changes to the collision files of the project.
    public Data.Collisions.CollisionEdits CollisionEdits { get; private set; } = new();

    private readonly HashSet<CollisionPolygon> _selectedCollision = [];

    public IReadOnlySet<CollisionPolygon> SelectedCollision => _selectedCollision;

    // Incremented when the collision selection changes (the 3D view rebuilds its highlight).
    public int CollisionSelectionVersion { get; private set; }

    public void SelectCollision(IEnumerable<CollisionPolygon> polygons, CollisionSelectMode mode)
    {
        if (mode == CollisionSelectMode.Replace)
            _selectedCollision.Clear();
        foreach (var polygon in polygons)
        {
            if (mode == CollisionSelectMode.Toggle && !_selectedCollision.Add(polygon))
                _selectedCollision.Remove(polygon);
            else
                _selectedCollision.Add(polygon);
        }
        CollisionSelectionVersion++;
    }

    public void DeleteSelectedCollision()
    {
        if (_selectedCollision.Count == 0)
            return;
        Apply(CollisionEdit.Remove(CollisionEdits, [.. _selectedCollision]));
        int count = _selectedCollision.Count;
        SelectCollision([], CollisionSelectMode.Replace);
        NotifyUndoable(count == 1 ? L("1 surface de collision supprimée") : L($"{count} surfaces de collision supprimées"));
    }

    // A move of objects (already made) with their map collision moved too (setting CollisionFollowsObjects).
    public IEdit FollowCollision(PlacementEdit edit)
    {
        edit.Collision = CollisionFollowing([(edit.Entity, edit.Before, edit.After)], edit.Description);
        return edit;
    }

    public IEdit FollowCollision(GroupPlacementEdit edit)
    {
        edit.Collision = CollisionFollowing(edit.Selection.Select((e, i) => (e, edit.Before[i], edit.After[i])), edit.Description);
        return edit;
    }

    private CollisionMoveEdit? CollisionFollowing(IEnumerable<(MapEntity Entity, Placement Before, Placement After)> moved, string description)
    {
        if (!Settings.CollisionFollowsObjects || World.Collisions is not { } catalog || World.Maps is not { } library)
            return null;
        var errors = new List<string>();
        var moves = CollisionRemoval.Follow(catalog, CollisionEdits,
            moved.Select(m => (m.Entity, library.GetArchetype(m.Entity.ArchetypeHash), m.Before, m.After)), errors);
        if (errors.Count > 0)
            Notify(L($"Collision illisible : {errors[0]}"), warning: true);
        if (moves.Count == 0)
            return null;
        var edit = new CollisionMoveEdit(CollisionEdits, description, moves);
        edit.Redo();
        int count = moves.Sum(m => m.Move.Polygons.Length);
        Notify(count == 1 ? L("1 surface de collision déplacée avec l'objet") : L($"{count} surfaces de collision déplacées avec l'objet"));
        return edit;
    }

    // While the handles are dragged: how the selected polygons are moved (world space), drawn over the collision.
    public System.Numerics.Matrix4x4? CollisionDragPreview { get; set; }

    // Moves the selected polygons by world (world space). placements: where each file is shown (an interior's file is
    // in the interior's space), to turn the move into the file's space.
    public void MoveSelectedCollision(System.Numerics.Matrix4x4 world, IReadOnlyDictionary<string, System.Numerics.Matrix4x4> placements,
        string description)
    {
        var moves = new List<(string Path, Data.Collisions.CollisionMove Move)>();
        foreach (var group in _selectedCollision.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            var toWorld = placements.GetValueOrDefault(group.Key, System.Numerics.Matrix4x4.Identity);
            if (!System.Numerics.Matrix4x4.Invert(toWorld, out var toFile))
                continue;
            moves.Add((group.Key, Data.Collisions.CollisionMove.Of(group.Select(p => p.Polygon), toWorld * world * toFile)));
        }
        if (moves.Count > 0)
            Apply(new CollisionMoveEdit(CollisionEdits, description, moves));
    }

    public void SetSelectedCollisionMaterial(byte material)
    {
        if (_selectedCollision.Count == 0 || World.Collisions is not { } catalog)
            return;
        Apply(CollisionEdit.SetMaterial(CollisionEdits, [.. _selectedCollision], material, catalog.Materials.Name(material)));
        CollisionSelectionVersion++;
    }
}
