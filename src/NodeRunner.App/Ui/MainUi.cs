using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// Everything drawn with ImGui: menu bar, toolbar, status bar, dockable panels and floating buttons around the 3D
// view, "About" window.
internal sealed class MainUi
{
    // Window titles: icon and name; the part after ### is the window's identity (kept in imgui.ini).
    public const string LayersTitle = $"{Icons.Layers}  Calques###Calques";

    private readonly AppState _state;
    private bool _showLayers = true;
    private bool _showAbout;

    private const string CodeWalkerUrl = "https://github.com/dexyfex/CodeWalker";
    private bool _showStatus = true;
    private bool _showProperties = true;
    private bool _showScene = true;
    private bool _showViewer;
    private bool _showArchives;
    private bool _showProject;
    private bool _showImGuiDemo;
    private bool _resetLayout;
    private readonly OnlineWindow _onlineWindow;

    public MainUi(AppState state, CameraController cameraController, PathNodeLayer pathNodeLayer, ScenarioLayer scenarioLayer, NavMeshLayer navMeshLayer, CollisionLayer collisionLayer, EntityBoxLayer entityBoxLayer, RoomLayer roomLayer, OcclusionLayer occlusionLayer, OcclusionCuller occlusionCuller, ModelLayer modelLayer)
    {
        _state = state;
        _viewerPanel = new ViewerPanel(state);
        _propertiesPanel = new PropertiesPanel(state, OpenInViewer, collisionLayer);
        _scenePanel = new ScenePanel(state);
        _layersPanel = new LayersPanel(state, pathNodeLayer, scenarioLayer, entityBoxLayer, roomLayer, occlusionLayer, occlusionCuller);
        _statusPanel = new StatusPanel(state, this, cameraController, navMeshLayer, collisionLayer, entityBoxLayer, modelLayer);
        _searchPalette = new SearchPalette(state, _scenePanel.RevealMap, OpenInViewer);
        _archivePanel = new ArchivePanel(state, OpenFileInViewer, _scenePanel.RevealMap);
        _projectPanel = new ProjectPanel(state);
        _exportWindow = new ExportWindow(state, () => ChooseExportFolderRequested = true);
        ProjectWindow = new ProjectWindow(state, () => ChooseProjectsFolderRequested = true);
        _onlineWindow = new OnlineWindow(state);
        _toolbar = new Toolbar(state, DrawProjectMenuItems, () => BrowseGameFolderRequested = true, _searchPalette.Open, () => SaveRequested = true, _exportWindow.Open, _onlineWindow.Toggle);
        _errorWindow = new ErrorWindow(state, modelLayer, collisionLayer, navMeshLayer);
        _statusBar = new StatusBar(state, modelLayer, _errorWindow.Open);
        _viewportOverlay = new ViewportOverlay(state, cameraController);
        _gizmo = new TransformGizmo(state);
        _roomHover = new RoomHover(state, roomLayer);
        _roomGizmo = new RoomGizmo(state);
        _deleteWindow = new DeleteWindow(state);
        _settingsWindow = new SettingsWindow(state, () => BrowseGameFolderRequested = true, () => ChooseProjectsFolderRequested = true);
    }

    private void OpenInViewer(uint archetypeHash)
    {
        _viewerPanel.OpenArchetype(archetypeHash);
        _showViewer = true;
        ImGui.SetWindowFocus(ViewerPanel.Title);
    }

    private void OpenFileInViewer(string path)
    {
        _viewerPanel.OpenFile(path);
        _showViewer = true;
        ImGui.SetWindowFocus(ViewerPanel.Title);
    }

