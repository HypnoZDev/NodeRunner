using NodeRunner.Data.Hashing;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Classic;
using NodeRunner.Data.Textures;
using NodeRunner.Data.Water;

namespace NodeRunner.App.World;

// The GTA III, Vice City or San Andreas installation opened in this session: archives and map, read on a worker thread.
internal sealed class ClassicWorld : IOtherWorld, IDisposable
{
    private volatile bool _busy;
    private volatile string _status = "";
    private volatile string? _error;
    private volatile ClassicScene? _scene;
    private volatile WaterMap? _water;
    private volatile ClassicTimeCycle? _timeCycle;

    // A new set for each game opened: what was built from the previous one (archive tree, models) sees the change.
    public ClassicFileSystem Files { get; private set; } = new();

    public string? Folder { get; private set; }

    public ClassicGame Game { get; private set; } = ClassicGame.SanAndreas;

    public bool IsBusy => _busy;

    public string Status => _status;

    public string? Error => _error;

    public bool IsLoaded => !_busy && Folder is not null && _error is null;

    // The map as the 3D view draws it, set once read.
    public ClassicScene? Scene => _scene;

    public MapLibrary? Library => _scene?.Library;

    public ModelCatalog? Models => _scene?.Models;

    public TextureCatalog? Textures => _scene?.Textures;

    public HashNames? Names => _scene?.Names;

    public IModelReader ModelReader { get; private set; }

    public ITextureReader TextureReader { get; private set; }

    // Sea, rivers and pools, set once the map is read.
    public WaterMap? Water => _water;

    // Colours of the light and sky through the day ("data	imecyc.dat"), null when missing.
    public ClassicTimeCycle? TimeCycle => _timeCycle;

    public string GameName => ClassicInstallLocator.DisplayName(Game);

    public string MapFileKind => "IPL";

    public ClassicWorld()
    {
        ModelReader = new ClassicModelReader(Files);
        TextureReader = new ClassicTextureReader(Files);
    }

    public void Load(string folder, ClassicGame game)
    {
        if (_busy)
            return;
        _busy = true;
        _error = null;
        _scene = null;
        _water = null;
        _timeCycle = null;
        Folder = folder;
        Game = game;
        // The previous files are left to close by themselves: models of the previous map may still be reading them.
        Files = new ClassicFileSystem();
        ModelReader = new ClassicModelReader(Files);
        TextureReader = new ClassicTextureReader(Files);
        var files = Files;
        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                files.Open(folder, game, new Progress(s => _status = s));
                foreach (string error in files.Errors)
                    Diagnostics.ErrorLog.Add(GameName, error);
                _status = L("Lecture de la carte");
                var map = ClassicMap.Load(files);
                foreach (string error in map.Errors)
                    Diagnostics.ErrorLog.Add(L($"Carte de {GameName}"), error);
                var water = ClassicWater.Load(files, game);
                foreach (string error in water.Errors)
                    Diagnostics.ErrorLog.Add(L($"Eau de {GameName}"), error);
                _water = water;
                _timeCycle = ClassicTimeCycle.Load(files, game);
                if (_timeCycle is null)
                    Diagnostics.ErrorLog.Add(GameName, L(@"data\timecyc.dat introuvable : couleurs du ciel de GTA V"));
                if (ModelReader is ClassicModelReader reader)
                    reader.Definitions = map.Definitions;
                _scene = ClassicScene.Build(map, files);
                _status = L($"Chargé en {clock.Elapsed.TotalSeconds:0.0} s.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _error = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private readonly HashSet<MapEntity> _hiddenRooms = [];
    private ClassicScene? _hiddenFor;
    private ClassicInterior? _openRoom;

    // Objects of the rooms standing inside the city (most of Vice City's): the game draws them only with the player
    // inside, and they cover the buildings around (room seen through a window, low detail city behind it). Hidden,
    // except the room of the selected object.
    public IReadOnlySet<MapEntity> HiddenRooms(MapEntity? selected)
    {
        var scene = _scene;
        if (scene is null)
            return _hiddenRooms;
        if (!ReferenceEquals(scene, _hiddenFor))
        {
            _hiddenFor = scene;
            _openRoom = null;
            _hiddenRooms.Clear();
            foreach (var room in scene.Interiors.Where(r => !r.AboveCity))
                _hiddenRooms.UnionWith(room.Entities);
        }
        var open = selected is not null && scene.InteriorOf.TryGetValue(selected, out var r2) && !r2.AboveCity ? r2 : null;
        if (open != _openRoom)
        {
            if (_openRoom is not null)
                _hiddenRooms.UnionWith(_openRoom.Entities);
            if (open is not null)
                _hiddenRooms.ExceptWith(open.Entities);
            _openRoom = open;
        }
        return _hiddenRooms;
    }

    public void Dispose() => Files.Dispose();

    // Reports on the worker thread itself (no synchronization context needed for a status text).
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
