using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

/// <summary>A change made to the map, which can be undone and done again.</summary>
public interface IEdit
{
    /// <summary>Short text for the interface, e.g. "Déplacer prop_bench_01".</summary>
    string Description { get; }

    /// <summary>The object changed (selected when the change is undone or redone), null if none.</summary>
    MapEntity? Subject { get; }

    /// <summary>Every object the change concerns (their map files are the ones to save).</summary>
    IEnumerable<MapEntity> Entities => Subject is { } subject ? [subject] : [];

    /// <summary>The objects selected when the change is undone or redone, the main one (<see cref="Subject"/>) last.</summary>
    IReadOnlyList<MapEntity> Selection => Subject is { } subject ? [subject] : [];

    void Undo();
    void Redo();
}

/// <summary>
/// The changes made to the map, in order, for "Annuler" (Ctrl+Z) and "Rétablir" (Ctrl+Y).
/// A new change after some were undone discards them, as in any editor.
/// </summary>
public sealed class EditHistory
{
    private readonly List<IEdit> _done = new();
    private readonly List<IEdit> _undone = new();

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    /// <summary>The change "Annuler" would undo, null if none.</summary>
    public IEdit? NextUndo => _done.Count > 0 ? _done[^1] : null;

    /// <summary>The change "Rétablir" would do again, null if none.</summary>
    public IEdit? NextRedo => _undone.Count > 0 ? _undone[^1] : null;

    /// <summary>Number of changes currently applied to the map.</summary>
    public int Count => _done.Count;

    /// <summary>Every change recorded, applied or undone (e.g. to know which map files were touched).</summary>
    public IEnumerable<IEdit> All => _done.Concat(_undone);

    /// <summary>False right after <see cref="MarkSaved"/>, until a change is made, undone or redone.</summary>
    public bool HasUnsavedChanges => NextUndo != _savedTop;

    private IEdit? _savedTop;

    /// <summary>Remembers that the map, as it is now, has been saved.</summary>
    public void MarkSaved() => _savedTop = NextUndo;

    /// <summary>Records a change that has already been applied.</summary>
    public void Add(IEdit edit)
    {
        _done.Add(edit);
        _undone.Clear();
    }

    /// <summary>Undoes the last change and returns it, null if there is none.</summary>
    public IEdit? Undo()
    {
        if (_done.Count == 0)
            return null;
        var edit = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        edit.Undo();
        _undone.Add(edit);
        return edit;
    }

    /// <summary>Does the last undone change again and returns it, null if there is none.</summary>
    public IEdit? Redo()
    {
        if (_undone.Count == 0)
            return null;
        var edit = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        edit.Redo();
        _done.Add(edit);
        return edit;
    }

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _savedTop = null;
    }
}
