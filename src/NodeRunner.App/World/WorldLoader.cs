using NodeRunner.Data.Collisions;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Paths;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.NavMeshes;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.NavMeshes;
using NodeRunner.Data.Native.Dlc;
using NodeRunner.Data.Native.Scenarios;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Paths;
using NodeRunner.Data.Scenarios;
using NodeRunner.Data.Textures;
using NodeRunner.Data.Water;

namespace NodeRunner.App.World;

// Opens the game archives and reads the map data on a background thread, so the window stays responsive.
internal sealed class WorldLoader : IDisposable
{
    private readonly object _lock = new();
    private int _generation;
    private RpfFileSystem? _files;

    public GameInstallation? Game { get; private set; }

    public bool IsBusy { get; private set; }

    public string Status { get; private set; } = L("Aucune donnée chargée.");

    public string? Error { get; private set; }

    // Key loading of the last opening of the archives (what was tried, what worked), for error reports.
    public GameArchiveKeys? Keys { get; private set; }

    public PathNetwork? PathNetwork { get; private set; }

    public ScenarioSet? Scenarios { get; private set; }

    public NavMeshCatalog? NavMeshes { get; private set; }

    public WaterMap? Water { get; private set; }

    public MapLibrary? Maps { get; private set; }

    public CollisionCatalog? Collisions { get; private set; }

    // The project's own files seen as game files (null without any).
    public LooseFileOverlay? ProjectFiles { get; private set; }

    public ModelCatalog? Models { get; private set; }

    public IModelReader? ModelReader { get; private set; }

    public TextureCatalog? Textures { get; private set; }

    public ITextureReader? TextureReader { get; private set; }

    public HashNames? Names { get; private set; }

    // Vehicle model sets (popGroup of the parking spots).
    public VehicleModelSets ModelSets { get; private set; } = VehicleModelSets.Empty;

    // Text file of names written by the user (one per line), added to those found in the game files; read with the
    // map.
    public string? UserNamesFile { get; init; }

    public (int Count, string? Error) UserNames { get; private set; }

    public GameContent? Content { get; private set; }

    // Vehicle palette, colours and sheets: read in the background the first time they are asked for (model viewer),
    // null until then.
    public VehicleData? Vehicles
    {
        get
        {
            if (Content is not { } content)
                return null;
            if (_vehicleDataContent != content)
            {
                _vehicleDataContent = content;
                _vehicleData = Task.Run(() => VehicleData.Load(content));
            }
            return _vehicleData is { IsCompletedSuccessfully: true } done ? done.Result : null;
        }
    }

    private GameContent? _vehicleDataContent;
    private Task<VehicleData>? _vehicleData;

    // Door tunings (doortuning.ymt): read in the background the first time a door is selected, null until then.
    public DoorTunings? DoorTunings
    {
        get
        {
            if (Content is not { } content)
                return null;
            if (_doorTuningContent != content)
            {
                _doorTuningContent = content;
                _doorTunings = Task.Run(() =>
                {
                    try
                    {
                        var tunings = DoorTunings.Load(content, new DoorTuningReader());
                        foreach (var error in tunings.Errors)
                            Diagnostics.ErrorLog.Add(L("Réglages des portes"), error);
                        return tunings;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException)
                    {
                        Diagnostics.ErrorLog.Add(L("Réglages des portes"), ex);
                        return Data.Maps.DoorTunings.Empty;
                    }
                });
            }
            return _doorTunings is { IsCompletedSuccessfully: true } done ? done.Result : null;
        }
    }

    private GameContent? _doorTuningContent;
    private Task<DoorTunings>? _doorTunings;

    public RpfFileSystem? Files => _files;

    public MapSelection Selection { get; private set; }

    private HashSet<string>? _hiddenScriptedMaps;

    public TimeSpan LoadDuration { get; private set; }

