using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.App.Ui;

// Bar under the menu: project (with its menu), game folder, save, undo / redo, map version (multiplayer or story
// mode) and "map after this DLC", ground grid, global search (Ctrl+K).
internal sealed class Toolbar(AppState state, Action drawProjectMenu, Action browseGameFolder, Action openSearch, Action save, Action export, Action toggleOnline)
{
    private const float PaddingY = 5f;

    private IReadOnlyList<DlcPackInfo> _knownPacks = [];

    public void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, PaddingY));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Chrome);
        if (DockLayout.BeginSideBar("##toolbar", ImGuiDir.Up, ImGui.GetFrameHeight() + PaddingY * 2))
            DrawContent();
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void DrawContent()
    {
        DrawProject();
        Widgets.ToolbarSeparator();
        if (Widgets.ToolbarButton(Icons.HardDrive, "GTA V", L("Choisir le dossier d'installation de GTA V")))
            browseGameFolder();

        Widgets.ToolbarSeparator();
        var history = state.History;
        string saveTip = state.Project is { } project
            ? L($"Enregistrer les fichiers modifiés dans le projet « {project.Name} » (Ctrl+S)")
            : L("Enregistrer les fichiers modifiés (Ctrl+S) : un projet sera créé d'abord");
        if (state.Online.IsGuest)
            saveTip = L("Session Online : c'est l'hôte qui enregistre le projet.");
        if (Widgets.IconButton(Icons.Save, "save", saveTip, history.HasUnsavedChanges && !state.Online.IsGuest, active: false))
            save();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Package, "export", L("Exporter en ressource FiveM (fxmanifest.lua + YMAP dans « stream »)"), state.MapsToExport.Count > 0 || state.PathAreasToSave.Count > 0 || state.InteriorFilesToExport.Count > 0 || !state.OccluderEdits.IsEmpty))
            export();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Undo, "undo", history.NextUndo is { } undo ? L($"Annuler : {undo.Description} (Ctrl+Z)") : L("Annuler (Ctrl+Z)"), history.CanUndo))
            state.Undo();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Redo, "redo", history.NextRedo is { } redo ? L($"Rétablir : {redo.Description} (Ctrl+Y)") : L("Rétablir (Ctrl+Y)"), history.CanRedo))
            state.Redo();

        Widgets.ToolbarSeparator();
        // Flat combo arrows, same colour as the field.
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered]);
        DrawMapVersion();
        ImGui.SameLine();
        DrawDlcChoice();
        ImGui.PopStyleColor(2);

        Widgets.ToolbarSeparator();
        bool grid = state.Settings.ShowGrid;
        if (Widgets.IconButton(Icons.Grid, "grid", L("Grille au sol"), active: grid))
        {
            state.Settings.ShowGrid = !grid;
            state.SaveSettings();
        }

        Widgets.ToolbarSeparator();
        if (Widgets.ToolbarButton(Icons.Search, L("Rechercher"), L("Rechercher un objet, un YMAP, un type ou aller à une position FiveM (Ctrl+K)")))
            openSearch();

        Widgets.ToolbarSeparator();
        var online = state.Online;
        string onlineLabel = online.Session is { IsOver: false } session ? L($"Online · {session.Participants.Count}") : online.IsPreparing ? "Online..." : "Online";
        if (Widgets.ToolbarButton(Icons.Users, onlineLabel,
                L("NodeRunner Online : modifier la carte à plusieurs (jusqu'à 5 personnes), le projet restant sur le PC de l'hôte."),
                active: online.IsActive))
            toggleOnline();
    }

    private void DrawProject()
    {
        var project = state.Project;
        string label = project is null ? L("Aucun projet") : Widgets.Fit(project.Name, 220f);
        if (Widgets.ToolbarButton(Icons.FolderOpen, $"{label}  {Icons.ChevronDown}",
                project is null ? L("Aucun projet ouvert : nouveau projet, ouvrir, projets récents") : L($"Projet « {project.Name} »\n{project.Folder}")))
        {
            ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2f));
            ImGui.OpenPopup("##project_menu");
        }
        if (ImGui.BeginPopup("##project_menu"))
        {
            drawProjectMenu();
            ImGui.EndPopup();
        }
    }

    private void DrawMapVersion()
    {
        var current = state.Settings.MapVersion;
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("##map_version", $"{Icons.Globe}  {VersionName(current)}"))
        {
            if (ImGui.Selectable(VersionName(MapVersion.Multiplayer), current == MapVersion.Multiplayer))
                SetMapSelection(MapVersion.Multiplayer, state.Settings.LastDlcPack);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L("Carte du mode en ligne, utilisée par FiveM : ajoute les modifications de carte des DLC\n(centre-ville des Braquages, appartements...)."));
            if (ImGui.Selectable(VersionName(MapVersion.SinglePlayer), current == MapVersion.SinglePlayer))
                SetMapSelection(MapVersion.SinglePlayer, state.Settings.LastDlcPack);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L("Carte du mode histoire."));
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Version de la carte"));
    }

    public static string VersionName(MapVersion version) =>
        version == MapVersion.Multiplayer ? L("Multijoueur / FiveM") : L("Solo");

    // Map as it was after a given DLC pack: previous / next buttons and the list of the packs, in the order the game
    // applies them.
    private void DrawDlcChoice()
    {
        if (state.World.Content is { AllPacks.Count: > 0 } content)
            _knownPacks = content.AllPacks;
        if (_knownPacks.Count == 0)
            return;

        var packs = _knownPacks;
        string? selected = state.Settings.LastDlcPack;
        int index = selected is null ? -1 : packs.ToList().FindIndex(p => p.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
        int shown = index < 0 ? packs.Count - 1 : index; // "All" is the last pack.

        if (Widgets.IconButton(Icons.ChevronLeft, "dlc_previous", L("DLC précédent : la carte telle qu'elle était avant celui-ci."), shown > 0))
            SetMapSelection(state.Settings.MapVersion, packs[shown - 1].Name);
        ImGui.SameLine(0, 2);

        string label = index < 0
            ? L($"{Icons.Package}  Tous les DLC ({packs.Count})")
            : $"{Icons.Package}  {index + 1}. {packs[index].DisplayName}";
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##dlc", label, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(L($"Tous ({packs.Count})"), index < 0))
                SetMapSelection(state.Settings.MapVersion, null);
            for (int i = 0; i < packs.Count; i++)
            {
                if (ImGui.Selectable($"{i + 1}. {packs[i].DisplayName}##{packs[i].Name}", i == index))
                    SetMapSelection(state.Settings.MapVersion, i == packs.Count - 1 ? null : packs[i].Name);
                if (i == index)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Carte telle qu'elle était après ce DLC (les suivants sont ignorés),\ndans l'ordre où le jeu applique les DLC."));
        ImGui.SameLine(0, 2);

        if (Widgets.IconButton(Icons.ChevronRight, "dlc_next", L("DLC suivant."), index >= 0))
            SetMapSelection(state.Settings.MapVersion, shown + 1 >= packs.Count - 1 ? null : packs[shown + 1].Name);
    }

    private void SetMapSelection(MapVersion version, string? lastPack) => state.SetMapSelection(version, lastPack);
}
