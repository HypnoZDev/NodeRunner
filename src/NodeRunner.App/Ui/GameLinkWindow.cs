using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// Tools > Live link with the game: installs the "noderunner-link" resource on the mapper's own FiveM server.
internal sealed class GameLinkWindow(AppState state)
{
    public const string Title = $"{Icons.Monitor}  Lien avec le jeu (FiveM)###GameLink";

    private bool _open;
    private Task<List<string>>? _search;
    private List<string> _found = [];
    private string? _message;
    private bool _messageIsError;
    private bool _follow;
    private int _player;
    private readonly GameLinkPreview _preview = new();
    private NodeRunner.Data.Maps.MapLibrary? _previewMaps;
    private bool _livePreview = true, _previewDirty, _wasConnected;
    private DateTime _pushedAt;
    private int _previewCount;

    private ExportWindow? _export;
    private Task<World.ExportChecker.Result>? _sending;
    private (string Server, string Folder, string Name)? _sendTarget;
    private string? _sendText;
    private bool _sendFailed;

    // Follows the edits from the start, so that the preview knows what the game showed before them.
    public void Attach(ExportWindow export)
    {
        _export = export;
        var history = state.History;
        history.Added += edit => _preview.Observe(edit, undone: false);
        history.Redone += edit => _preview.Observe(edit, undone: false);
        history.Undone += edit => _preview.Observe(edit, undone: true);
    }

    public bool FolderRequested { get; set; }

    // Ctrl+S done: the export goes to the server too, when asked and the game is there.
    public void Saved()
    {
        if (state.Settings.GameLinkSendOnSave && state.Link.IsConnected && Server is not null)
            Send();
    }

    // Exports the project into the server's resource (checked first, in the background), then has the server reload it.
    public void Send()
    {
        if (_sending is not null || _export is null)
            return;
        if (state.Online.IsGuest)
        {
            Report(L("Session Online : seul l'hôte exporte le projet."), failed: true);
            return;
        }
        if (Server is not { } server || GameLinkResource.StateOf(server) == GameLinkResource.State.NotInstalled)
        {
            Report(L("Installe d'abord le lien sur ton serveur (Outils > Lien avec le jeu)."), failed: true);
            return;
        }
        if (!_export.HasChanges)
        {
            Report(L("Rien à envoyer : aucune modification dans le projet."), failed: true);
            return;
        }
        string name = state.ExportResourceName;
        if (!FiveMResource.IsValidName(name))
        {
            Report(L($"Nom de ressource invalide : « {name} » (Fichier > Exporter)."), failed: true);
            return;
        }
        string folder = GameLinkResource.MapFolder(server, name);
        if (World.ResourceExporter.Inspect(folder) == World.ResourceExporter.TargetState.Foreign)
        {
            Report(L($"Le serveur a déjà une ressource « {name} » qui n'a pas été créée par NodeRunner : change le nom dans Fichier > Exporter."), failed: true);
            return;
        }
        var input = World.ExportChecker.Prepare(state) with { ResourceName = name };
        var world = state.World;
        _sendTarget = (server, folder, name);
        _sendText = L($"{Icons.LoaderCircle}  Envoi de « {name} » au serveur...");
        _sendFailed = false;
        _sending = Task.Run(() => World.ExportChecker.Run(input, world));
    }