    // Starts loading game with this map selection (or clears everything if null).
    public void Load(GameInstallation? game, MapSelection selection, IReadOnlySet<string>? hiddenScriptedMaps, string? saveFolder)
    {
        _saveFolder = saveFolder;
        bool sameGame = SameGame(game, Game);
        bool sameHidden = hiddenScriptedMaps is null
            ? _hiddenScriptedMaps is null
            : _hiddenScriptedMaps is not null && _hiddenScriptedMaps.SetEquals(hiddenScriptedMaps);
        if (sameGame && selection == Selection && sameHidden && !_reloadRequested)
            return;
        _reloadRequested = false;
        var hidden = hiddenScriptedMaps?.ToHashSet();

        int generation;
        RpfFileSystem? openFiles;
        lock (_lock)
        {
            generation = ++_generation;
            Selection = selection;
            _hiddenScriptedMaps = hidden;
            Content = null;
            Maps = null;
            Collisions = null;
            Models = null;
            ModelReader = null;
            Textures = null;
            TextureReader = null;
            Names = null;
            Error = null;

            // The base data is complete once the navmesh index is published.
            openFiles = sameGame && NavMeshes is not null ? _files : null;
            if (openFiles is null)
            {
                Game = game;
                PathNetwork = null;
                Scenarios = null;
                NavMeshes = null;
                Water = null;
                _files?.Dispose();
                _files = null;
            }
        }

        if (game is null)
        {
            IsBusy = false;
            Status = L("Aucune donnée chargée.");
            return;
        }

        IsBusy = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (openFiles is not null)
        {
            Status = L("Changement de version de la carte...");
            Task.Run(() => LoadMaps(openFiles, selection, hidden, generation, clock));
        }
        else
        {
            Status = L("Ouverture des archives du jeu...");
            Task.Run(() => LoadInBackground(game, selection, hidden, generation, clock));
        }
    }

