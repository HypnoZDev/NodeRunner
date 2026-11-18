using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// A newer NodeRunner is published: a small box in the top right corner of the 3D view, until closed (for this session)
// or followed. Settings > Interface turns the check off.
internal sealed class UpdateNotice
{
    private const float Margin = 12f;

    private readonly AppState _state;
    private readonly UpdateCheck _check = new();
    private bool _closed;

    public UpdateNotice(AppState state)
    {
        _state = state;
        if (state.Settings.CheckForUpdates)
            _check.Start(Program.Version);
    }

    public void Draw()
    {
        if (_closed || !_state.Settings.CheckForUpdates || _check.Available is not { } version)
            return;
        var (position, size) = DockLayout.SceneArea;
        if (size.X < 300f || size.Y < 160f)
            return;
        float width = MathF.Min(ImGui.GetFontSize() * 24f, size.X - Margin * 2f);

        ImGui.SetNextWindowPos(new Vector2(position.X + size.X - Margin, position.Y + Margin + 44f), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(width, size.Y * 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("##update_notice", flags))
        {
            OverlayStyle.WindowBackground(8f, strong: true);
            ImGui.TextColored(UiTheme.Accent, Icons.ArrowDownToLine);
            ImGui.SameLine();
            ImGui.PushTextWrapPos(width - 12f);
            ImGui.TextUnformatted(L($"NodeRunner {version} est disponible (vous avez la {Program.Version})."));
            ImGui.PopTextWrapPos();
            if (_task is { } task)
                DrawProgress(task);
            else
            {
                if (_error is { } error)
                {
                    ImGui.PushTextWrapPos(width - 12f);
                    ImGui.TextColored(UiTheme.Error, L($"Mise à jour impossible : {error}"));
                    ImGui.PopTextWrapPos();
                }
                if (_check.Package is { } package && _canWrite)
                {
                    bool unsaved = _state.History.HasUnsavedChanges;
                    if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Mettre à jour"),
                            unsaved ? L("Enregistre d'abord tes modifications (Ctrl+S)")
                                : L("Télécharger la nouvelle version, la mettre à la place de celle-ci et relancer NodeRunner (réglages gardés)"),
                            enabled: !unsaved))
                        Start(package, version);
                    ImGui.SameLine();
                }
                if (Widgets.Button(L("Page de téléchargement"), L("Ouvrir la page de la nouvelle version sur GitHub")))
                {
                    OpenInBrowser(UpdateCheck.ReleasePage);
                    _closed = true;
                }
                ImGui.SameLine();
                if (Widgets.Button(L("Plus tard"), L("Masquer jusqu'au prochain démarrage (Paramètres > Interface pour ne plus être prévenu)")))
                    _closed = true;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private Task? _task;
    private volatile float _progress;
    private string? _error;
    private readonly bool _canWrite = Updater.CanWrite(Updater.AppFolder);

    private void Start((string Url, string Sha256) package, string version)
    {
        _error = null;
        _progress = 0f;
        var progress = new Progress<double>(p => _progress = (float)p);
        _task = Task.Run(async () =>
        {
            string zip = await Updater.DownloadAsync(package.Url, package.Sha256, progress, CancellationToken.None).ConfigureAwait(false);
            try
            {
                Updater.Install(zip, Updater.AppFolder, version);
            }
            finally
            {
                try
                {
                    File.Delete(zip);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }

    private void DrawProgress(Task task)
    {
        if (!task.IsCompleted)
        {
            ImGui.ProgressBar(_progress, new Vector2(-1f, 0f), _progress > 0.999f ? L("Installation...") : L("Téléchargement..."));
            return;
        }
        _task = null;
        if (task.Exception?.InnerException is { } ex)
        {
            _error = ex is HttpRequestException or TaskCanceledException ? L($"téléchargement interrompu ({ex.Message})") : ex.Message;
            Diagnostics.ErrorLog.Add(L("Mise à jour"), ex);
            return;
        }
        try
        {
            Updater.Restart(Updater.AppFolder);
            System.Windows.Forms.Application.Exit();
        }
        catch (Exception restart) when (restart is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _error = L("la nouvelle version est installée : relance NodeRunner toi-même");
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser: the address goes to the clipboard.
            ImGui.SetClipboardText(url);
        }
    }
}
