using System.IO.Compression;
using NodeRunner.App.World;
using NodeRunner.Editor;
using NodeRunner.Editor.Online;

namespace NodeRunner.App.Online;

// NodeRunner Online on this computer: creating or joining a session, and the session's link with the open project.
internal sealed class OnlineController(AppState state) : IOnlineDocument
{
    private EditCodecs? _codecs;
    private Data.Maps.MapLibrary? _waitingFor;
    private bool _preparing;
    private bool _openingSnapshot;

    public OnlineSession? Session { get; private set; }

    public bool IsActive => Session is { IsOver: false } || _preparing;
    public bool IsHost => IsActive && (_preparing || Session!.Role == SessionRole.Host);
    public bool IsGuest => Session is { IsOver: false, Role: SessionRole.Guest };
    public bool IsPreparing => _preparing;

    // The project open before joining, to go back to it.
    public string? PreviousProject { get; private set; }

    public string? LastError { get; private set; }

    public string Name
    {
        get => state.Settings.OnlineName is { Length: > 0 } name ? name : Environment.UserName;
        set
        {
            state.Settings.OnlineName = value;
            state.SaveSettings();
        }
    }

    public static string Version => Program.Version;

    // --- IOnlineDocument ---

    public EditCodecs Codecs => _codecs ??= OnlineEdits.Create(state.World, () => state.OccluderFileName);

    public EditHistory History => state.History;

    public EditContext? Context => state.World is { Maps: { } maps, IsBusy: false } world
        ? new EditContext { Library = maps, Paths = world.PathNetwork, Collisions = state.CollisionEdits }
        : null;

    public bool IsReady => state.World is { Maps: { } maps, IsBusy: false } && maps != _waitingFor;

    public string Fingerprint => state.World.Maps is { } maps ? $"{maps.Maps.Count}-{maps.EntityCount}-{maps.ArchetypeCount}" : "";

    public void OpenSnapshot(SessionSnapshot snapshot)
    {
        string folder = Path.Combine(state.ProjectsFolder, "Online", MapProject.ToFolderName(snapshot.ProjectName));
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            using (var zip = new MemoryStream(snapshot.ProjectZip, writable: false))
                ZipFile.ExtractToDirectory(zip, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Session?.End($"Impossible d'écrire le projet de l'hôte ({folder}) : {ex.Message}");
            return;
        }
        _waitingFor = state.World.Maps;
        _openingSnapshot = true;
        string? error = state.OpenProject(folder);
        _openingSnapshot = false;
        if (error is not null)
            Session?.End($"Projet de l'hôte illisible : {error}");
    }

    public void Applied(IEdit edit, string author, SessionAction action)
    {
        if (author == Session?.Name)
        {
            state.SelectAfterUndo(edit, action == SessionAction.Undo);
            return;
        }
        state.Notify(action switch
        {
            SessionAction.Undo => $"{author} a annulé : {edit.Description}",
            SessionAction.Redo => $"{author} a rétabli : {edit.Description}",
            _ => $"{author} : {edit.Description}",
        });
    }

    public void Notify(string text, bool warning = false) => state.Notify(text, warning);

    // --- Starting and ending ---

    // A session sharing the open project: its changes are saved, then the map is loaded again from the project's
    // files, which every guest receives.
    public void Host()
    {
        LastError = null;
        if (IsActive)
            return;
        if (state.Project is not { } project)
        {
            LastError = "Ouvrez ou créez d'abord un projet : la session partage le projet ouvert.";
            return;
        }
        if (state.World is not { Maps: not null, IsBusy: false })
        {
            LastError = "Attendez la fin du chargement de la carte.";
            return;
        }
        if (state.PendingSaveCount > 0 || state.OccludersPending)
        {
            state.SaveMaps();
            if (state.LastSave is { Success: false } failed)
            {
                LastError = failed.Text;
                return;
            }
        }
        _preparing = true;
        _waitingFor = state.World.Maps;
        _openingSnapshot = true;
        string? error = state.OpenProject(project.Folder);
        _openingSnapshot = false;
        if (error is not null)
        {
            _preparing = false;
            LastError = error;
        }
    }

    private void FinishHosting()
    {
        _preparing = false;
        var project = state.Project!;
        try
        {
            using var zip = new MemoryStream();
            ZipFile.CreateFromDirectory(project.Folder, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            var snapshot = new SessionSnapshot(project.Name, zip.ToArray(), Fingerprint);
            Session = OnlineSession.Host(this, Name, Version, snapshot);
            state.Notify($"Session ouverte : donnez votre adresse à vos collègues (port {Session.Port}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"Impossible d'ouvrir la session : {ex.Message}";
        }
    }

    public void Join(string address)
    {
        LastError = null;
        if (IsActive)
            return;
        if (string.IsNullOrWhiteSpace(address))
        {
            LastError = "Entrez l'adresse de l'hôte.";
            return;
        }
        // The guest's own work is kept: saved in its project, or the join is refused.
        if (state.PendingSaveCount > 0 || state.OccludersPending)
        {
            if (state.Project is null)
            {
                LastError = "Vous avez des modifications non enregistrées : enregistrez-les dans un projet (Ctrl+S) avant de rejoindre.";
                return;
            }
            state.SaveMaps();
            if (state.LastSave is { Success: false } failed)
            {
                LastError = failed.Text;
                return;
            }
        }
        state.Settings.OnlineAddress = address.Trim();
        state.SaveSettings();
        PreviousProject = state.Project?.Folder;
        Session = OnlineSession.Join(this, Name, Version, address);
    }

    public void Leave()
    {
        Session?.End(IsHost ? "Session terminée." : "Vous avez quitté la session.");
        _preparing = false;
    }

    public void BackToPreviousProject()
    {
        if (IsActive)
            return;
        if (PreviousProject is { } folder)
            LastError = state.OpenProject(folder);
        else
            state.CloseProject();
        PreviousProject = null;
    }

    // Changing project or map would leave the others: not during a session (except to open the session's project).
    public string? Blocks()
    {
        if (_openingSnapshot || !IsActive)
            return null;
        return "Pas pendant une session Online : quittez-la d'abord (bouton Online).";
    }

    // Every frame.
    public void Update()
    {
        if (_preparing && IsReady)
            FinishHosting();
        Session?.Update();
    }
}
