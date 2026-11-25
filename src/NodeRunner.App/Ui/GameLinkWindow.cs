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

    public bool FolderRequested { get; set; }

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
