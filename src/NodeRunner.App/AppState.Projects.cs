using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed partial class AppState
{
    public MapProject? Project { get; private set; }

    public string? SaveFolder => Project?.StreamFolder;

    // Other projects shown and edited with the open one (the active project): their maps and interiors are saved into
    // their own folders; added objects, collisions, occlusion and road nodes go to the active project.
    public IReadOnlyList<MapProject> ExtraProjects { get; private set; } = [];

    // Their stream folders, a new list at each change (the map loader compares it every frame).
    public IReadOnlyList<string> ExtraStreamFolders { get; private set; } = [];

    // The other open project a map was loaded from, or null (the active project's, or the game's).
    public MapProject? ExtraProjectOf(MapData map) =>
        ExtraProjects.Count > 0 && map.FilePath is { } path
        && World.ExtraMapFolders.TryGetValue(Path.GetFileName(path), out var stream)
            ? ExtraProjects.FirstOrDefault(p => string.Equals(p.StreamFolder, stream, StringComparison.OrdinalIgnoreCase))
            : null;

    // The other open project an archetype file (.ytyp) was loaded from, or null.
    private MapProject? ExtraProjectOfFile(string path) =>
        ExtraProjects.Count > 0 && World.ProjectFiles?.ExtraStreamOf(path) is { } stream
            ? ExtraProjects.FirstOrDefault(p => string.Equals(p.StreamFolder, stream, StringComparison.OrdinalIgnoreCase))
            : null;

    private void SetExtraProjects(IEnumerable<MapProject> projects)
    {
        ExtraProjects = projects.ToList();
        ExtraStreamFolders = ExtraProjects.Select(p => p.StreamFolder).ToList();
        Settings.ExtraProjectFolders = ExtraProjects.Select(p => p.Folder).ToList();
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Shows another project with the open one (the map is loaded again).
    public string? AddProjectToView(string folder)
    {
        if (Online.Blocks() is { } blocked)
            return blocked;
        if (Project is null)
            return L("Ouvrez d'abord un projet.");
        if (SameFolder(folder, Project.Folder) || ExtraProjects.Any(p => SameFolder(folder, p.Folder)))
            return L("Ce projet est déjà ouvert.");
        MapProject project;
        try
        {
            project = MapProject.Load(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return ex.Message;
        }
        if (ExtraProjects.Append(Project).FirstOrDefault(p => string.Equals(p.ResourceName, project.ResourceName, StringComparison.OrdinalIgnoreCase)) is { } same)
            return L($"Le projet « {same.Name} » porte déjà le nom de ressource FiveM « {project.ResourceName} ». Changez-le dans l'un des deux (fenêtre « Exporter en ressource FiveM », Ctrl+E) avant de les ouvrir ensemble.");
        SetExtraProjects(ExtraProjects.Append(project));
        AddRecentProject(project.Folder);
        SaveSettings();
        LastSave = null;
        LoadProjectEdits();
        World.RequestMapReload();
        return null;
    }

    // Stops showing another project (the map is loaded again).
    public void RemoveProjectFromView(string folder)
    {
        if (Online.Blocks() is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        if (!ExtraProjects.Any(p => SameFolder(folder, p.Folder)))
            return;
        SetExtraProjects(ExtraProjects.Where(p => !SameFolder(folder, p.Folder)));
        SaveSettings();
        LastSave = null;
        LoadProjectEdits();
        World.RequestMapReload();
    }

    // Makes another open project the active one (the map is loaded again; the projects shown stay the same).
    public void MakeProjectActive(string folder)
    {
        if (Online.Blocks() is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        if (Project is not { } previous || ExtraProjects.FirstOrDefault(p => SameFolder(folder, p.Folder)) is not { } next)
            return;
        var others = ExtraProjects.Select(p => p == next ? previous : p).ToList();
        UseProject(next);
        SetExtraProjects(others);
        SaveSettings();
    }

    // The other projects shown at the last start, opened again (those gone are dropped).
    private void RestoreExtraProjects()
    {
        var projects = new List<MapProject>();
        foreach (var folder in Settings.ExtraProjectFolders)
        {
            try
            {
                if (Project is { } active && SameFolder(folder, active.Folder) || projects.Any(p => SameFolder(folder, p.Folder)))
                    continue;
                projects.Add(MapProject.Load(folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                Diagnostics.ErrorLog.Add(L("Projet"), L($"Projet introuvable ou illisible ({folder}) : {ex.Message}"), ex);
            }
        }
        SetExtraProjects(projects);
    }

    // Files of the project's stream folder locked by FiveM's Asset Escrow (looked for once per project).
    public IReadOnlyList<string> ProtectedProjectFiles
    {
        get
        {
            string? folder = Project?.StreamFolder;
            if (folder != _protectedFolder)
            {
                _protectedFolder = folder;
                _protectedFiles = folder is null || !Directory.Exists(folder) ? []
                    : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Where(NodeRunner.Data.FileSystem.ProtectedFile.IsProtected)
                        .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            return _protectedFiles;
        }
    }

    private string? _protectedFolder;
    private IReadOnlyList<string> _protectedFiles = [];

    // Streamed files of the project's folder (an imported mod's models, textures, collisions...), copied by the FiveM
    // export; read again at most every 2 s, the toolbar asks every frame.
    public int ProjectFileCount
    {
        get
        {
            long now = Environment.TickCount64;
            if (_projectFiles.Folder != SaveFolder || now - _projectFiles.Time > 2000)
                _projectFiles = (SaveFolder, now, NodeRunner.App.World.ResourceExporter.ProjectFiles(SaveFolder).Count);
            return _projectFiles.Count;
        }
    }

    private (string? Folder, long Time, int Count) _projectFiles = (null, long.MinValue / 2, 0);

    public string? ProjectError { get; private set; }

    private const int MaxRecentProjects = 8;

    public string ProjectsFolder => Settings.ProjectsFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NodeRunner");

    // Prop library shared by every project (addon packs).
    public string LibraryFolder => Settings.LibraryFolder ?? DefaultLibraryFolder;

    public static string DefaultLibraryFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NodeRunner", "Bibliothèque");

    // Reads the library again (pack added, other folder): the map is loaded again, as when a project opens.
    public string? ReloadLibrary(string? folder)
    {
        if (Online.Blocks() is { } blocked)
            return blocked;
        Settings.LibraryFolder = folder;
        SaveSettings();
        World.LibraryFolder = LibraryFolder;
        LoadProjectEdits();
        World.RequestMapReload();
        return null;
    }

    public void RestoreProject()
    {
        MigrateSaveFolder();
        if (Settings.ProjectFolder is not { } folder)
            return;
        try
        {
            Project = MapProject.Load(folder);
            ApplyProjectMap(Project);
            RestoreExtraProjects();
            LoadProjectEdits();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ProjectError = L($"Projet introuvable ou illisible ({folder}) : {ex.Message}");
            Diagnostics.ErrorLog.Add(L("Projet"), ProjectError, ex);
            Settings.ProjectFolder = null;
            SetExtraProjects([]);
            SaveSettings();
        }
    }

    // The save folder of the versions before projects becomes the first project (its .ymap files go to "stream").
    private void MigrateSaveFolder()
    {
        if (Settings.SaveFolder is not { } folder)
            return;
        if (Settings.ProjectFolder is null && Directory.Exists(folder))
        {
            try
            {
                MapProject project;
                if (MapProject.Exists(folder))
                    project = MapProject.Load(folder);
                else
                {
                    project = MapProject.Create(folder, Path.GetFileName(Path.GetFullPath(folder)), Settings.MapVersion, Settings.LastDlcPack);
                    if (Settings.ExportResourceName is { } name && FiveMResource.IsValidName(name))
                        project.ResourceName = name;
                    project.ExportFolder = Settings.ExportFolder;
                    project.Save();
                }
                Settings.ProjectFolder = project.Folder;
                AddRecentProject(project.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                return; // Tried again at the next start.
            }
        }
        Settings.SaveFolder = null;
        SaveSettings();
    }

    // Projects are FiveM resources of the GTA V map.
    private static string Gta4ProjectsBlocked => L("Les projets sont des ressources FiveM pour GTA V : relance NodeRunner sur GTA V pour en ouvrir un.");

    // Opens the project of a folder (holding projet.json) and loads its map. The other projects shown are closed,
    // unless kept (the same project opened again).
    public string? OpenProject(string folder, bool keepOthers = false)
    {
        if (!Features.Editing)
            return Gta4ProjectsBlocked;
        if (Online.Blocks() is { } blocked)
            return blocked;
        MapProject project;
        try
        {
            project = MapProject.Load(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return ex.Message;
        }
        if (!keepOthers)
            SetExtraProjects([]);
        UseProject(project);
        return null;
    }

    // Creates a project in a folder and opens it.
    public string? CreateProject(string folder, string name)
    {
        if (!Features.Editing)
            return Gta4ProjectsBlocked;
        if (Online.Blocks() is { } blocked)
            return blocked;
        bool reload = Project is not null || MapProject.HasMapFiles(folder);
        MapProject project;
        try
        {
            project = MapProject.Create(folder, name, Settings.MapVersion, Settings.LastDlcPack);
            project.ExportFolder = Settings.ExportFolder;
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        if (ExtraProjects.Count > 0)
        {
            SetExtraProjects([]);
            reload = true;
        }
        UseProject(project, reload);
        return null;
    }

    public void CloseProject()
    {
        if (Project is null)
            return;
        if (Online.Blocks() is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        Project = null;
        Settings.ProjectFolder = null;
        SetExtraProjects([]);
        SaveSettings();
        LastSave = null;
        LoadProjectEdits();
        World.RequestMapReload();
    }

    private void UseProject(MapProject project, bool reload = true)
    {
        Project = project;
        if (ExtraProjects.Any(p => SameFolder(p.Folder, project.Folder)))
            SetExtraProjects(ExtraProjects.Where(p => !SameFolder(p.Folder, project.Folder)));
        ProjectError = null;
        LastSave = null;
        Settings.ProjectFolder = project.Folder;
        AddRecentProject(project.Folder);
        ApplyProjectMap(project);
        SaveSettings();
        // Without reload, the changes in progress go into the new project (they are kept).
        if (reload)
        {
            LoadProjectEdits();
            World.RequestMapReload();
        }
    }

    private void ApplyProjectMap(MapProject project)
    {
        Settings.MapVersion = project.MapVersion;
        Settings.LastDlcPack = project.LastDlcPack;
    }

    private void AddRecentProject(string folder)
    {
        Settings.RecentProjects.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        Settings.RecentProjects.Insert(0, folder);
        if (Settings.RecentProjects.Count > MaxRecentProjects)
            Settings.RecentProjects.RemoveRange(MaxRecentProjects, Settings.RecentProjects.Count - MaxRecentProjects);
    }

    public void ForgetRecentProject(string folder)
    {
        if (Settings.RecentProjects.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) > 0)
            SaveSettings();
    }

    // Map version and "map after this DLC" (toolbar): changing them reloads the map; kept by the open project.
    public void SetMapSelection(MapVersion version, string? lastPack)
    {
        if (Settings.MapVersion == version && Settings.LastDlcPack == lastPack)
            return;
        if (Online.Blocks() is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        Settings.MapVersion = version;
        Settings.LastDlcPack = lastPack;
        SaveSettings();
        if (Project is { } project)
        {
            project.MapVersion = version;
            project.LastDlcPack = lastPack;
            SaveProject();
        }
    }

    public string ExportResourceName
    {
        get => Project?.ResourceName ?? Settings.ExportResourceName ?? FiveMResource.DefaultName;
        set
        {
            Settings.ExportResourceName = value;
            SaveSettings();
            if (Project is { } project)
            {
                project.ResourceName = value;
                SaveProject();
            }
        }
    }

    public string? ExportFolder
    {
        get => Project?.ExportFolder ?? Settings.ExportFolder;
        set
        {
            Settings.ExportFolder = value;
            SaveSettings();
            if (Project is { } project)
            {
                project.ExportFolder = value;
                SaveProject();
            }
        }
    }

    private void SaveProject()
    {
        try
        {
            Project?.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProjectError = L($"projet.json non enregistré : {ex.Message}");
            Diagnostics.ErrorLog.Add(L("Projet"), ProjectError, ex);
        }
    }
}
