using System.IO.Compression;
using System.Numerics;
using NodeRunner.Data.Maps;
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

    public const string DefaultRelay = "wss://noderunner-relay.insiderockstar.workers.dev";

    // The relay: NODERUNNER_RELAY, then settings.json, then NodeRunner's own.
    public string RelayUrl =>
        Environment.GetEnvironmentVariable("NODERUNNER_RELAY") is { Length: > 0 } env ? env
        : state.Settings.OnlineRelay is { Length: > 0 } set ? set
        : DefaultRelay;

    private bool _local;

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
            Session?.End(L($"Impossible d'écrire le projet de l'hôte ({folder}) : {ex.Message}"));
            return;
        }
        _waitingFor = state.World.Maps;
        _openingSnapshot = true;
        string? error = state.OpenProject(folder);
        _openingSnapshot = false;
        if (error is not null)
            Session?.End(L($"Projet de l'hôte illisible : {error}"));
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
            SessionAction.Undo => L($"{author} a annulé : {edit.Description}"),
            SessionAction.Redo => L($"{author} a rétabli : {edit.Description}"),
            _ => $"{author} : {edit.Description}",
        });
    }

    public void Notify(string text, bool warning = false) => state.Notify(text, warning);

    // --- Starting and ending ---

    // A session sharing the open project: its changes are saved, then the map is loaded again from the project's
    // files, which every guest receives.
    // local: on this network only, joined by address (no relay).
    public void Host(bool local = false)
    {
        LastError = null;
        _local = local;
        if (IsActive)
            return;
        if (state.Project is not { } project)
        {
            LastError = L("Ouvrez ou créez d'abord un projet : la session partage le projet ouvert.");
            return;
        }
        if (state.World is not { Maps: not null, IsBusy: false })
        {
            LastError = L("Attendez la fin du chargement de la carte.");
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
            if (_local)
            {
                Session = OnlineSession.Host(this, Name, Version, snapshot);
                state.Notify(L($"Session ouverte : donnez votre adresse à vos collègues (port {Session.Port})."));
            }
            else
            {
                Session = OnlineSession.HostOnRelay(this, Name, Version, snapshot, RelayUrl);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = L($"Impossible d'ouvrir la session : {ex.Message}");
        }
    }

    public void Join(string address)
    {
        LastError = null;
        if (IsActive)
            return;
        if (string.IsNullOrWhiteSpace(address))
        {
            LastError = L("Entrez le code de la session (NRS-XXXX) ou l'adresse de l'hôte.");
            return;
        }
        // The guest's own work is kept: saved in its project, or the join is refused.
        if (state.PendingSaveCount > 0 || state.OccludersPending)
        {
            if (state.Project is null)
            {
                LastError = L("Vous avez des modifications non enregistrées : enregistrez-les dans un projet (Ctrl+S) avant de rejoindre.");
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
        Session = OnlineSession.Join(this, Name, Version, address, RelayUrl);
    }

    public void Leave()
    {
        Session?.End(IsHost ? L("Session terminée.") : L("Vous avez quitté la session."));
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
        return L("Pas pendant une session Online : quittez-la d'abord (bouton Online).");
    }

    // Every frame.
    public void Update()
    {
        if (_preparing && IsReady)
            FinishHosting();
        string? code = Session?.Code;
        Session?.Update();
        if (Session is { Role: SessionRole.Host, IsOver: false, Code: { } newCode } && code is null)
            state.Notify(L($"Session ouverte : donnez le code {newCode} à vos collègues."));
        UpdatePresence();
    }

    // --- Presence: what the others have selected ---

    // Another participant's selection, in its color (the same on every computer: its place in the participant list).
    public sealed record Peer(string Name, Vector4 Color, IReadOnlyList<MapEntity> Entities);

    private static readonly Vector4[] PeerColors =
    [
        new(0.20f, 0.85f, 0.75f, 1f),
        new(1.00f, 0.40f, 0.65f, 1f),
        new(0.55f, 0.90f, 0.25f, 1f),
        new(0.72f, 0.52f, 1.00f, 1f),
        new(0.35f, 0.78f, 1.00f, 1f),
    ];

    public static Vector4 ColorOf(int participant) => PeerColors[participant % PeerColors.Length];

    private List<MapEntity> _shared = [];
    private OnlineSession? _sharedWith;
    private List<Peer> _peers = [];
    private readonly Dictionary<MapEntity, string> _locks = new(ReferenceEqualityComparer.Instance);
    private (OnlineSession?, int, int, object?, MapLibrary?) _peersFrom;

    public IReadOnlyList<Peer> Peers => _peers;

    // The participant who has this object selected (null: free, or selected here).
    public string? LockedBy(MapEntity entity) => _locks.Count > 0 && _locks.TryGetValue(entity, out var name) ? name : null;

    private void UpdatePresence()
    {
        if (Session is not { IsOver: false, IsLoading: false } session || state.World is not { Maps: { } library, IsBusy: false }
            || Context is not { } context)
        {
            if (_peers.Count > 0 || _locks.Count > 0)
            {
                _peers = [];
                _locks.Clear();
            }
            _sharedWith = null;
            return;
        }

        var selected = state.SelectedEntities;
        if (_sharedWith != session || !selected.SequenceEqual(_shared, ReferenceEqualityComparer.Instance))
        {
            _shared = [.. selected];
            _sharedWith = session;
            session.ShareSelection(SelectionCodec.Encode(library, _shared));
        }

        var from = (session, session.PresenceVersion, session.ChangeCount, (object)session.Participants, library);
        if (from == _peersFrom)
            return;
        _peersFrom = from;
        _peers = [];
        _locks.Clear();
        var participants = session.Participants;
        foreach (var (name, data) in session.Presence)
        {
            int index = IndexOf(participants, name);
            if (name == session.Name || index < 0)
                continue;
            var entities = SelectionCodec.Decode(data, context);
            if (entities.Count == 0)
                continue;
            _peers.Add(new Peer(name, ColorOf(index), entities));
            foreach (var entity in entities)
                _locks.TryAdd(entity, name);
        }
        _peers.Sort((a, b) => IndexOf(participants, a.Name).CompareTo(IndexOf(participants, b.Name)));

        // Both took the same object at the same moment: the one higher in the list keeps it.
        int me = IndexOf(participants, session.Name);
        var lost = selected.Where(e => LockedBy(e) is { } other && IndexOf(participants, other) < me).ToList();
        if (lost.Count > 0)
        {
            state.Deselect(lost);
            state.Notify(L($"{LockedBy(lost[0])} a sélectionné cet objet en même temps que vous : il lui revient."), warning: true);
        }
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
                return i;
        }
        return -1;
    }
}
