using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Tabs above the 3D view: "Map" (the 3D view), the workshop and the ped viewer, each shown in its place, and "+" to open
// a viewer. As an option the workshop goes into its own window (to put on another screen), the map staying in the main
// one.
internal sealed unsafe class WorkspaceTabs(AppState state, ViewerPanel viewer, PedStudio peds, Action drawOpenMenu)
{
    private const ImGuiWindowFlags BarFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollWithMouse;

    private bool _confirmClose;
    private bool _detachedActive; // Detached: the workshop window was the last one used (not the map).

    // After the dock space: takes the top of the 3D view for the tabs, then the workshop covers the rest when chosen.
    public void Draw()
    {
        if (state.WorkshopOpen && state.IsGta4)
            viewer.CloseWorkshop();
        if (!state.WorkshopOpen)
        {
            state.WorkshopShown = false;
            state.WorkshopCoversMap = false;
        }
        if (!state.PedOpen)
            state.PedShown = false;
        // GTA IV has no viewer in a tab: no bar.
        if (state.IsGta4 && !state.WorkshopOpen && !state.PedOpen)
            return;
        bool detached = state.WorkshopOpen && state.WorkshopDetached;
        if (detached)
            DrawDetached();

        var (position, size) = DockLayout.SceneArea;
        float height = ImGui.GetFrameHeight() + 8f;
        var mainViewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(mainViewport.ID);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(new Vector2(size.X, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        bool visible = ImGui.Begin("##workspace_tabs", BarFlags);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        if (visible)
            OverlayStyle.WindowGlass(strong: false, rounding: 0f);
        if (visible)
            OverlayStyle.Sheen(ImGui.GetWindowDrawList(), ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(), 0f);
        if (visible && ImGui.BeginTabBar("##workspace", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            bool workshopTab = state.WorkshopOpen && !detached;
            bool front = workshopTab && state.WorkshopFront;
            if (workshopTab)
                state.WorkshopFront = false;
            bool pedFront = state.PedFront;
            state.PedFront = false;
            bool workshopShown = false, pedShown = false;
            if (TabItem(L($"{Icons.Map}  Carte###tab_map"), null, ImGuiTabItemFlags.None))
                ImGui.EndTabItem();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(L("La carte en 3D"));

            if (workshopTab)
            {
                bool open = true;
                var flags = (front ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)
                    | (viewer.WorkshopChanged ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None);
                if (TabItem(L($"{Icons.Box}  Atelier : {viewer.WorkshopName ?? L("modèle")}###tab_workshop"), &open, flags))
                {
                    workshopShown = true;
                    ImGui.EndTabItem();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(viewer.WorkshopChanged
                        ? L("Le modèle en cours de modification (● : modifications pas encore enregistrées)")
                        : L("Le modèle en cours de modification"));
                if (!open)
                    AskClose();
            }

            if (state.PedOpen)
            {
                bool open = true;
                if (TabItem(L($"{Icons.PersonStanding}  Personnage : {state.PedViewer.Item?.Name ?? L("aucun")}###tab_ped"), &open,
                        pedFront ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                {
                    pedShown = true;
                    ImGui.EndTabItem();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(L("La visionneuse de personnages : la liste est dans le panneau Scène, la tenue dans Propriétés"));
                if (!open)
                {
                    peds.Close();
                    pedShown = false;
                }
            }

            if (ImGui.TabItemButton($"{Icons.Plus}##tab_open", ImGuiTabItemFlags.NoTooltip))
                ImGui.OpenPopup("##tab_open_menu");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Ouvrir une visionneuse (modèles, personnages, cinématiques) ou l'Atelier"));
            if (ImGui.BeginPopup("##tab_open_menu"))
            {
                drawOpenMenu();
                ImGui.EndPopup();
            }

            if (workshopTab)
            {
                if (ImGui.TabItemButton(L($"{Icons.Monitor}  Détacher"), ImGuiTabItemFlags.Trailing))
                {
                    state.WorkshopDetached = true;
                    state.WorkshopFront = true;
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(L("L'Atelier dans sa propre fenêtre, à poser sur un autre écran ; la carte reste ici."));
                state.WorkshopShown = state.WorkshopCoversMap = workshopShown;
            }
            state.PedShown = pedShown;
            ImGui.EndTabBar();
        }
        ImGui.End();
        DockLayout.ReserveTop(height);
        DrawConfirmClose();

        if (state.PedShown)
            DrawCovering("##ped_tab", peds.DrawView);
        else if (state.WorkshopShown && state.WorkshopOpen && !detached)
            DrawCovering("##workshop", viewer.DrawWorkshop);
    }

    private void AskClose()
    {
        if (viewer.WorkshopChanged)
            _confirmClose = true;
        else
            viewer.CloseWorkshop();
    }

    // Detached: the workshop in a window of its own (it can leave NodeRunner's window), the map shown as usual.
    // Properties and Scene follow the last one used: the workshop window, or the map (a click in the 3D view).
    private void DrawDetached()
    {
        state.WorkshopCoversMap = false;
        ImGui.SetNextWindowSize(new Vector2(1100f, 750f), ImGuiCond.FirstUseEver);
        if (state.WorkshopFront)
        {
            state.WorkshopFront = false;
            ImGui.SetNextWindowFocus();
        }
        bool open = true;
        var flags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | (viewer.WorkshopChanged ? ImGuiWindowFlags.UnsavedDocument : ImGuiWindowFlags.None);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool visible = ImGui.Begin(L($"{Icons.Box}  Atelier : {viewer.WorkshopName ?? L("modèle")}###workshop_window"), ref open, flags);
        ImGui.PopStyleVar();
        if (visible)
        {
            if (Widgets.Button(L($"{Icons.Map}  Rattacher"), L("Remet l'Atelier en onglet à côté de la carte.")))
            {
                state.WorkshopDetached = false;
                state.WorkshopFront = true;
            }
            ImGui.SameLine();
            viewer.DrawWorkshop();
        }
        bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        ImGui.End();
        if (focused)
            _detachedActive = true;
        else if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow))
            _detachedActive = false;
        state.WorkshopShown = _detachedActive;
        if (!open)
            AskClose();
    }

    // A tab's content over the 3D view, below the tabs.
    private static void DrawCovering(string id, Action draw)
    {
        var (position, size) = DockLayout.SceneArea;
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        bool visible = ImGui.Begin(id, BarFlags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(3);
        if (visible)
            draw();
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