    private readonly ArchivePanel _archivePanel;
    private readonly ProjectPanel _projectPanel;
    private readonly PropertiesPanel _propertiesPanel;
    private readonly ScenePanel _scenePanel;
    private readonly SearchPalette _searchPalette;
    private readonly LayersPanel _layersPanel;
    private readonly StatusPanel _statusPanel;
    private readonly Toolbar _toolbar;
    private readonly StatusBar _statusBar;
    private readonly ViewportOverlay _viewportOverlay;
    private readonly ExportWindow _exportWindow;
    private readonly ViewerPanel _viewerPanel;
    private readonly TransformGizmo _gizmo;
    private readonly RoomHover _roomHover;
    private readonly RoomGizmo _roomGizmo;
    private readonly DeleteWindow _deleteWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly ErrorWindow _errorWindow;

    public bool BrowseGameFolderRequested { get; set; }

    public bool ExitRequested { get; set; }

    // Set by the UI to save the modified map files (asks for a folder first if none is set); handled by the main
    // form.
    public bool SaveRequested { get; set; }

    public ProjectWindow ProjectWindow { get; }

    public bool OpenProjectRequested { get; set; }

    public bool ImportMappingRequested { get; set; }

    public void ShowProjectPanel()
    {
        _showProject = true;
        ImGui.SetWindowFocus(ProjectPanel.Title);
    }

    public string? RecentProjectRequested { get; set; }

    public bool CloseProjectRequested { get; set; }

    public bool ChooseProjectsFolderRequested { get; set; }

    public bool ChooseExportFolderRequested { get; set; }

    // File or folder of the archive explorer to extract: the main form asks where, then calls ExtractArchiveItem.
    public ArchiveTree.Node? ArchiveExtractRequested
    {
        get => _archivePanel.ExtractRequested;
        set => _archivePanel.ExtractRequested = value;
    }

    public void ExtractArchiveItem(ArchiveTree.Node node, string target) => _archivePanel.Extract(node, target);

    public bool IsSearchOpen => _searchPalette.IsOpen;

    public bool IsGizmoHovered => _gizmo.IsHovered || _roomGizmo.IsHovered;

    public bool IsGizmoDragging => _gizmo.IsDragging || _roomGizmo.IsDragging;

    public void Draw()
    {
        _state.Online.Update();
        HandleShortcuts();
        DrawMenuBar();
        // The bars first: the panels and the 3D view share what is left of the window.
        _toolbar.Draw();
        _statusBar.Draw();
        DockLayout.Draw(_resetLayout, ScenePanel.Title, LayersTitle, PropertiesPanel.Title, StatusPanel.Title);
        _resetLayout = false;
        _gizmo.Update(blocked: _searchPalette.IsOpen);
        _roomHover.Update(blocked: _searchPalette.IsOpen || IsGizmoHovered || IsGizmoDragging);
        _roomGizmo.Update(blocked: _searchPalette.IsOpen);
        _viewportOverlay.Draw();
        if (_showLayers) DrawLayersWindow();
        if (_showScene) _scenePanel.Draw(ref _showScene);
        if (_showProperties) _propertiesPanel.Draw(ref _showProperties);
        if (_showStatus) _statusPanel.Draw(ref _showStatus);
        if (_showArchives) _archivePanel.Draw(ref _showArchives);
        if (_showProject) _projectPanel.Draw(ref _showProject);
        if (_showViewer) _viewerPanel.Draw(ref _showViewer);
        else _state.Viewer.RequestedSize = Vector2.Zero;
        if (_showAbout) DrawAboutWindow();
        _exportWindow.Draw();
        ProjectWindow.Draw();
        _deleteWindow.Draw();
        _settingsWindow.Draw();
        _onlineWindow.Draw();
        _errorWindow.Draw();
        if (_showImGuiDemo) ImGui.ShowDemoWindow(ref _showImGuiDemo);
        _searchPalette.Draw(); // Last: above the panels.
    }

