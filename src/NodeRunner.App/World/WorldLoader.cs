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

/// <summary>
/// Opens the game archives and reads the map data on a background thread, so the window stays responsive.
/// Each kind of data is published as soon as it is read (roads first, objects last), and the layers
/// rebuild their GPU data when the object they display changes.
/// </summary>
internal sealed class WorldLoader : IDisposable
{
    private readonly object _lock = new();
    private int _generation;
    private RpfFileSystem? _files;

    /// <summary>The installation being loaded or loaded, null if none.</summary>
    public GameInstallation? Game { get; private set; }

    public bool IsBusy { get; private set; }

    /// <summary>What is happening right now, in French, for the status panel.</summary>
    public string Status { get; private set; } = "Aucune donnée chargée.";

    /// <summary>Set when loading failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Road and pedestrian network, null until loaded.</summary>
    public PathNetwork? PathNetwork { get; private set; }

    /// <summary>Scenario points, null until loaded.</summary>
    public ScenarioSet? Scenarios { get; private set; }

    /// <summary>Index of the navmesh tiles (read on demand around the camera), null until loaded.</summary>
    public NavMeshCatalog? NavMeshes { get; private set; }

    /// <summary>Water surfaces (sea, lakes, rivers), null until loaded.</summary>
    public WaterMap? Water { get; private set; }

    /// <summary>Placed objects (.ymap) and object types (.ytyp), null until loaded.</summary>
    public MapLibrary? Maps { get; private set; }

    /// <summary>Index of the map collision files (read on demand around the camera), null until loaded.</summary>
    public CollisionCatalog? Collisions { get; private set; }

    /// <summary>Index of the 3D model files, null until loaded.</summary>
    public ModelCatalog? Models { get; private set; }

    /// <summary>Reads 3D models on demand, null until the archives are open.</summary>
    public IModelReader? ModelReader { get; private set; }

    /// <summary>Index of the texture dictionaries and their parents, null until loaded.</summary>
    public TextureCatalog? Textures { get; private set; }

    /// <summary>Reads texture dictionaries on demand, null until the archives are open.</summary>
    public ITextureReader? TextureReader { get; private set; }

    /// <summary>Readable names behind the hashes of the game files (null until the map is loaded).</summary>
    public HashNames? Names { get; private set; }

    /// <summary>
    /// Text file of names written by the user (one per line), added to those found in the game files; read with the map.
    /// The file is optional.
    /// </summary>
    public string? UserNamesFile { get; init; }

    /// <summary>Names read from <see cref="UserNamesFile"/> at the last load (0 if none), and the reading error if any.</summary>
    public (int Count, string? Error) UserNames { get; private set; }

    /// <summary>Which archives of the base game, update and DLC packs are used (null until the archives are open).</summary>
    public GameContent? Content { get; private set; }

    /// <summary>The map version and DLC packs being loaded or loaded.</summary>
    public MapSelection Selection { get; private set; }

    /// <summary>The maps loaded by scripts hidden (see <see cref="MapLibrary.Load"/>; null = default), being loaded or loaded.</summary>
    private HashSet<string>? _hiddenScriptedMaps;

    public TimeSpan LoadDuration { get; private set; }

