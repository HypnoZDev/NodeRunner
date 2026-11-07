using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;

namespace NodeRunner.App.Ui;

// Bar at the bottom of the window: map version and DLC, loading progress or errors on the left; camera position, game
// edition and frames per second on the right.
internal sealed class StatusBar(AppState state, ModelLayer modelLayer, Action openErrors)
{
    private const float PaddingY = 3f;

    public void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, PaddingY));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Chrome);
        if (DockLayout.BeginSideBar("##statusbar", ImGuiDir.Down, ImGui.GetTextLineHeight() + PaddingY * 2 + 2))
        {
            DrawLeft();
            DrawRight();
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void DrawLeft()
    {
        var settings = state.Settings;
        if (state.Project is { } project)
            Item(Icons.FolderOpen, project.Name);
        else
            Item(Icons.FolderOpen, L("Aucun projet"), UiTheme.TextFaint);
        Separator();
        Item(Icons.Globe, Toolbar.VersionName(settings.MapVersion));

        if (state.World.Content is { AllPacks.Count: > 0 } content)
        {
            var last = settings.LastDlcPack is null ? null
                : content.AllPacks.FirstOrDefault(p => p.Name.Equals(settings.LastDlcPack, StringComparison.OrdinalIgnoreCase));
            Separator();
            Item(Icons.Package, last is null ? L($"Tous les DLC ({content.AllPacks.Count})") : L($"DLC jusqu'à : {last.DisplayName}"));
        }

        var world = state.World;
        if (world.Error is not null)
        {
            Separator();
            Item(Icons.TriangleAlert, world.Status, UiTheme.Error);
            Clickable(L("Clic : voir l'erreur et copier le rapport"));
        }
        else if (world.IsBusy)
        {
            Separator();
            Item(Icons.LoaderCircle, world.Status, UiTheme.Accent with { X = 0.42f, Y = 0.70f, Z = 1f });
        }
        else if (modelLayer.PendingModelCount > 0)
        {
            Separator();
            Item(Icons.LoaderCircle, L($"{modelLayer.PendingModelCount} modèles en cours de chargement"), ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        if (state.Notice is { } notice && (DateTime.Now - notice.Time).TotalSeconds < 8)
        {
            Separator();
            if (notice.IsWarning)
                Item(Icons.TriangleAlert, notice.Text, UiTheme.Warning);
            else
                Item(Icons.Check, notice.Text, UiTheme.Success);
        }

        if (state.Online.Session is { IsOver: false } session)
        {
            Separator();
            string host = session.Participants.Count > 0 ? session.Participants[0] : L("l'hôte");
            Item(Icons.Users, session.Role == Editor.Online.SessionRole.Host
                ? L($"Session Online : {session.Participants.Count} participant(s)")
                : L($"Session Online de {host} : c'est {host} qui enregistre"), UiTheme.Accent);
        }

        if (state.History.HasUnsavedChanges && !state.Online.IsGuest)
        {
            Separator();
            Item(Icons.Save, L($"Modifications non enregistrées ({state.PendingSaveText}) : Ctrl+S"), UiTheme.Warning);
        }
        else if (state.LastSave is { } save && (DateTime.Now - save.Time).TotalSeconds < 8)
        {
            Separator();
            Item(save.Success ? Icons.Check : Icons.TriangleAlert, save.Text, save.Success ? UiTheme.Success : UiTheme.Error);
        }
    }

    private void DrawRight()
    {
        var p = state.Camera.Position;
        string position = $"{Icons.MapPinned}  X {p.X:0}   Y {p.Y:0}   Z {p.Z:0}";
        string edition = state.Game is { } game ? L($"{Icons.HardDrive}  GTA V {(game.Edition == GameEdition.Legacy ? "Legacy" : "Enhanced")}") : L($"{Icons.HardDrive}  GTA V introuvable");
        string fps = $"{Icons.Gauge}  {ImGui.GetIO().Framerate:0} FPS";
        int errorCount = Diagnostics.ErrorLog.Count;
        string errors = errorCount > 0 ? $"{Icons.TriangleAlert}  {errorCount}" : Icons.TriangleAlert;

        float gap = 24f;
        float width = ImGui.CalcTextSize(errors).X + ImGui.CalcTextSize(position).X + ImGui.CalcTextSize(edition).X + ImGui.CalcTextSize(fps).X + gap * 3;
        float x = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - width;
        ImGui.SameLine();
        if (x <= ImGui.GetCursorPosX() + gap)
            return; // Window too narrow: the left part wins.

        ImGui.SetCursorPosX(x);
        if (errorCount > 0)
            ImGui.TextColored(Diagnostics.ErrorLog.Unseen > 0 ? UiTheme.Error : UiTheme.Warning, errors);
        else
            ImGui.TextDisabled(errors);
        Clickable(errorCount > 0 ? L($"{errorCount} erreur(s) : clic pour les voir et copier le rapport") : L("Aucune erreur. Clic : rapport à envoyer en cas de problème"));
        ImGui.SameLine(0, gap);
        ImGui.TextDisabled(position);
        ImGui.SameLine(0, gap);
        ImGui.TextDisabled(edition);
        ImGui.SameLine(0, gap);
        ImGui.TextUnformatted(fps);
    }

    private static void Item(string icon, string text, Vector4? color = null)
    {
        if (color is { } c)
            ImGui.TextColored(c, $"{icon}  {text}");
        else
            ImGui.TextUnformatted($"{icon}  {text}");
    }

    private void Clickable(string tooltip)
    {
        if (!ImGui.IsItemHovered())
            return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetTooltip(tooltip);
        if (ImGui.IsItemClicked())
            openErrors();
    }

    private static void Separator()
    {
        ImGui.SameLine(0, 12);
        ImGui.TextColored(UiTheme.Rgb(0x34363C), "|");
        ImGui.SameLine(0, 12);
    }
}