    private void HandleShortcuts()
    {
        if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.K) && !_searchPalette.IsOpen)
            _searchPalette.Open();
        if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.S))
            SaveRequested = true;
        if (ImGui.GetIO().WantTextInput || _searchPalette.IsOpen || IsGizmoDragging || _state.PendingDeletion is not null)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            if (_state.Tool == TransformTool.Collision)
                _state.DeleteSelectedCollision();
            else if (_state.SelectedOccluder is not null)
                _state.DeleteSelectedOccluder();
            else if (_state.SelectedRoom is not null)
                _state.DeleteSelectedRoom();
            else if (_state.SelectedPortal is not null)
                _state.DeleteSelectedPortal();
            else
                _state.RequestDelete();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.End, false))
            _state.PutSelectionOnGround();
        if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.C))
            _state.CopySelection();
        else if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.V) && _state.Copied.Count > 0)
            _state.PasteRequest = true;
        if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.Z))
            _state.Undo();
        else if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.Y) || ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.ModShift | ImGuiKey.Z))
            _state.Redo();
        else if (ImGui.IsKeyChordPressed(ImGuiKey.G))
        {
            _state.Settings.ShowGrid = !_state.Settings.ShowGrid;
            _state.SaveSettings();
        }
        else if (!ImGui.GetIO().KeyCtrl)
        {
            // Shift allowed: on an AZERTY keyboard the digits of the top row need it.
            if (ToolKeyPressed(ImGuiKey._1, ImGuiKey.Keypad1)) _state.Tool = TransformTool.Select;
            else if (ToolKeyPressed(ImGuiKey._2, ImGuiKey.Keypad2)) _state.Tool = TransformTool.Move;
            else if (ToolKeyPressed(ImGuiKey._3, ImGuiKey.Keypad3)) _state.Tool = TransformTool.Rotate;
            else if (ToolKeyPressed(ImGuiKey._4, ImGuiKey.Keypad4)) _state.Tool = TransformTool.Scale;
            else if (ToolKeyPressed(ImGuiKey._5, ImGuiKey.Keypad5)) _state.Tool = TransformTool.Collision;
        }
    }

    private static bool ToolKeyPressed(ImGuiKey digit, ImGuiKey keypad) =>
        ImGui.IsKeyPressed(digit, false) || ImGui.IsKeyPressed(keypad, false);

    private const float MenuBarPaddingY = 9f;

    private static bool Menu(string label)
    {
        // Centred in the (taller) bar: ImGui would put the menus lower, with the bar's padding above.
        ImGui.SetCursorPosY((ImGui.GetWindowHeight() - ImGui.GetFrameHeight()) * 0.5f - 5f);
        bool open = ImGui.BeginMenu(label);
        WindowChrome.TrackItem();
        return open;
    }

    private void DrawMenuBar()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, MenuBarPaddingY));
        bool visible = ImGui.BeginMainMenuBar();
        ImGui.PopStyleVar();
        if (!visible)
            return;
        WindowChrome.Begin(ImGui.GetWindowPos(), ImGui.GetWindowSize());

        // Brand monogram: NR, then S in the accent colour (Unbounded bold, as the logo).
        ImGui.PushFont(UiTheme.BrandFont);
        float brandY = (ImGui.GetWindowHeight() - ImGui.GetTextLineHeight()) * 0.5f - 7f;
        ImGui.SetCursorPos(new Vector2(ImGui.GetCursorPosX() + 6f, brandY));
        ImGui.TextUnformatted("NR");
        ImGui.SameLine(0, 0);
        ImGui.TextColored(UiTheme.Accent, "S");
        ImGui.PopFont();
        ImGui.SameLine(0, 18f);

        if (Menu(L("Fichier")))
        {
            DrawProjectMenuItems();
            ImGui.Separator();
            int toSave = _state.PendingSaveCount;
            if (ImGui.MenuItem(L($"{Icons.Save}  Enregistrer les fichiers modifiés{(toSave > 0 ? $" ({toSave})" : "")}"), "Ctrl+S", false, toSave > 0))
                SaveRequested = true;
            if (ImGui.MenuItem(L($"{Icons.Package}  Exporter en ressource FiveM...")))
                _exportWindow.Open();
            if (ImGui.MenuItem(L($"{Icons.Users}  Online..."), null, _onlineWindow.IsOpen))
                _onlineWindow.Toggle();
            ImGui.Separator();
            if (ImGui.MenuItem(L("Choisir le dossier de GTA V...")))
                BrowseGameFolderRequested = true;
            if (ImGui.MenuItem(L("Relancer la détection automatique")))
                _state.RedetectGame(clearManualChoice: true);
            ImGui.Separator();
            if (ImGui.MenuItem(L("Quitter")))
                ExitRequested = true;
            ImGui.EndMenu();
        }

        if (Menu(L("Édition")))
        {
            var history = _state.History;
            if (ImGui.MenuItem(history.NextUndo is { } undo ? L($"Annuler : {undo.Description}") : L("Annuler"), "Ctrl+Z", false, history.CanUndo))
                _state.Undo();
            if (ImGui.MenuItem(history.NextRedo is { } redo ? L($"Rétablir : {redo.Description}") : L("Rétablir"), "Ctrl+Y", false, history.CanRedo))
                _state.Redo();
            ImGui.Separator();
            int selected = _state.SelectedEntities.Count;
            if (ImGui.MenuItem(selected > 1 ? L($"{Icons.Copy}  Copier les {selected} objets sélectionnés") : L($"{Icons.Copy}  Copier l'objet sélectionné"),
                "Ctrl+C", false, selected > 0))
                _state.CopySelection();
            int copied = _state.Copied.Count;
            if (ImGui.MenuItem(copied > 1 ? L($"{Icons.Copy}  Coller les {copied} objets") : L($"{Icons.Copy}  Coller"), "Ctrl+V", false, copied > 0))
                _state.PasteRequest = true;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute une copie au sol, au centre de la vue 3D, dans le YMAP du projet (le groupe garde sa disposition)."));
            ImGui.Separator();
            int group = _state.DeletableSelection.Count;
            if (ImGui.MenuItem(group > 1 ? L($"{Icons.Trash}  Supprimer les {group} objets sélectionnés...") : L($"{Icons.Trash}  Supprimer l'objet sélectionné..."),
                L("Suppr"), false, group > 0))
                _state.RequestDelete();
            if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Poser au sol"), L("Fin"), false, _state.EditableSelection.Count > 0))
                _state.PutSelectionOnGround();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Monte ou descend chaque objet sélectionné jusqu'à ce que le bas de sa boîte touche la collision en dessous."));
            bool followGround = _state.Settings.FollowGround;
            if (ImGui.MenuItem(L($"{Icons.Footprints}  Suivre le sol en déplaçant"), null, ref followGround))
            {
                _state.Settings.FollowGround = followGround;
                _state.SaveSettings();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Pendant un déplacement avec les flèches X / Y ou le carré au sol, l'objet reste posé sur la collision."));
            ImGui.Separator();
            if (ImGui.BeginMenu(L($"{Icons.ScanEye}  Occlusion")))
            {
                DrawOcclusionMenuItems();
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.Search}  Rechercher..."), "Ctrl+K"))
                _searchPalette.Open();
            ImGui.Separator();
            ImGui.TextDisabled(history.Count == 0 ? L("Aucune modification") : L($"{history.Count} modification(s), pas encore enregistrée(s)"));
            ImGui.EndMenu();
        }

        if (Menu(L("Affichage")))
        {
            ImGui.MenuItem(L(ScenePanel.Title), null, ref _showScene);
            ImGui.MenuItem(L(LayersTitle), null, ref _showLayers);
            ImGui.MenuItem(L(PropertiesPanel.Title), null, ref _showProperties);
            ImGui.MenuItem(L(StatusPanel.Title), null, ref _showStatus);
            ImGui.MenuItem(L(ViewerPanel.Title), null, ref _showViewer);
            ImGui.MenuItem(L(ArchivePanel.Title), null, ref _showArchives);
            ImGui.MenuItem(L(ProjectPanel.Title), null, ref _showProject);
            if (ImGui.MenuItem(L("Réinitialiser la disposition des panneaux")))
            {
                _showLayers = _showScene = _showProperties = _showStatus = true;
                _resetLayout = true;
            }
            ImGui.Separator();
            bool showGrid = _state.Settings.ShowGrid;
            if (ImGui.MenuItem(L("Grille au sol"), "G", ref showGrid))
            {
                _state.Settings.ShowGrid = showGrid;
                _state.SaveSettings();
            }
            if (ImGui.MenuItem(L($"{Icons.Settings}  Paramètres d'affichage...")))
                _settingsWindow.Open();
            ImGui.Separator();
            ImGui.MenuItem(L("Démo ImGui (développeurs)"), null, ref _showImGuiDemo);
            ImGui.EndMenu();
        }

        if (Menu(L("Aide")))
        {
            if (ImGui.MenuItem(L($"{Icons.TriangleAlert}  Erreurs et rapport...")))
                _errorWindow.Open();
            ImGui.Separator();
            if (ImGui.MenuItem(L($"À propos de {Program.Name}")))
                _showAbout = true;
            ImGui.EndMenu();
        }

        // Author credit, right-aligned before the window buttons.
        string credit = $"{Program.Name} par {Program.Author}";
        float creditX = ImGui.GetWindowWidth() - WindowChrome.ButtonWidth * 3 - ImGui.CalcTextSize(credit).X - 16f;
        if (creditX > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(creditX);
            ImGui.TextDisabled(credit);
        }
        WindowChrome.DrawButtons();

        ImGui.EndMainMenuBar();
    }

    private void DrawOcclusionMenuItems()
    {
        if (ImGui.MenuItem(L($"{Icons.Box}  Nouvelle boîte d'occlusion")))
            _state.CreateOccluderBox();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Boîte de 10 m posée au sol devant la caméra, dans le fichier d'occlusion du projet. À ajuster avec les poignées."));
        int selected = _state.SelectedEntities.Count;
        if (ImGui.MenuItem(selected > 1 ? L($"{Icons.Boxes}  Boîtes d'occlusion sur les {selected} objets sélectionnés") : L($"{Icons.Boxes}  Boîte d'occlusion sur l'objet sélectionné"),
                null, false, selected > 0))
            _state.CreateOccluderBoxesFromSelection();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Une boîte par bâtiment fermé, à l'intérieur des murs de la collision (sinon dans la boîte du modèle). ")
                + L("Pour un intérieur (MLO) : des boîtes fines dans ses murs extérieurs, portes et fenêtres laissées libres."));
        var occluder = _state.SelectedOccluder;
        if (ImGui.MenuItem(L($"{Icons.Trash}  Supprimer l'occluder sélectionné"), L("Suppr"), false, occluder is not null))
            _state.DeleteSelectedOccluder();
    }

    private void DrawProjectMenuItems()
    {
        if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Nouveau projet...")))
            ProjectWindow.Open();
        if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Ouvrir un projet...")))
            OpenProjectRequested = true;
        if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Importer un mapping...")))
            ImportMappingRequested = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Ouvre le mapping de quelqu'un d'autre (ressource FiveM ou dossier stream) dans un nouveau projet.\nLes fichiers sont copiés : le dossier d'origine n'est pas modifié."));
        var recent = _state.Settings.RecentProjects.Where(f => !string.Equals(f, _state.Project?.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ImGui.BeginMenu(L("Projets récents"), recent.Count > 0))
        {
            foreach (var folder in recent)
            {
                bool exists = NodeRunner.Editor.MapProject.Exists(folder);
                if (ImGui.MenuItem($"{Path.GetFileName(folder)}##{folder}", null, false, exists))
                    RecentProjectRequested = folder;
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(exists ? folder : L($"{folder}\nIntrouvable (déplacé ou supprimé)"));
            }
            ImGui.Separator();
            if (ImGui.MenuItem(L("Effacer la liste")))
            {
                _state.Settings.RecentProjects.RemoveAll(f => !string.Equals(f, _state.Project?.Folder, StringComparison.OrdinalIgnoreCase));
                _state.SaveSettings();
            }
            ImGui.EndMenu();
        }
        if (_state.Project is { } project)
        {
            if (ImGui.MenuItem(L($"{Icons.ListTree}  Contenu du projet (objets ajoutés, déplacés, supprimés)")))
                ShowProjectPanel();
            if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Ouvrir le dossier du projet")) && Directory.Exists(project.Folder))
                System.Diagnostics.Process.Start("explorer.exe", project.Folder);
            if (ImGui.MenuItem(L($"{Icons.X}  Fermer le projet")))
                CloseProjectRequested = true;
        }
    }

    private void DrawLayersWindow()
    {
        if (ImGui.Begin(L(LayersTitle), ref _showLayers))
            _layersPanel.DrawContent();
        ImGui.End();
    }

    private void DrawAboutWindow()
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(440f, 0f), new Vector2(440f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L($"À propos de {Program.Name}###About"), ref _showAbout, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (!visible)
        {
            ImGui.End();
            return;
        }

        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(Program.Name);
        ImGui.PopFont();
        Widgets.Hint(L($"Version {Program.Version} · par {Program.Author}"));
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.TextWrapped(L("Outil gratuit pour explorer et modifier la carte de GTA V en 3D (solo et FiveM)."));

        if (Widgets.Section(L("Crédits"), "about_credits") && Widgets.BeginProperties("##credits", 0.32f))
        {
            Widgets.Property(L("Remerciements"));
            ImGui.TextWrapped(L("CodeWalker par dexyfex, référence de la communauté pour les fichiers du jeu"));
            if (Widgets.Link(CodeWalkerUrl, L("Clic : copier l'adresse")))
                ImGui.SetClipboardText(CodeWalkerUrl);
            Widgets.Property(L("Interface"));
            ImGui.TextWrapped("Dear ImGui (Omar Cornut) via ImGui.NET");
            Widgets.Property("DirectX 11");
            ImGui.TextWrapped("Vortice.Windows (Amer Koleci)");
            Widgets.Property(L("Icônes"));
            ImGui.TextWrapped(L("Lucide (lucide.dev, licence ISC ; icônes issues de Feather : MIT)"));
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Licence"), "about_license") && Widgets.BeginProperties("##license", 0.32f))
        {
            Widgets.Property(L("Licence"));
            ImGui.TextWrapped(L("PolyForm Strict 1.0.0 (fichier LICENSE.md à côté du logiciel)"));
            Widgets.Property(L("Autorisé"));
            ImGui.TextWrapped(L("Utilisation gratuite pour tout usage non commercial"));
            Widgets.Property(L("Interdit"));
            ImGui.TextWrapped(L("Revente, redistribution et versions modifiées sans l'accord de l'auteur"));
            Widgets.Property(L("Nom et logo"));
            ImGui.TextWrapped(L($"« {Program.Name} » et le logo NRS appartiennent à {Program.Author}"));
            Widgets.Property("Copyright");
            ImGui.TextWrapped($"© 2026 {Program.Author}");
            Widgets.EndProperties();
        }

        ImGui.Dummy(new Vector2(0, 4));
        Widgets.Hint(L($"NodeRunner et {Program.Author} ne sont pas affiliés à Rockstar Games ni à Take-Two Interactive. ")
            + L("Aucun fichier du jeu n'est fourni : le logiciel lit ta propre installation."), wrap: true);
        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Button(L("Fermer"), width: 90f))
            _showAbout = false;
        ImGui.End();
    }
}
