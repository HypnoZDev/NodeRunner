using NodeRunner.Data.Native.Gta4;

namespace NodeRunner.App.World;

// The GTA IV installation opened in this session: archives, road network and map, read on a worker thread.
internal sealed class Gta4World : IDisposable
{
    private volatile bool _busy;
    private volatile string _status = "";
    private volatile string? _error;
    private volatile List<Gta4PathArea>? _paths;
    private volatile Gta4MapBoxes? _boxes;

    public Gta4FileSystem Files { get; } = new();

    public string? Folder { get; private set; }

    public bool IsBusy => _busy;

    public string Status => _status;

    public string? Error => _error;

    // Set once loading is over (empty if the paths could not be read).
    public List<Gta4PathArea>? PathAreas => _paths;

    // The placed objects, set once the map is read.
    public Gta4MapBoxes? MapBoxes => _boxes;

    public bool IsLoaded => !_busy && Folder is not null && _error is null;

    public void Load(string folder)
    {
        if (_busy)
            return;
        _busy = true;
        _error = null;
        _paths = null;
        _boxes = null;
        Folder = folder;
        Task.Run(() =>
        {
            try
            {
                Files.Open(folder, new Progress(s => _status = s));
                _status = L("Lecture des pathnodes");
                _paths = Gta4PathNodes.ReadAll(Files);
                _status = L("Lecture de la carte");
                var map = Gta4Map.Load(Files);
                foreach (string error in map.Errors)
                    Diagnostics.ErrorLog.Add(L("Carte de GTA IV"), error);
                _boxes = new Gta4MapBoxes(map);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _error = ex.Message;
                _paths ??= [];
            }
            finally
            {
                _busy = false;
            }
        });
    }

    public void Dispose() => Files.Dispose();

    // Reports on the worker thread itself (no synchronization context needed for a status text).
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
