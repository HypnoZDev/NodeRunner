namespace NodeRunner.App.World;

// The last files read, shared by several reading threads: a file asked by two threads at once is read only once.
internal sealed class RecentFileCache<T>(int capacity)
{
    private readonly Dictionary<string, Lazy<T>> _entries = new();
    private readonly Queue<string> _order = new();

    public T Get(string path, Func<string, T> read)
    {
        Lazy<T>? entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(path, out entry))
            {
                entry = new Lazy<T>(() => read(path), LazyThreadSafetyMode.ExecutionAndPublication);
                _entries[path] = entry;
                _order.Enqueue(path);
                if (_order.Count > capacity)
                    _entries.Remove(_order.Dequeue());
            }
        }
        return entry.Value;
    }
}
