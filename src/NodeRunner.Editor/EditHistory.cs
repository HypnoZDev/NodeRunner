using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// A change made to the map, which can be undone and done again.
public interface IEdit
{
    string Description { get; }

    MapEntity? Subject { get; }

    IEnumerable<MapEntity> Entities => Subject is { } subject ? [subject] : [];

    IReadOnlyList<MapEntity> Selection => Subject is { } subject ? [subject] : [];

    void Undo();
    void Redo();
}

// The changes made to the map, in order, for "Annuler" (Ctrl+Z) and "Rétablir" (Ctrl+Y). A new change after some were
// undone discards them, as in any editor.
public sealed class EditHistory
{
    private readonly List<IEdit> _done = new();
    private readonly List<IEdit> _undone = new();

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    public IEdit? NextUndo => _done.Count > 0 ? _done[^1] : null;

    public IEdit? NextRedo => _undone.Count > 0 ? _undone[^1] : null;

    public int Count => _done.Count;

    public IEnumerable<IEdit> All => _done.Concat(_undone);

    public bool HasUnsavedChanges => NextUndo != _savedTop;

    private IEdit? _savedTop;

    public void MarkSaved() => _savedTop = NextUndo;

    public void Add(IEdit edit)
    {
        _done.Add(edit);
        _undone.Clear();
    }

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
