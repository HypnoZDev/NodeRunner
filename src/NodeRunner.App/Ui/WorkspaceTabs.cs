using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Tabs above the 3D view while the model workshop is open: "Map" (the 3D view) and the workshop, shown in its place.
internal sealed unsafe class WorkspaceTabs(AppState state, ViewerPanel viewer)
{
    private const ImGuiWindowFlags BarFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollWithMouse;

    private bool _confirmClose;

    // After the dock space: takes the top of the 3D view for the tabs, then the workshop covers the rest when chosen.
    public void Draw()
    {
        if (state.WorkshopOpen && state.IsGta4)
            viewer.CloseWorkshop();
        if (!state.WorkshopOpen)
        {
            state.WorkshopShown = false;
            return;
        }

        var (position, size) = DockLayout.SceneArea;
        float height = ImGui.GetFrameHeight() + 8f;
        var mainViewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(mainViewport.ID);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(new Vector2(size.X, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool visible = ImGui.Begin("##workspace_tabs", BarFlags);
        ImGui.PopStyleVar(2);
        if (visible && ImGui.BeginTabBar("##workspace", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            bool front = state.WorkshopFront;
            state.WorkshopFront = false;
            if (TabItem(L($"{Icons.Map}  Carte###tab_map"), null, ImGuiTabItemFlags.None))
            {
                state.WorkshopShown = false;
                ImGui.EndTabItem();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(L("La carte en 3D"));

            bool open = true;
            var flags = (front ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)
                | (viewer.WorkshopChanged ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None);
            if (TabItem(L($"{Icons.Box}  Atelier : {viewer.WorkshopName ?? L("modèle")}###tab_workshop"), &open, flags))
            {
                state.WorkshopShown = true;
                ImGui.EndTabItem();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(viewer.WorkshopChanged
                    ? L("Le modèle en cours de modification (● : modifications pas encore enregistrées)")
                    : L("Le modèle en cours de modification"));
            if (!open)
            {
                if (viewer.WorkshopChanged)
                    _confirmClose = true;
                else
                    viewer.CloseWorkshop();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
        DockLayout.ReserveTop(height);
        DrawConfirmClose();

        if (state.WorkshopShown && state.WorkshopOpen)
            DrawWorkshop();
    }

    // The workshop over the 3D view, below the tabs.
    private void DrawWorkshop()
    {
        var (position, size) = DockLayout.SceneArea;
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool visible = ImGui.Begin("##workshop", BarFlags | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar(2);
        if (visible)
            viewer.DrawWorkshop();
        ImGui.End();
    }

    private void DrawConfirmClose()
    {
        const string id = "##workshop_close";
        if (_confirmClose)
        {
            ImGui.OpenPopup(id);
            _confirmClose = false;
        }
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        ImGui.TextUnformatted(L($"Les modifications de {viewer.WorkshopName} ne sont pas enregistrées."));
        Widgets.Hint(L("Fermer l'Atelier les abandonne (le fichier et la carte restent comme avant)."));
        ImGui.Spacing();
        if (Widgets.Button(L("Abandonner et fermer")))
        {
            viewer.CloseWorkshop();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Widgets.PrimaryButton(L("Continuer à modifier")))
        {
            state.WorkshopFront = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // A tab that can be brought to the front, with or without a close button.
    private static bool TabItem(string label, bool* open, ImGuiTabItemFlags flags)
    {
        int length = System.Text.Encoding.UTF8.GetByteCount(label);
        var bytes = stackalloc byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(label, new Span<byte>(bytes, length));
        bytes[length] = 0;
        byte value = open is null ? (byte)0 : (byte)(*open ? 1 : 0);
        bool shown = ImGuiNative.igBeginTabItem(bytes, open is null ? null : &value, flags) != 0;
        if (open is not null)
            *open = value != 0;
        return shown;
    }
}
