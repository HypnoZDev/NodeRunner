using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;

namespace NodeRunner.App.Ui;

/// <summary>
/// Bar at the bottom of the window: map version and DLC, loading progress or errors on the left;
/// camera position, game edition and frames per second on the right.
/// </summary>
internal sealed class StatusBar(AppState state, ModelLayer modelLayer)
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
            Item(Icons.FolderOpen, "Aucun projet", UiTheme.TextFaint);
        Separator();
        Item(Icons.Globe, Toolbar.VersionName(settings.MapVersion));

        if (state.World.Content is { AllPacks.Count: > 0 } content)
        {
            var last = settings.LastDlcPack is null ? null
                : content.AllPacks.FirstOrDefault(p => p.Name.Equals(settings.LastDlcPack, StringComparison.OrdinalIgnoreCase));
            Separator();
            Item(Icons.Package, last is null ? $"Tous les DLC ({content.AllPacks.Count})" : $"DLC jusqu'à : {last.DisplayName}");
        }

        var world = state.World;
        if (world.Error is not null)
        {
            Separator();
            Item(Icons.TriangleAlert, world.Status, UiTheme.Error);
        }
        else if (world.IsBusy)
        {
            Separator();
            Item(Icons.LoaderCircle, world.Status, UiTheme.Accent with { X = 0.42f, Y = 0.70f, Z = 1f });
        }
        else if (modelLayer.PendingModelCount > 0)
        {
            Separator();
            Item(Icons.LoaderCircle, $"{modelLayer.PendingModelCount} modèles en cours de chargement", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        if (state.Notice is { } notice && (DateTime.Now - notice.Time).TotalSeconds < 8)
        {
            Separator();
            if (notice.IsWarning)
                Item(Icons.TriangleAlert, notice.Text, UiTheme.Warning);
            else
                Item(Icons.Check, notice.Text, UiTheme.Success);
        }

        if (state.History.HasUnsavedChanges)
        {
            Separator();
            Item(Icons.Save, $"Modifications non enregistrées ({state.MapsToSave.Count} YMAP) : Ctrl+S", UiTheme.Warning);
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
        string edition = state.Game is { } game ? $"{Icons.HardDrive}  GTA V {(game.Edition == GameEdition.Legacy ? "Legacy" : "Enhanced")}" : $"{Icons.HardDrive}  GTA V introuvable";
        string fps = $"{Icons.Gauge}  {ImGui.GetIO().Framerate:0} FPS";

        float gap = 24f;
        float width = ImGui.CalcTextSize(position).X + ImGui.CalcTextSize(edition).X + ImGui.CalcTextSize(fps).X + gap * 2;
        float x = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - width;
        ImGui.SameLine();
        if (x <= ImGui.GetCursorPosX() + gap)
            return; // Window too narrow: the left part wins.

        ImGui.SetCursorPosX(x);
        ImGui.TextDisabled(position);
        ImGui.SameLine(0, gap);
        ImGui.TextDisabled(edition);
        ImGui.SameLine(0, gap);
        ImGui.TextUnformatted(fps);
    }

    /// <summary>Icon and text; items after the first one are preceded by <see cref="Separator"/>, which keeps them on the line.</summary>
    private static void Item(string icon, string text, Vector4? color = null)
    {
        if (color is { } c)
            ImGui.TextColored(c, $"{icon}  {text}");
        else
            ImGui.TextUnformatted($"{icon}  {text}");
    }

    private static void Separator()
    {
        ImGui.SameLine(0, 12);
        ImGui.TextColored(UiTheme.Rgb(0x34363C), "|");
        ImGui.SameLine(0, 12);
    }
}
