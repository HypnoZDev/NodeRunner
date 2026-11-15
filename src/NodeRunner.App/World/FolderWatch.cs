namespace NodeRunner.App.World;

// Watches a linked export folder (Sollumz...) and hands over the paths that changed, once nothing has been written there
// for a moment (an export writes several files, sometimes in several passes).
internal sealed class FolderWatch : IDisposable
{
    private const int QuietMilliseconds = 1500;

    private readonly object _lock = new();
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private long _lastEvent;

    public string? Folder { get; private set; }

    // Starts watching folder (null = none); nothing happens if it is already the watched one.
    public void Watch(string? folder)
    {
        if (string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase) && (_watcher is not null || folder is null))
            return;
        Stop();
        Folder = folder;
        if (folder is null || !Directory.Exists(folder))
            return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => Mark(e.FullPath);
            watcher.Created += (_, e) => Mark(e.FullPath);
            watcher.Renamed += (_, e) => Mark(e.FullPath);
            // Too many changes at once (or the folder went away): everything is read again.
            watcher.Error += (_, _) => Mark(folder);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Network folder without notifications...: the folder can still be added by hand.
        }
    }

    private void Mark(string path)
    {
        lock (_lock)
        {
            _changed.Add(path);
            _lastEvent = Environment.TickCount64;
        }
    }

    // The paths changed since the last call, once writing has stopped and every changed file can be read; else null.
    public List<string>? TakeChanges()
    {
        lock (_lock)
        {
            if (_changed.Count == 0 || Environment.TickCount64 - _lastEvent < QuietMilliseconds)
                return null;
            foreach (string path in _changed)
            {
                if (!CanRead(path))
                {
                    _lastEvent = Environment.TickCount64;
                    return null;
                }
            }
            var changed = _changed.ToList();
            _changed.Clear();
            return changed;
        }
    }

    private static bool CanRead(string path)
    {
        if (!File.Exists(path))
            return true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_lock)
            _changed.Clear();
    }

    public void Dispose() => Stop();
}
