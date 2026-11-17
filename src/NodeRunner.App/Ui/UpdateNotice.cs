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
            if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Télécharger"), L("Ouvrir la page de la nouvelle version sur GitHub")))
            {
                OpenInBrowser(UpdateCheck.ReleasePage);
                _closed = true;
            }
            ImGui.SameLine();
            if (Widgets.Button(L("Plus tard"), L("Masquer jusqu'au prochain démarrage (Paramètres > Interface pour ne plus être prévenu)")))
                _closed = true;
        }
        ImGui.End();
        ImGui.PopStyleVar();
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