    private void FinishSend()
    {
        if (_sending is not { IsCompleted: true } sending || _sendTarget is not { } target || _export is null)
            return;
        _sending = null;
        _sendTarget = null;
        if (!sending.IsCompletedSuccessfully)
        {
            Report(L($"Échec de la vérification avant l'envoi : {sending.Exception?.GetBaseException().Message}"), failed: true);
            return;
        }
        if (!_export.ExportForLink(target.Folder, sending.Result, out string text))
        {
            Report(text, failed: true);
            return;
        }
        try
        {
            GameLinkResource.StartWithServer(target.Server, target.Folder, target.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(L($"Exporté, mais server.cfg n'a pas pu être modifié : {e.Message}"), failed: true);
            return;
        }
        state.Link.Restart(target.Name);
        // The server now streams what NodeRunner shows: nothing left to preview.
        _preview.Reset();
        _previewDirty = true;
        Report(L($"{Icons.Check}  « {target.Name} » envoyée au serveur et rechargée en jeu."), failed: false);
    }

    private void Report(string text, bool failed)
    {
        (_sendText, _sendFailed) = (text, failed);
        state.Notify(text, warning: failed);
    }

    public void Open()
    {
        _open = true;
        _message = null;
        if (Server is null && _search is null)
            StartSearch();
    }

    private string? Server => state.Settings.GameLinkServer;

    // Brings the installed resource up to this version of NodeRunner (files only: server.cfg already has its line).
    public static void UpdateInstalled(string? server)
    {
        if (server is null)
            return;
        Task.Run(() =>
        {
            try
            {
                if (GameLinkResource.IsServerFolder(server) && GameLinkResource.StateOf(server) == GameLinkResource.State.Outdated)
                    GameLinkResource.Install(server);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left for the window, which shows "update" and the error.
            }
        });
    }

    public void SetServer(string folder)
    {
        string? server = GameLinkResource.FindServerFolder(folder);
        if (server is null)
        {
            Show(L("Ce dossier n'est pas un serveur FiveM : il faut le dossier qui contient server.cfg et resources (ou un dossier dedans)."), error: true);
            return;
        }
        state.Settings.GameLinkServer = server;
        state.SaveSettings();
        _message = null;
    }

    private void StartSearch()
    {
        string? export = state.Settings.ExportFolder;
        _search = Task.Run(() =>
        {
            var found = new List<string>();
            if (export is not null && GameLinkResource.FindServerFolder(export) is { } fromExport)
                found.Add(fromExport);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                user,
                Path.Combine(user, "Downloads"),
            };
            roots.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName));
            foreach (string server in GameLinkResource.Search(roots))
                if (!found.Contains(server, StringComparer.OrdinalIgnoreCase))
                    found.Add(server);
            return found;
        });
    }

    public void Draw()
    {
        Follow();
        UpdatePreview();
        FinishSend();
        if (!_open)
            return;
        if (_search is { IsCompleted: true })
        {
            _found = _search.IsCompletedSuccessfully ? _search.Result : [];
            _search = null;
            if (Server is null && _found.Count == 1)
            {
                state.Settings.GameLinkServer = _found[0];
                state.SaveSettings();
            }
        }

        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(600f, 0f), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        const float wrap = 568f;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
        Widgets.Hint(L("NodeRunner installe une petite ressource sur ton serveur FiveM (celui de ton PC) pour parler au jeu pendant que tu mappes. ")
            + L("Elle ne change rien à tes autres ressources et peut être retirée d'un clic."), wrap: true);
        ImGui.Spacing();

        ImGui.TextUnformatted(L("Serveur FiveM"));
        if (Server is { } server)
        {
            Widgets.Hint(server, wrap: true);
        }
        else if (_search is not null)
        {
            ImGui.TextColored(UiTheme.TextMuted, L($"{Icons.LoaderCircle}  Recherche des serveurs sur ce PC..."));
        }
        else
        {
            ImGui.TextColored(UiTheme.Warning, _found.Count == 0
                ? L($"{Icons.TriangleAlert}  Aucun serveur trouvé : choisis son dossier (celui qui contient server.cfg).")
                : L($"{Icons.Info}  Plusieurs serveurs trouvés : choisis le tien."));
        }

        if (Server is null && _found.Count > 1)
        {
            foreach (string found in _found)
                if (ImGui.Selectable(found))
                {
                    state.Settings.GameLinkServer = found;
                    state.SaveSettings();
                }
        }
        ImGui.Spacing();
        if (Widgets.Button(L($"{Icons.FolderOpen}  Choisir le dossier du serveur...")))
            FolderRequested = true;
        if (Server is not null)
        {
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.Search}  Chercher à nouveau"), enabled: _search is null))
            {
                state.Settings.GameLinkServer = null;
                state.SaveSettings();
                StartSearch();
            }
        }

        if (Server is { } chosen && GameLinkResource.IsServerFolder(chosen))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawInstall(chosen);
            if (GameLinkResource.StateOf(chosen) == GameLinkResource.State.Installed)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawConnection();
            }
        }
        else if (Server is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Error, L($"{Icons.TriangleAlert}  Ce dossier n'existe plus ou n'a plus de server.cfg."));
        }

        if (_message is not null)
        {
            ImGui.Spacing();
            if (_messageIsError)
                ImGui.TextColored(UiTheme.Error, _message);
            else
                ImGui.TextColored(UiTheme.Success, _message);
        }
        ImGui.PopTextWrapPos();
    }

    private void DrawInstall(string server)
    {
        var current = GameLinkResource.StateOf(server);
        switch (current)
        {
            case GameLinkResource.State.Installed:
                ImGui.TextColored(UiTheme.Success, L($"{Icons.Check}  Ressource « {GameLinkResource.ResourceName} » installée."));
                break;
            case GameLinkResource.State.Outdated:
                ImGui.TextColored(UiTheme.Warning, L($"{Icons.RefreshCw}  Ressource installée mais ancienne : mets-la à jour."));
                break;
            default:
                ImGui.TextColored(UiTheme.TextMuted, L("Ressource pas encore installée."));
                break;
        }
        ImGui.Spacing();

        if (current != GameLinkResource.State.Installed)
        {
            string label = current == GameLinkResource.State.Outdated ? L($"{Icons.RefreshCw}  Mettre à jour") : L($"{Icons.Package}  Installer sur ce serveur");
            if (Widgets.PrimaryButton(label))
                Run(() => GameLinkResource.Install(server),
                    L("Installé. Redémarre ton serveur (ou tape « ensure noderunner-link » dans sa console), puis rejoins-le : un message « NodeRunner Studio » apparaît en jeu."));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L($"Copie la ressource dans resources\\{GameLinkResource.CategoryFolder} et ajoute « ensure {GameLinkResource.ResourceName} » à la fin de server.cfg\n(une copie de server.cfg est gardée à côté, en {GameLinkResource.ConfigFileName}{GameLinkResource.ConfigBackupSuffix})."));
        }
        else
        {
            Widgets.Hint(L("Si le serveur tournait déjà : redémarre-le ou tape « ensure noderunner-link » dans sa console."), wrap: true);
            ImGui.Spacing();
            if (Widgets.Button(L($"{Icons.FolderOpen}  Ouvrir le dossier")))
                OpenFolder(GameLinkResource.ResourceFolder(server));
        }

        if (current != GameLinkResource.State.NotInstalled || GameLinkResource.InstalledVersion(server) is not null)
        {
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.Trash}  Retirer du serveur"), L("Supprime la ressource et sa ligne dans server.cfg. Tes autres fichiers ne sont pas touchés.")))
                Run(() => GameLinkResource.Uninstall(server), L("Retiré du serveur."));
        }
    }

    private GameLinkPlayer? Player
    {
        get
        {
            var players = state.Link.Players;
            return players.Count == 0 ? null : players[Math.Clamp(_player, 0, players.Count - 1)];
        }
    }

    // The editor camera glued to the game camera while "follow" is on.
    private void Follow()
    {
        if (!_follow)
            return;
        if (Player is not { } player)
        {
            if (!state.Link.IsConnected)
                _follow = false;
            return;
        }
        CameraToGame(player);
    }

    private void UpdatePreview()
    {
        if (!ReferenceEquals(_previewMaps, state.World.Maps))
        {
            _previewMaps = state.World.Maps;
            _preview.Reset();
        }
        foreach (var entity in state.SelectedEntities)
            _preview.Watch(entity);
        if (_preview.Refresh())
            _previewDirty = true;

        bool connected = state.Link.IsConnected;
        if (connected && !_wasConnected)
            _previewDirty = true;
        _wasConnected = connected;
        var now = DateTime.UtcNow;
        if (!_previewDirty || !connected || now - _pushedAt < TimeSpan.FromMilliseconds(100))
            return;
        var items = _livePreview ? _preview.Items(Player?.Position ?? state.Camera.Position) : [];
        _previewCount = items.Count;
        state.Link.SetPreview(_preview.Revision, items);
        (_previewDirty, _pushedAt) = (false, now);
    }

    private void CameraToGame(GameLinkPlayer player)
    {
        state.Camera.Position = player.CameraPosition;
        state.Camera.Yaw = player.CameraYaw;
        state.Camera.Pitch = player.CameraPitch;
    }

    private void DrawConnection()
    {
        var link = state.Link;
        if (!link.IsRunning)
        {
            ImGui.TextColored(UiTheme.Error, L($"{Icons.TriangleAlert}  Le lien ne peut pas démarrer (port {GameLinkHost.DefaultPort} déjà pris ?) : {link.Error}"));
            return;
        }
        var players = link.Players;
        if (!link.IsConnected)
        {
            ImGui.TextColored(UiTheme.TextMuted, L($"{Icons.LoaderCircle}  En attente du jeu : lance ton serveur et rejoins-le."));
            return;
        }
        if (players.Count == 0)
        {
            ImGui.TextColored(UiTheme.Success, L($"{Icons.Check}  Serveur connecté, personne en jeu pour l'instant."));
            return;
        }

        ImGui.TextColored(UiTheme.Success, L($"{Icons.Check}  Jeu connecté."));
        if (players.Count > 1)
        {
            _player = Math.Clamp(_player, 0, players.Count - 1);
            ImGui.SetNextItemWidth(260f);
            if (ImGui.BeginCombo(L("Joueur"), players[_player].Name))
            {
                for (int i = 0; i < players.Count; i++)
                    if (ImGui.Selectable($"{players[i].Name}##{players[i].Id}", i == _player))
                        _player = i;
                ImGui.EndCombo();
            }
        }
        var player = Player!;
        Widgets.Hint(L($"{player.Name} : {player.Position.X:0.00}, {player.Position.Y:0.00}, {player.Position.Z:0.00}"));
        ImGui.Spacing();

        if (Widgets.PrimaryButton(L($"{Icons.Camera}  Caméra à ma position en jeu")))
            CameraToGame(player);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.PersonStanding}  Envoyer mon perso à la caméra"),
                L("Pose ton personnage (ou ton véhicule) au sol sous la caméra de NodeRunner, tourné dans le même sens.")))
            link.Teleport(player.Id, state.Camera.Position, GameLinkHost.HeadingFromYaw(state.Camera.Yaw));
        ImGui.Checkbox(L("Suivre la caméra du jeu"), ref _follow);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("La vue 3D de NodeRunner reprend en continu la caméra du jeu."));

        ImGui.Spacing();
        if (ImGui.Checkbox(L("Aperçu en direct"), ref _livePreview))
            _previewDirty = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Les objets déplacés, ajoutés, supprimés ou changés dans NodeRunner le sont aussitôt en jeu,\nsans rien enregistrer. Seulement pour toi, et effacé quand NodeRunner se ferme."));
        if (_livePreview)
        {
            ImGui.SameLine();
            Widgets.Hint(L($"{_previewCount} objet(s) changé(s) en jeu"));
            Widgets.Hint(L("Les props qui ne sont pas dans le jeu (ceux de ton projet) et les objets des intérieurs apparaissent à l'envoi au serveur."), wrap: true);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.BeginDisabled(_sending is not null);
        if (Widgets.PrimaryButton(L($"{Icons.Package}  Envoyer au serveur")))
            Send();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L($"Exporte le projet dans la ressource « {state.ExportResourceName} » du serveur (comme Fichier > Exporter)\npuis la recharge : props du projet, collisions, occlusion, tout est là pour de vrai."));
        ImGui.SameLine();
        bool onSave = state.Settings.GameLinkSendOnSave;
        if (ImGui.Checkbox(L("À chaque Ctrl+S"), ref onSave))
        {
            state.Settings.GameLinkSendOnSave = onSave;
            state.SaveSettings();
        }
        if (_sendText is not null)
        {
            if (_sendFailed)
                ImGui.TextColored(UiTheme.Error, _sendText);
            else
                ImGui.TextColored(_sending is null ? UiTheme.Success : UiTheme.TextMuted, _sendText);
        }
    }

    private void Run(Action action, string done)
    {
        try
        {
            action();
            Show(done, error: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show(L($"Impossible d'écrire dans le dossier du serveur : {e.Message}"), error: true);
        }
    }

    private void Show(string text, bool error) => (_message, _messageIsError) = (text, error);

    private static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