    /// <summary>
    /// Starts loading <paramref name="game"/> with this map selection (or clears everything if null).
    /// Ignored if already loaded. When only the selection or the <paramref name="hiddenScriptedMaps"/> change, the archives,
    /// roads, scenarios and navmeshes are kept: only the map objects, models and textures are read again.
    /// </summary>
    /// <param name="saveFolder">Folder of the maps saved by the user: they replace the game's version when they match it.</param>
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
            Status = "Aucune donnée chargée.";
            return;
        }

        IsBusy = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (openFiles is not null)
        {
            Status = "Changement de version de la carte...";
            Task.Run(() => LoadMaps(openFiles, selection, hidden, generation, clock));
        }
        else
        {
            Status = "Ouverture des archives du jeu...";
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
        var files = new RpfFileSystem(new GameArchiveKeys());
        try
        {
            files.Open(game, progress);

            if (!Publish(generation, () => _files = files))
            {
                files.Dispose();
                return; // Another installation was selected meanwhile.
            }

            progress.Report("Lecture des pathnodes...");
            var network = PathNetwork.Load(files, new YndReader(), progress);
            if (!Publish(generation, () => PathNetwork = network))
                return;

            progress.Report("Lecture des scénarios...");
            var scenarios = ScenarioSet.Load(files, new ScenarioRegionReader(), progress);
            if (!Publish(generation, () => Scenarios = scenarios))
                return;

            progress.Report("Lecture de l'eau...");
            var water = WaterMap.Load(files);
            if (!Publish(generation, () => Water = water))
                return;

            progress.Report("Index des navmesh...");
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

    /// <summary>Reads the DLC configuration, then the map objects, models and textures of this selection.</summary>
    /// <summary>Folder of the saved maps, read when the map is loaded (changing it does not reload the map).</summary>
    private volatile string? _saveFolder;

    private bool _reloadRequested;

    /// <summary>The next <see cref="Load"/> reads the map objects again (another project: other saved maps).</summary>
    public void RequestMapReload() => _reloadRequested = true;

    /// <summary>The .ymap files of the save folder, uncompressed, by file name; unreadable ones are reported in <paramref name="errors"/>.</summary>
    private static Dictionary<string, byte[]> ReadSavedMaps(string? folder, List<string> errors)
    {
        var maps = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder))
            return maps;
        foreach (var file in Directory.EnumerateFiles(folder, "*.ymap"))
        {
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

    private void LoadMaps(RpfFileSystem files, MapSelection selection, HashSet<string>? hiddenScriptedMaps, int generation, System.Diagnostics.Stopwatch clock)
    {
        var progress = new Progress(this, generation);
        try
        {
            progress.Report("Lecture des packs DLC...");
            var content = GameContent.Load(files, new DlcManifestReader(), selection);
            if (!Publish(generation, () => Content = content))
                return;

            progress.Report("Lecture des objets de la carte...");
            var savedErrors = new List<string>();
            var saved = ReadSavedMaps(_saveFolder, savedErrors);
            var maps = MapLibrary.Load(content, new YtypReader(), new YmapReader(), progress, hiddenScriptedMaps, saved);
            maps.SavedMapErrors.AddRange(savedErrors);

            progress.Report("Index des modèles 3D...");
            var models = ModelCatalog.Create(content);

            progress.Report("Index des textures...");
            var textures = TextureCatalog.Create(content, new GtxdReader());
            var names = HashNames.FromFileNames(content, maps.GetArchetypeHashes());
            var userNames = ReadUserNames(names);

            progress.Report("Index des collisions...");
            var collisions = CollisionCatalog.Create(content, new YbnReader(files), new MapCacheReader(),
                maps.GetInteriorArchetypeHashes());

            lock (_lock)
            {
                if (generation != _generation)
                    return;
                Maps = maps;
                Collisions = collisions;
                Models = models;
                ModelReader = new DrawableReader(files);
                Textures = textures;
                TextureReader = new YtdReader(files);
                Names = names;
                UserNames = userNames;
                LoadDuration = clock.Elapsed;
                IsBusy = false;
                Status = $"Chargé en {clock.Elapsed.TotalSeconds:0.0} s.";
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
            Status = "Échec du chargement.";
        }
    }

    /// <summary>Makes freshly read data visible, unless another installation was selected meanwhile.</summary>
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

    /// <summary>Forwards progress messages, ignoring those of an outdated load.</summary>
    private sealed class Progress(WorldLoader owner, int generation) : IProgress<string>
    {
        public void Report(string value)
        {
            if (generation == owner._generation)
                owner.Status = value;
        }
    }
}
