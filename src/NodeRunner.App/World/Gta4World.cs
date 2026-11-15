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

    // Game or episode whose map is shown.
    public Gta4Episode Episode { get; private set; }

    // Episodes whose files are present (GTA IV first), known once the archives are open.
    public IReadOnlyList<Gta4Episode> InstalledEpisodes { get; private set; } = [];

    public void Load(string folder, Gta4Episode episode)
    {
        if (_busy)
            return;
        _busy = true;
        _error = null;
        _paths = null;
        _boxes = null;
        Folder = folder;
        Episode = episode;
        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Files.Open(folder, new Progress(s => _status = s));
                InstalledEpisodes = Enum.GetValues<Gta4Episode>().Where(e => Gta4Map.IsInstalled(Files, e)).ToList();
                if (!InstalledEpisodes.Contains(episode))
                    Episode = Gta4Episode.Base;
                ReadMap(clock);
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

    // Shows the map of another episode; the archives stay open.
    public void SetEpisode(Gta4Episode episode)
    {
        if (_busy || !IsLoaded || episode == Episode)
            return;
        _busy = true;
        _paths = null;
        _boxes = null;
        Episode = episode;
        Task.Run(() =>
        {
            try
            {
                ReadMap(System.Diagnostics.Stopwatch.StartNew());
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

    private void ReadMap(System.Diagnostics.Stopwatch clock)
    {
        _status = L("Lecture des pathnodes");
        _paths = Gta4PathNodes.ReadAll(Files, Episode);
        _status = L("Lecture de la carte");
        var map = Gta4Map.Load(Files, Episode);
        foreach (string error in map.Errors)
            Diagnostics.ErrorLog.Add(L("Carte de GTA IV"), error);
        _boxes = new Gta4MapBoxes(map);
        _status = L($"Chargé en {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    public static string EpisodeName(Gta4Episode episode) => episode switch
    {
        Gta4Episode.LostAndDamned => "The Lost and Damned",
        Gta4Episode.BalladOfGayTony => "The Ballad of Gay Tony",
        _ => "GTA IV",
    };

    public void Dispose() => Files.Dispose();

    // Reports on the worker thread itself (no synchronization context needed for a status text).
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