    private (int Count, string? Error) ReadUserNames(HashNames names)
    {
        if (UserNamesFile is not { } path || !File.Exists(path))
            return (0, null);
        try
        {
            return (names.AddLines(File.ReadLines(path)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, ex.Message);
        }
    }

    private void LoadInBackground(GameInstallation game, MapSelection selection, HashSet<string>? hiddenScriptedMaps, int generation, System.Diagnostics.Stopwatch clock)
    {
        var progress = new Progress(this, generation);
        var keys = new GameArchiveKeys();
        Keys = keys;
        var files = new RpfFileSystem(keys);
        try
        {
            files.Open(game, progress);

            if (!Publish(generation, () => _files = files))
            {
                files.Dispose();
                return; // Another installation was selected meanwhile.
            }

            progress.Report(L("Lecture des pathnodes..."));
            var network = PathNetwork.Load(files, new YndReader(), progress);
            if (!Publish(generation, () => PathNetwork = network))
                return;

            progress.Report(L("Lecture des scénarios..."));
            var scenarios = ScenarioSet.Load(files, new ScenarioRegionReader(), progress);
            if (!Publish(generation, () => Scenarios = scenarios))
                return;

            progress.Report(L("Lecture de l'eau..."));
            var water = WaterMap.Load(files);
            if (!Publish(generation, () => Water = water))
                return;

            progress.Report(L("Index des navmesh..."));
            var navMeshes = NavMeshCatalog.Create(files, new YnvReader());
            if (!Publish(generation, () => NavMeshes = navMeshes))
                return;
        }
        catch (Exception ex)
        {
            Fail(generation, ex);
            return;
        }

        LoadMaps(files, selection, hiddenScriptedMaps, generation, clock);
    }

    // Reads the DLC configuration, then the map objects, models and textures of this selection.
    private volatile string? _saveFolder;

    private bool _reloadRequested;

    public void RequestMapReload() => _reloadRequested = true;

    // Occluder map files of the save folder: rebuilt from the project's occlusion changes, not map files to load.
    public IReadOnlySet<string> OccluderFileNames { get; set; } = new HashSet<string>();

    // The .ymap files of the save folder, uncompressed, by file name; unreadable ones are reported in errors.
    private static Dictionary<string, byte[]> ReadSavedMaps(string? folder, IReadOnlySet<string> skipped, List<string> errors)
    {
        var maps = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder))
            return maps;
        foreach (var file in Directory.EnumerateFiles(folder, "*.ymap"))
        {
            if (skipped.Contains(Path.GetFileName(file)))
                continue;
            try
            {
                maps[Path.GetFileName(file)] = ResourceFile.Read(File.ReadAllBytes(file)).Content;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)} : {ex.Message}");
            }
        }
        return maps;
    }

    // The game's .ybn files that the project changed and wrote into its stream folder: not the project's own files.
    private static HashSet<string> GameCollisionFiles(string? streamFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (streamFolder is null || Path.GetDirectoryName(Path.GetFullPath(streamFolder)) is not { } project)
            return names;
        string file = Path.Combine(project, CollisionEdits.FileName);
        try
        {
            if (File.Exists(file))
            {
                foreach (var path in CollisionEdits.FromJson(File.ReadAllText(file)).Files.Keys)
                {
                    if (!path.StartsWith(GameContent.LooseFilesRoot + "\\", StringComparison.OrdinalIgnoreCase))
                        names.Add(Path.GetFileName(path));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Reported when the project loads its changes.
        }
        return names;
    }

    private void LoadMaps(RpfFileSystem files, MapSelection selection, HashSet<string>? hiddenScriptedMaps, int generation, System.Diagnostics.Stopwatch clock)
    {
        var progress = new Progress(this, generation);
        try
        {
            progress.Report(L("Lecture des packs DLC..."));
            // The project's own types, models and textures (a mapping with custom buildings) count as game files.
            var source = LooseFileOverlay.Over(files, _saveFolder, GameCollisionFiles(_saveFolder));
            var content = GameContent.Load(source, new DlcManifestReader(), selection);
            if (!Publish(generation, () => Content = content))
                return;

            progress.Report(L("Lecture des objets de la carte..."));
            var savedErrors = new List<string>();
            var saved = ReadSavedMaps(_saveFolder, OccluderFileNames, savedErrors);
            var maps = MapLibrary.Load(content, new YtypReader(), new YmapReader(), progress, hiddenScriptedMaps, saved);
            maps.SavedMapErrors.AddRange(savedErrors);

            progress.Report(L("Index des modèles 3D..."));
            var models = ModelCatalog.Create(content);

            progress.Report(L("Index des textures..."));
            var textures = TextureCatalog.Create(content, new GtxdReader());
            var names = HashNames.FromFileNames(content, maps.GetArchetypeHashes());
            var modelSets = VehicleModelSets.Empty;
            try
            {
                modelSets = VehicleModelSets.Load(files);
            }
            catch (InvalidDataException ex)
            {
                Diagnostics.ErrorLog.Add(L("Véhicules garés"), ex);
            }
            foreach (var name in modelSets.AllNames)
                names.Add(name);
            var userNames = ReadUserNames(names);

            progress.Report(L("Index des collisions..."));
            var collisions = CollisionCatalog.Create(content, new YbnReader(source), new MapCacheReader(),
                maps.GetInteriorArchetypeHashes());

            // Road nodes: the game's, with the project's saved files put back (unsaved moves are dropped, like the maps').
            var pathErrors = new List<string>();
            var paths = PathNetwork?.WithSavedAreas(files, new YndReader(), PathFileSaver.ReadSaved(_saveFolder, pathErrors), pathErrors);
            maps.SavedMapErrors.AddRange(pathErrors);

            lock (_lock)
            {
                if (generation != _generation)
                    return;
                Maps = maps;
                if (paths is not null)
                    PathNetwork = paths;
                Collisions = collisions;
                ProjectFiles = source as LooseFileOverlay;
                Models = models;
                ModelReader = new DrawableReader(source);
                Textures = textures;
                TextureReader = new YtdReader(source);
                Names = names;
                ModelSets = modelSets;
                UserNames = userNames;
                LoadDuration = clock.Elapsed;
                IsBusy = false;
                Status = L($"Chargé en {clock.Elapsed.TotalSeconds:0.0} s.");
            }
        }
        catch (Exception ex)
        {
            Fail(generation, ex);
        }
    }

    private void Fail(int generation, Exception ex)
    {
        lock (_lock)
        {
            if (generation != _generation)
                return;
            Error = ex.Message;
            IsBusy = false;
            Diagnostics.ErrorLog.Add(L("Chargement de la carte"), ex);
            Status = L($"Échec du chargement : {ex.Message}");
        }
    }

    private bool Publish(int generation, Action apply)
    {
        lock (_lock)
        {
            if (generation != _generation)
                return false;
            apply();
            return true;
        }
    }

    private static bool SameGame(GameInstallation? a, GameInstallation? b) =>
        a is null ? b is null : b is not null && string.Equals(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_lock)
        {
            _generation++;
            _files?.Dispose();
            _files = null;
        }
    }

    // Forwards progress messages, ignoring those of an outdated load.
    private sealed class Progress(WorldLoader owner, int generation) : IProgress<string>
    {
        public void Report(string value)
        {
            if (generation == owner._generation)
                owner.Status = value;
        }
    }
}
