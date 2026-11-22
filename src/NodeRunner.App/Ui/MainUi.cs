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
    private bool _showLibrary = true;
    private bool _showArchives;
    private bool _showProject;
    private bool _showHistory;
    private bool _showImGuiDemo;
    private bool _resetLayout;
    private readonly OnlineWindow _onlineWindow;

    public MainUi(AppState state, CameraController cameraController, PathNodeLayer pathNodeLayer, ScenarioLayer scenarioLayer, NavMeshLayer navMeshLayer, CollisionLayer collisionLayer, EntityBoxLayer entityBoxLayer, RoomLayer roomLayer, OcclusionLayer occlusionLayer, OcclusionCuller occlusionCuller, ModelLayer modelLayer, CarGeneratorLayer carGeneratorLayer, TimecycleLayer timecycleLayer, LodLightLayer lodLightLayer)
    {
        _state = state;
        _viewerPanel = new ViewerPanel(state);
        _pedStudio = new PedStudio(state);
        _cutscenePanel = new CutscenePanel(state);
        _workspaceTabs = new WorkspaceTabs(state, _viewerPanel, _pedStudio, DrawOpenMenu);
        _libraryPanel = new LibraryPanel(state, OpenInViewer);
        _propertiesPanel = new PropertiesPanel(state, OpenInViewer, collisionLayer);
        _scenePanel = new ScenePanel(state);
        _propertiesPanel.WorkshopContent = _viewerPanel.DrawWorkshopProperties;
        _scenePanel.WorkshopContent = _viewerPanel.DrawWorkshopParts;
        _propertiesPanel.PedContent = _pedStudio.DrawProperties;
        _scenePanel.PedContent = _pedStudio.DrawList;
        _scenePanel.CutsceneContent = _cutscenePanel.DrawList;
        _viewerPanel.BuildingOpeningContent = _propertiesPanel.DrawBuildingOpeningFor;
        _layersPanel = new LayersPanel(state, pathNodeLayer, scenarioLayer, entityBoxLayer, roomLayer, occlusionLayer, occlusionCuller, carGeneratorLayer, timecycleLayer, lodLightLayer);
        _statusPanel = new StatusPanel(state, this, cameraController, navMeshLayer, collisionLayer, entityBoxLayer, modelLayer);
        _searchPalette = new SearchPalette(state, _scenePanel.RevealMap, OpenInViewer) { Commands = PaletteCommands };
        _propertiesPanel.Search = _searchPalette.Open;
        _propertiesPanel.ShowLibrary = ShowLibrary;
        _archivePanel = new ArchivePanel(state, OpenFileInViewer, _scenePanel.RevealMap, OpenTypeInViewer);
        _historyPanel = new HistoryPanel(state);
        _projectPanel = new ProjectPanel(state)
        {
            NewProject = () => ProjectWindow!.Open(),
            OpenProject = () => OpenProjectRequested = true,
            ImportMapping = () => ImportMappingRequested = true,
        };
        _exportWindow = new ExportWindow(state, () => ChooseExportFolderRequested = true, () => SaveSoloPackRequested = true);
        ProjectWindow = new ProjectWindow(state, () => ChooseProjectsFolderRequested = true);
        _onlineWindow = new OnlineWindow(state);
        _shortcutsWindow = new ShortcutsWindow(state);
        _welcomeWindow = new WelcomeWindow(state, this, _shortcutsWindow.Toggle);
        _toolbar = new Toolbar(state, DrawToolbarProjectMenu,() => BrowseGameFolderRequested = true, _searchPalette.Open, () => SaveRequested = true, _exportWindow.Open, _onlineWindow.Toggle)
        {
            ToggleLibrary = ToggleLibrary,
            IsLibraryShown = () => _showLibrary,
        };
        _errorWindow = new ErrorWindow(state, modelLayer, collisionLayer, navMeshLayer);
        _statusBar = new StatusBar(state, modelLayer, _errorWindow.Open)
        {
            IsDragging = () => IsGizmoDragging,
            IsLooking = () => cameraController.IsLooking,
            WorkshopHints = _viewerPanel.WorkshopHints,
            PedHints = _pedStudio.Hints,
        };
        _viewportOverlay = new ViewportOverlay(state, cameraController);
        _noticeToast = new NoticeToast(state);
        _updateNotice = new UpdateNotice(state);
        _gizmo = new TransformGizmo(state);
        _roomHover = new RoomHover(state, roomLayer);
        _presence = new PresenceOverlay(state);
        _roomGizmo = new RoomGizmo(state);
        _collisionGizmo = new CollisionGizmo(state, collisionLayer);
        _deleteWindow = new DeleteWindow(state);
        _interiorWindow = new InteriorWindow(state);
        _imagePropWindow = new ImagePropWindow(state);
        _settingsWindow = new SettingsWindow(state, () => BrowseGameFolderRequested = true, () => ChooseProjectsFolderRequested = true);
        _shortcutsWindow.OpenSettings = _settingsWindow.OpenKeys;
    }

    // What the search palette (Ctrl+K) can run besides finding things: the usual actions, by name.
    private IReadOnlyList<PaletteCommand> PaletteCommands()
    {
        var features = _state.Features;
        var settings = _state.Settings;
        void Hour(float hour)
        {
            settings.TimeOfDay = hour;
            _state.SaveSettings();
        }
        return
        [
            new(Icons.Save, L("Enregistrer le projet"), "sauvegarder save", KeyText(KeyAction.Save), () => SaveRequested = true, features.Editing),
            new(Icons.Package, L("Exporter en ressource FiveM"), "export fxmanifest serveur", KeyText(KeyAction.Export), _exportWindow.Open, features.Gta5Exports),
            new(Icons.FolderPlus, L("Nouveau projet"), "créer new", KeyText(KeyAction.NewProject), () => ProjectWindow.Open(), features.Editing),
            new(Icons.FolderOpen, L("Ouvrir un projet"), "open", KeyText(KeyAction.OpenProject), () => OpenProjectRequested = true, features.Editing),
            new(Icons.Undo, L("Annuler la dernière modification"), "undo retour", KeyText(KeyAction.Undo), _state.Undo, features.Editing),
            new(Icons.Redo, L("Rétablir la modification annulée"), "redo refaire", KeyText(KeyAction.Redo), _state.Redo, features.Editing),
            new(Icons.MousePointer, L("Outil Sélection"), "select choisir", "1", () => _state.Tool = TransformTool.Select, features.Editing),
            new(Icons.Move, L("Outil Déplacer"), "move bouger", "2", () => _state.Tool = TransformTool.Move, features.Editing),
            new(Icons.Rotate, L("Outil Tourner"), "rotate rotation pivoter", "3", () => _state.Tool = TransformTool.Rotate, features.Editing),
            new(Icons.Scale, L("Outil Échelle"), "scale taille agrandir", "4", () => _state.Tool = TransformTool.Scale, features.Editing),
            new(Icons.Mountain, L("Outil Collisions"), "collision surface", "5", () => _state.Tool = TransformTool.Collision, features.Editing),
            new(Icons.ArrowDownToLine, L("Poser la sélection au sol"), "ground sol", KeyText(KeyAction.PutOnGround), _state.PutSelectionOnGround, features.Editing && _state.Selection is not null),
            new(Icons.Box, L("Modifier le modèle sélectionné dans l'Atelier"), "atelier workshop morceau texture percer", L("Double-clic"),
                () => _state.WorkshopRequest = _state.Selection, features.Editing && _state.Selection is not null),
            new(Icons.Grid, L("Afficher ou masquer la grille au sol"), "grid quadrillage", KeyText(KeyAction.Grid), () =>
            {
                settings.ShowGrid = !settings.ShowGrid;
                _state.SaveSettings();
            }),
            new(Icons.Sunrise, L("Heure : aube"), "matin jour time", "", () => Hour(6.5f)),
            new(Icons.Sun, L("Heure : midi"), "jour time", "", () => Hour(13f)),
            new(Icons.Sunset, L("Heure : soir"), "coucher time", "", () => Hour(19f)),
            new(Icons.Moon, L("Heure : nuit"), "night time", "", () => Hour(23f)),
            new(Icons.Box, L("Ouvrir la Visionneuse"), "viewer modèles véhicules catalogue", KeyText(KeyAction.Viewer), ToggleViewer),
            new(Icons.Film, L("Ouvrir la visionneuse de cinématiques"), "cinématique cutscene cut film scène mission", KeyText(KeyAction.Cutscenes), () =>
            {
                if (!_state.CutsceneOpen) ToggleCutscenes();
            }, features.CutsceneViewer),
            new(Icons.PersonStanding, L("Ouvrir la visionneuse de personnages"), "ped pnj piéton personnage animal animation", KeyText(KeyAction.Peds), _pedStudio.Open, features.PedViewer),
            new(Icons.HardDrive, L("Ouvrir l'explorateur d'archives"), "rpf fichiers archives", KeyText(KeyAction.Archives), () => _showArchives = true),
            new(Icons.LibraryBig, L("Ouvrir la Bibliothèque"), "props packs addons library", "", ShowLibrary, features.Editing),
            new(Icons.Users, L("Ouvrir NodeRunner Online"), "multijoueur session inviter", "", _onlineWindow.Toggle, features.Online),
            new(Icons.Settings, L("Ouvrir les paramètres"), "réglages options settings qualité langue", KeyText(KeyAction.Settings), _settingsWindow.Open),
            new(Icons.Keyboard, L("Ouvrir les raccourcis clavier"), "touches aide help shortcuts", KeyText(KeyAction.Shortcuts), _shortcutsWindow.Toggle),
            new(Icons.ListTree, L("Afficher le panneau Scène"), "objets liste panel", "", () => _showScene = true),
            new(Icons.Layers, L("Afficher le panneau Calques"), "layers panel", "", () => _showLayers = true),
            new(Icons.SlidersHorizontal, L("Afficher le panneau Propriétés"), "properties panel", "", () => _showProperties = true),
            new(Icons.Activity, L("Afficher le panneau État"), "status panel", "", () => _showStatus = true),
            new(Icons.FolderOpen, L("Afficher le panneau Projet"), "project fichiers panel", "", ShowProjectPanel, features.Editing),
            new(Icons.Undo, L("Afficher le panneau Historique"), "history annuler undo panel", "", () => _showHistory = true, features.Editing),
            new(Icons.Monitor, L("Remettre la disposition des panneaux par défaut"), "layout reset fenêtres", "", () => _resetLayout = true),
            new(Icons.Info, L("À propos de NodeRunner Studio"), "about version crédits", "", () => _showAbout = true),
        ];
    }

    private void OpenInViewer(uint archetypeHash)
    {
        _viewerPanel.OpenArchetype(archetypeHash);
        _showViewer = true;
        ImGui.SetWindowFocus(ViewerPanel.Title);
    }

    // Library panel shown (after a pack is added), at the next frame.
    public void ShowLibrary()
    {
        _showLibrary = true;
        _focusLibrary = true;
    }

    public void ToggleLibrary()
    {
        _showLibrary = !_showLibrary;
        _focusLibrary = _showLibrary;
    }

    public bool IsLibraryShown => _showLibrary;

    private bool _libraryHiddenForPeds;

    // The prop library has nothing for the ped viewer: closed while its tab is shown, back with the map (unless it was
    // closed already).
    private void HideLibraryForPeds()
    {
        if (_state.PedShown && _showLibrary && !_libraryHiddenForPeds)
        {
            _showLibrary = false;
            _libraryHiddenForPeds = true;
        }
        else if (!_state.PedShown && _libraryHiddenForPeds)
        {
            _showLibrary = true;
            _libraryHiddenForPeds = false;
        }
    }

    private void ToggleViewer()
    {
        _showViewer = !_showViewer;
        if (_showViewer)
            ImGui.SetWindowFocus(ViewerPanel.Title);
    }

    private void ToggleCutscenes()
    {
        if (_state.CutsceneOpen)
            _cutscenePanel.Close();
        else
        {
            _cutscenePanel.Open();
            _showScene = true;
        }
    }

    // The "+" of the tabs above the 3D view: every viewer, wherever it opens (panel, tab, or on the map).
    private void DrawOpenMenu()
    {
        var features = _state.Features;
        if (ImGui.MenuItem(L(ViewerPanel.Title), KeyText(KeyAction.Viewer), _showViewer))
            ToggleViewer();
        if (ImGui.MenuItem(L($"{Icons.PersonStanding}  Visionneuse de personnages"), KeyText(KeyAction.Peds), _state.PedOpen, features.PedViewer))
            _pedStudio.Open();
        if (ImGui.MenuItem(L($"{Icons.Film}  Visionneuse de cinématiques"), KeyText(KeyAction.Cutscenes), _state.CutsceneOpen, features.CutsceneViewer))
            ToggleCutscenes();
        ImGui.Separator();
        if (ImGui.MenuItem(L($"{Icons.Box}  Modifier le modèle (Atelier)"), L("Double-clic"), false, features.Editing && _state.Selection is not null))
            _state.WorkshopRequest = _state.Selection;
        if (_state.Selection is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Sélectionnez d'abord un objet sur la carte."));
    }

    private bool _focusLibrary;

    private void OpenTypeInViewer(NodeRunner.Data.Maps.Archetype archetype)
    {
        if (!_viewerPanel.OpenArchetype(archetype))
        {
            _state.Notify(L("Modèle introuvable pour ce type (aucun fichier .ydr / .ydd / .yft de ce nom)"), warning: true);
            return;
        }
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
    private readonly HistoryPanel _historyPanel;
    private readonly PropertiesPanel _propertiesPanel;
    private readonly ScenePanel _scenePanel;
    private readonly SearchPalette _searchPalette;
    private readonly LayersPanel _layersPanel;
    private readonly StatusPanel _statusPanel;
    private readonly Toolbar _toolbar;
    private readonly StatusBar _statusBar;
    private readonly ViewportOverlay _viewportOverlay;
    private readonly NoticeToast _noticeToast;
    private readonly UpdateNotice _updateNotice;
    private readonly ExportWindow _exportWindow;
    private readonly ViewerPanel _viewerPanel;
    private readonly WorkspaceTabs _workspaceTabs;
    private readonly PedStudio _pedStudio;
    private readonly CutscenePanel _cutscenePanel;
    private readonly LibraryPanel _libraryPanel;
    private readonly TransformGizmo _gizmo;
    private readonly RoomHover _roomHover;
    private readonly PresenceOverlay _presence;
    private readonly RoomGizmo _roomGizmo;
    private readonly CollisionGizmo _collisionGizmo;
    private readonly DeleteWindow _deleteWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly InteriorWindow _interiorWindow;
    private readonly ImagePropWindow _imagePropWindow;
    private readonly ErrorWindow _errorWindow;
    private readonly ShortcutsWindow _shortcutsWindow;
    private readonly WelcomeWindow _welcomeWindow;
    private readonly ProtectedMappingWindow _protectedWindow = new();

    public bool BrowseGameFolderRequested { get; set; }

    public bool ExitRequested { get; set; }

    // Set by the UI to save the modified map files (asks for a folder first if none is set); handled by the main
    // form.
    public bool SaveRequested { get; set; }

    public ProjectWindow ProjectWindow { get; }

    public bool OpenProjectRequested { get; set; }

    public bool ImportMappingRequested { get; set; }

    public bool ImportPackageRequested { get; set; }

    public bool AddFilesRequested { get; set; }

    public bool AddFolderRequested { get; set; }

    public bool LinkFolderRequested { get; set; }

    public bool CheckResourceRequested { get; set; }

    public bool UnlinkFolderRequested { get; set; }

    public void ShowProjectPanel()
    {
        _showProject = true;
        ImGui.SetWindowFocus(ProjectPanel.Title);
    }

    // After an import: explains the models locked by FiveM's Asset Escrow, if any.
    public void ShowProtectedMapping(NodeRunner.Editor.ImportResult result) => _protectedWindow.Open(result);

    public string? RecentProjectRequested { get; set; }

    public bool CloseProjectRequested { get; set; }

    // Another project to show with the open one: a folder, or "" to browse for it.
    public string? AddViewProjectRequested { get; set; }

    public string? RemoveViewProjectRequested { get; set; }

    public string? ActivateProjectRequested { get; set; }

    public bool ChooseProjectsFolderRequested { get; set; }

    public bool ChooseExportFolderRequested { get; set; }

    // Single-player pack to save: the main form asks where, then calls ExportSoloPack.
    public bool SaveSoloPackRequested { get; set; }

    public void ExportSoloPack(string dlcRpf) => _exportWindow.ExportSolo(dlcRpf);

    // File or folder of the archive explorer to extract: the main form asks where, then calls ExtractArchiveItem.
    public ArchiveTree.Node? ArchiveExtractRequested
    {
        get => _archivePanel.ExtractRequested;
        set => _archivePanel.ExtractRequested = value;
    }

    public void ExtractArchiveItem(ArchiveTree.Node node, string target) => _archivePanel.Extract(node, target);

    public bool IsSearchOpen => _searchPalette.IsOpen;

    private bool _openViewportMenu;

    public void OpenViewportMenu() => _openViewportMenu = true;

    // Right click in the 3D view: the usual actions on the selection, or paste / place when nothing is selected.
    private void DrawViewportMenu()
    {
        if (_openViewportMenu)
        {
            ImGui.OpenPopup("##viewport_menu");
            _openViewportMenu = false;
        }
        if (!ImGui.BeginPopup("##viewport_menu"))
            return;
        var entity = _state.Selection;
        int selected = _state.SelectedEntities.Count;
        if (entity is not null)
        {
            string name = _state.World.Names?.Describe(entity.ArchetypeHash) ?? L("Objet");
            ImGui.TextDisabled(selected > 1 ? L($"{name} (+{selected - 1})") : name);
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.Crosshair}  Aller à l'objet")))
                _state.FocusOn(entity);
            string position = _state.FormatPosition(entity.Position);
            if (ImGui.MenuItem(L($"{Icons.Copy}  Copier la position"), position))
                ImGui.SetClipboardText(position);
            if (ImGui.MenuItem(L($"{Icons.Copy}  Copier la position avec le cap")))
                ImGui.SetClipboardText(_state.FormatPosition(entity.Position, FiveMFormat.Heading(entity)));
            if (ImGui.MenuItem(L($"{Icons.Box}  Voir le modèle dans la visionneuse")))
                OpenInViewer(entity.ArchetypeHash);
            if (ImGui.MenuItem(L($"{Icons.Box}  Modifier le modèle (Atelier)"), L("Double-clic")))
                _state.WorkshopRequest = entity;
            ImGui.Separator();
            if (ImGui.MenuItem(selected > 1 ? L($"{Icons.Copy}  Copier les {selected} objets sélectionnés") : L($"{Icons.Copy}  Copier l'objet sélectionné"), KeyText(KeyAction.Copy)))
                _state.CopySelection();
        }
        int copied = _state.Copied.Count;
        if (ImGui.MenuItem(copied > 1 ? L($"{Icons.Copy}  Coller les {copied} objets") : L($"{Icons.Copy}  Coller"), KeyText(KeyAction.Paste), false, copied > 0))
            _state.PasteRequest = true;
        if (entity is not null)
        {
            if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Poser au sol"), KeyText(KeyAction.PutOnGround), false, _state.EditableSelection.Count > 0))
                _state.PutSelectionOnGround();
            int group = _state.DeletableSelection.Count;
            if (ImGui.MenuItem(group > 1 ? L($"{Icons.Trash}  Supprimer les {group} objets sélectionnés...") : L($"{Icons.Trash}  Supprimer l'objet sélectionné..."),
                    KeyText(KeyAction.Delete), false, group > 0))
                _state.RequestDelete();
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.X}  Désélectionner"), L("Échap")))
                _state.Select(null);
        }
        else
        {
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.Search}  Rechercher..."), KeyText(KeyAction.Search)))
                _searchPalette.Open();
        }
        ImGui.EndPopup();
    }

    public bool IsGizmoHovered => _gizmo.IsHovered || _roomGizmo.IsHovered || _collisionGizmo.IsHovered;

    public bool IsGizmoDragging => _gizmo.IsDragging || _roomGizmo.IsDragging || _collisionGizmo.IsDragging;

    public void Draw()
    {
        _state.Online.Update();
        _viewerPanel.BeginFrame();
        _pedStudio.BeginFrame();
        _cutscenePanel.BeginFrame();
        if (_state.WorkshopRequest is { } workshopModel)
        {
            _state.WorkshopRequest = null;
            _viewerPanel.OpenWorkshop(workshopModel);
            if (_state.OpeningTool?.Building == workshopModel)
            {
                // A building is made of several objects: they are shown around it.
                _viewerPanel.ShowOpeningTool();
                _state.WorkshopShowsMap = true;
            }
        }
        // The workshop's tools are in Properties, its parts in Scene.
        if (_state.WorkshopFront)
            _showProperties = _showScene = true;
        HandleShortcuts();
        DrawMenuBar();
        // The bars first: the panels and the 3D view share what is left of the window.
        _toolbar.Draw();
        _statusBar.Draw();
        DockLayout.Draw(_resetLayout, ScenePanel.Title, LayersTitle, PropertiesPanel.Title, StatusPanel.Title, LibraryPanel.Title);
        _resetLayout = false;
        _workspaceTabs.Draw();
        HideLibraryForPeds();
        _cutscenePanel.DrawTimeline();
        // The map's handles and overlays only while the map is shown.
        if (!_state.MapCovered)
        {
            _presence.Draw();
            _gizmo.Update(blocked: _searchPalette.IsOpen);
            _roomHover.Update(blocked: _searchPalette.IsOpen || IsGizmoHovered || IsGizmoDragging);
            _roomGizmo.Update(blocked: _searchPalette.IsOpen);
            _collisionGizmo.Update(blocked: _searchPalette.IsOpen);
            _viewportOverlay.Draw();
        }
        _noticeToast.Draw();
        _updateNotice.Draw();
        if (_showLayers) DrawLayersWindow();
        if (_showScene) _scenePanel.Draw(ref _showScene);
        if (_showLibrary) _libraryPanel.Draw(ref _showLibrary);
        if (_showProperties) _propertiesPanel.Draw(ref _showProperties);
        if (_showStatus) _statusPanel.Draw(ref _showStatus);
        if (_showArchives) _archivePanel.Draw(ref _showArchives);
        if (_showProject) _projectPanel.Draw(ref _showProject);
        if (_showHistory) _historyPanel.Draw(ref _showHistory);
        if (_focusLibrary && _showLibrary)
        {
            _focusLibrary = false;
            ImGui.SetWindowFocus(L(LibraryPanel.Title));
        }
        if (_showViewer) _viewerPanel.Draw(ref _showViewer);
        if (_showAbout) DrawAboutWindow();
        _exportWindow.Draw();
        ProjectWindow.Draw();
        _deleteWindow.Draw();
        _settingsWindow.Draw();
        if (_state.DuplicateInteriorRequest is { } duplicated)
        {
            _state.DuplicateInteriorRequest = null;
            _interiorWindow.OpenDuplicate(duplicated);
        }
        _interiorWindow.Draw();
        _imagePropWindow.Draw();
        _onlineWindow.Draw();
        _errorWindow.Draw();
        _protectedWindow.Draw();
        _welcomeWindow.Draw();
        _shortcutsWindow.Draw();
        DrawViewportMenu();
        if (_showImGuiDemo) ImGui.ShowDemoWindow(ref _showImGuiDemo);
        _searchPalette.Draw(); // Last: above the panels.
    }

    private void HandleShortcuts()
    {
        if (_state.IsCapturingKey)
            return;
        var keys = _state.Keys;
        if (keys.Pressed(KeyAction.Search) && !_searchPalette.IsOpen)
            _searchPalette.Open();
        if (keys.Pressed(KeyAction.Save))
            SaveRequested = true;
        if (ImGui.GetIO().WantTextInput || _searchPalette.IsOpen || IsGizmoDragging || _state.PendingDeletion is not null)
            return;
        // While the workshop is shown, the keys that edit the map do nothing.
        bool map = !_state.WorkshopShown && !_state.PedShown;
        if (map && keys.Pressed(KeyAction.Delete))
        {
            if (_state.Tool == TransformTool.Collision)
                _state.DeleteSelectedCollision();
            else if (_state.SelectedOccluder is not null)
                _state.DeleteSelectedOccluder();
            else if (_state.SelectedCarGenerator is not null)
                _state.DeleteSelectedCarGenerator();
            else if (_state.SelectedPathNode is not null)
                _state.DeleteSelectedPathNode();
            else if (_state.SelectedScenario is not null)
                _state.DeleteSelectedScenario();
            else if (_state.SelectedTimecycle is not null)
                _state.DeleteSelectedTimecycleZone();
            else if (_state.SelectedLodLight is not null)
                _state.DeleteSelectedLodLight();
            else if (_state.SelectedRoom is not null)
                _state.DeleteSelectedRoom();
            else if (_state.SelectedPortal is not null)
                _state.DeleteSelectedPortal();
            else
                _state.RequestDelete();
        }
        if (map && keys.Pressed(KeyAction.PutOnGround))
            _state.PutSelectionOnGround();
        if (keys.Pressed(KeyAction.NewProject))
            ProjectWindow.Open();
        if (keys.Pressed(KeyAction.OpenProject))
            OpenProjectRequested = true;
        if (keys.Pressed(KeyAction.Export) && _state.Features.Gta5Exports)
            _exportWindow.Open();
        if (keys.Pressed(KeyAction.Settings))
            _settingsWindow.Open();
        if (keys.Pressed(KeyAction.Shortcuts))
            _shortcutsWindow.Toggle();
        if (keys.Pressed(KeyAction.Viewer))
            ToggleViewer();
        if (keys.Pressed(KeyAction.Archives))
            _showArchives = !_showArchives;
        if (keys.Pressed(KeyAction.Peds))
        {
            if (_state.PedShown) _pedStudio.Close();
            else _pedStudio.Open();
        }
        if (keys.Pressed(KeyAction.Cutscenes) && _state.Features.CutsceneViewer)
            ToggleCutscenes();
        if (!map)
            return;
        if (keys.Pressed(KeyAction.Copy))
            _state.CopySelection();
        else if (keys.Pressed(KeyAction.Paste) && _state.Copied.Count > 0)
            _state.PasteRequest = true;
        if (keys.Pressed(KeyAction.Undo))
            _state.Undo();
        else if (keys.Pressed(KeyAction.Redo))
            _state.Redo();
        else if (keys.Pressed(KeyAction.Grid))
        {
            _state.Settings.ShowGrid = !_state.Settings.ShowGrid;
            _state.SaveSettings();
        }
        else if (ImGui.GetIO() is { KeyCtrl: true, KeyAlt: false } io && BookmarkKeyPressed() is int slot)
        {
            // Alt left out: Ctrl+Alt is AltGr, which types characters on some keyboards.
            if (io.KeyShift) _state.SaveCameraBookmark(slot);
            else _state.GoToCameraBookmark(slot);
        }
        else if (keys.Pressed(KeyAction.ToolSelect)) _state.Tool = TransformTool.Select;
        else if (keys.Pressed(KeyAction.ToolMove)) _state.Tool = TransformTool.Move;
        else if (keys.Pressed(KeyAction.ToolRotate)) _state.Tool = TransformTool.Rotate;
        else if (keys.Pressed(KeyAction.ToolScale)) _state.Tool = TransformTool.Scale;
        else if (keys.Pressed(KeyAction.ToolCollision)) _state.Tool = TransformTool.Collision;
    }

    // The first key of action, for menus; "" if it has none.
    private string KeyText(KeyAction action) => _state.Keys.First(action) ?? "";

    private static bool ToolKeyPressed(ImGuiKey digit, ImGuiKey keypad) =>
        ImGui.IsKeyPressed(digit, false) || ImGui.IsKeyPressed(keypad, false);

    private static int? BookmarkKeyPressed()
    {
        for (int slot = 1; slot <= AppState.BookmarkSlots; slot++)
        {
            if (ToolKeyPressed(ImGuiKey._0 + slot, ImGuiKey.Keypad0 + slot))
                return slot;
        }
        return null;
    }

    // Affichage > Signets de caméra: go to a bookmark, save the view in one, forget one.
    private void DrawBookmarkMenu()
    {
        if (!ImGui.BeginMenu(L($"{Icons.MapPinned}  Signets de caméra")))
            return;
        var bookmarks = _state.CameraBookmarks;
        for (int slot = 1; slot <= AppState.BookmarkSlots; slot++)
        {
            string label = bookmarks.TryGetValue(slot, out var b)
                ? L($"Signet {slot} : {b.X:0}, {b.Y:0}, {b.Z:0}")
                : L($"Signet {slot} (vide)");
            if (ImGui.MenuItem($"{label}###goto{slot}", $"Ctrl+{slot}", false, b is not null))
                _state.GoToCameraBookmark(slot);
        }
        ImGui.Separator();
        if (ImGui.BeginMenu(L("Enregistrer la vue actuelle dans")))
        {
            for (int slot = 1; slot <= AppState.BookmarkSlots; slot++)
            {
                string label = bookmarks.ContainsKey(slot) ? L($"Signet {slot} (remplace)") : L($"Signet {slot}");
                if (ImGui.MenuItem($"{label}###save{slot}", L($"Ctrl+Maj+{slot}")))
                    _state.SaveCameraBookmark(slot);
            }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(L("Oublier un signet"), bookmarks.Count > 0))
        {
            for (int slot = 1; slot <= AppState.BookmarkSlots; slot++)
            {
                if (bookmarks.ContainsKey(slot) && ImGui.MenuItem(L($"Signet {slot}") + $"###forget{slot}"))
                    _state.ForgetCameraBookmark(slot);
            }
            ImGui.EndMenu();
        }
        ImGui.EndMenu();
    }

    private const float MenuBarPaddingY = 9f;

    private static bool Menu(string label, bool enabled = true, string? disabledTip = null)
    {
        // Centred in the (taller) bar: ImGui would put the menus lower, with the bar's padding above.
        ImGui.SetCursorPosY((ImGui.GetWindowHeight() - ImGui.GetFrameHeight()) * 0.5f - 5f);
        bool open = ImGui.BeginMenu(label, enabled);
        WindowChrome.TrackItem();
        if (!enabled && disabledTip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(disabledTip);
        return open;
    }

    // Projects, additions and exports are for GTA V (FiveM resources).
    private static string Gta5OnlyTip => L("GTA V seulement : dans ce jeu, la carte se consulte sans être modifiée.");

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
            DrawFileProjectItems();
            ImGui.Separator();
            int toSave = _state.PendingSaveCount;
            if (ImGui.MenuItem(L($"{Icons.Save}  Enregistrer les fichiers modifiés{(toSave > 0 ? $" ({toSave})" : "")}"), KeyText(KeyAction.Save), false, toSave > 0))
                SaveRequested = true;
            if (ImGui.MenuItem(L($"{Icons.Package}  Exporter en ressource FiveM..."), KeyText(KeyAction.Export), false, _state.Features.Gta5Exports))
                _exportWindow.Open();
            if (!_state.Features.Gta5Exports && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(Gta5OnlyTip);
            if (ImGui.MenuItem(L($"{Icons.Package}  Exporter en pack solo..."), null, false, _state.Features.Gta5Exports))
                _exportWindow.OpenSolo();
            if (!_state.Features.Gta5Exports && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(Gta5OnlyTip);
            ImGui.Separator();
            if (ImGui.MenuItem(L("Quitter")))
                ExitRequested = true;
            ImGui.EndMenu();
        }

        if (Menu(L("Édition")))
        {
            var history = _state.History;
            if (ImGui.MenuItem(history.NextUndo is { } undo ? L($"Annuler : {undo.Description}") : L("Annuler"), KeyText(KeyAction.Undo), false, history.CanUndo))
                _state.Undo();
            if (ImGui.MenuItem(history.NextRedo is { } redo ? L($"Rétablir : {redo.Description}") : L("Rétablir"), KeyText(KeyAction.Redo), false, history.CanRedo))
                _state.Redo();
            ImGui.Separator();
            int selected = _state.SelectedEntities.Count;
            if (ImGui.MenuItem(selected > 1 ? L($"{Icons.Copy}  Copier les {selected} objets sélectionnés") : L($"{Icons.Copy}  Copier l'objet sélectionné"),
                KeyText(KeyAction.Copy), false, selected > 0))
                _state.CopySelection();
            int copied = _state.Copied.Count;
            if (ImGui.MenuItem(copied > 1 ? L($"{Icons.Copy}  Coller les {copied} objets") : L($"{Icons.Copy}  Coller"), KeyText(KeyAction.Paste), false, copied > 0))
                _state.PasteRequest = true;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute une copie au sol, au centre de la vue 3D, dans le YMAP du projet (le groupe garde sa disposition)."));
            int group = _state.DeletableSelection.Count;
            if (ImGui.MenuItem(group > 1 ? L($"{Icons.Trash}  Supprimer les {group} objets sélectionnés...") : L($"{Icons.Trash}  Supprimer l'objet sélectionné..."),
                KeyText(KeyAction.Delete), false, group > 0))
                _state.RequestDelete();
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Poser au sol"), KeyText(KeyAction.PutOnGround), false, _state.EditableSelection.Count > 0))
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
            if (ImGui.MenuItem(L($"{Icons.Search}  Rechercher..."), KeyText(KeyAction.Search)))
                _searchPalette.Open();
            ImGui.Separator();
            ImGui.TextDisabled(history.Count == 0 ? L("Aucune modification") : L($"{history.Count} modification(s), pas encore enregistrée(s)"));
            ImGui.EndMenu();
        }

        if (Menu(L("Projet"), _state.Features.Editing, Gta5OnlyTip))
        {
            DrawProjectContentItems();
            ImGui.EndMenu();
        }

        if (Menu(L("Créer"), _state.Features.Editing, Gta5OnlyTip))
        {
            if (ImGui.MenuItem(L($"{Icons.LibraryBig}  Placer un prop de la bibliothèque...")))
                ShowLibrary();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Ouvre la bibliothèque de props : double-clic sur un prop, ou glisser-le dans la vue 3D."));
            if (ImGui.MenuItem(L($"{Icons.Box}  Placer un objet du jeu...")))
            {
                _showViewer = true;
                ImGui.SetWindowFocus(ViewerPanel.Title);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Ouvre la visionneuse : cherche un objet ou un véhicule du jeu, puis « Placer »."));
            if (ImGui.MenuItem(L($"{Icons.Star}  Prop depuis une image..."), null, false, _state.World.Maps is not null))
                _imagePropWindow.Open();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Fait un médaillon en 3D à partir d'une image (insigne, logo), avec sa collision, et l'ajoute à la bibliothèque de props."));
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.House}  Nouvel intérieur (MLO)..."), null, false, _state.World.Maps is not null))
                _interiorWindow.Open();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Crée un intérieur vide devant la caméra (une salle et une porte), dans un .ytyp du projet."));
            var selectedInterior = _state.Selection is { } chosen ? chosen.IsInterior ? chosen : chosen.Interior : null;
            if (ImGui.MenuItem(L($"{Icons.House}  Dupliquer l'intérieur sous un nouveau nom..."), null, false, selectedInterior is not null) && selectedInterior is not null)
                _interiorWindow.OpenDuplicate(selectedInterior);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Copie l'intérieur sélectionné (ou celui de l'objet sélectionné) sous un autre nom, avec ses objets, dans un .ytyp du projet : la copie se modifie sans toucher l'original."));
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.Car}  Place de véhicule garé"), null, false, _state.World.Maps is not null))
                _state.AddCarGenerator();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute une place (car generator) au sol devant la caméra, dans le YMAP du projet : le jeu peut y garer un véhicule. À régler ensuite dans Propriétés ou avec les poignées."));
            if (ImGui.MenuItem(L($"{Icons.Sunset}  Zone de timecycle"), null, false, _state.World.Maps is not null))
                _state.AddTimecycleZone();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute une zone d'ambiance (timecycle modifier) devant la caméra, dans le YMAP du projet : le jeu y change la lumière, les couleurs ou le brouillard. Choisissez ensuite son modificateur dans Propriétés."));
            if (ImGui.MenuItem(L($"{Icons.Route}  Nœud de route"), null, false, _state.World.PathNetwork is not null))
                _state.AddPathNode(null);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute un nœud de route (pathnode) au sol devant la caméra, avec les réglages du nœud de véhicules le plus proche. Reliez-le ensuite à un autre nœud dans Propriétés."));
            if (ImGui.MenuItem(L($"{Icons.Drama}  Point de scénario"), null, false, _state.World.Scenarios is not null))
                _state.AddScenarioPoint(null);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute un point de scénario au sol devant la caméra (un PNJ, un véhicule ou un animal y fait une activité), copie du point le plus proche. Choisissez ensuite son type dans Propriétés."));
            if (ImGui.MenuItem(L($"{Icons.Moon}  Lumière LOD"), null, false, _state.World.Maps is not null))
                _state.AddLodLight();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Ajoute une lumière vue de loin la nuit (LOD light) devant la caméra, dans les fichiers de lumières du projet. Réglez ensuite sa couleur, ses heures et sa portée dans Propriétés."));
            if (ImGui.BeginMenu(L($"{Icons.ScanEye}  Occlusion")))
            {
                DrawOcclusionMenuItems();
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }

        if (Menu(L("Outils")))
        {
            ImGui.MenuItem(L(ViewerPanel.Title), KeyText(KeyAction.Viewer), ref _showViewer);
            if (ImGui.MenuItem(L($"{Icons.PersonStanding}  Visionneuse de personnages"), KeyText(KeyAction.Peds), _state.PedOpen, _state.Features.PedViewer))
                _pedStudio.Open();
            if (ImGui.MenuItem(L($"{Icons.Film}  Visionneuse de cinématiques"), KeyText(KeyAction.Cutscenes), _state.CutsceneOpen, _state.Features.CutsceneViewer))
                ToggleCutscenes();
            if (ImGui.BeginMenu(L($"{Icons.LibraryBig}  Bibliothèque de props")))
            {
                bool shown = _showLibrary;
                if (ImGui.MenuItem(L("Afficher la bibliothèque"), null, ref shown))
                {
                    if (shown) ShowLibrary();
                    else _showLibrary = false;
                }
                ImGui.Separator();
                DrawLibraryItems();
                ImGui.EndMenu();
            }
            ImGui.MenuItem(L(ArchivePanel.Title), KeyText(KeyAction.Archives), ref _showArchives);
            if (ImGui.MenuItem(L($"{Icons.Users}  Online..."), null, _onlineWindow.IsOpen))
                _onlineWindow.Toggle();
            if (ImGui.MenuItem(L($"{Icons.ScanEye}  Vérifier une ressource FiveM..."), null, false, _state.World.Maps is not null && !_state.IsOtherGame))
                CheckResourceRequested = true;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L("Vérifie un dossier de ressource FiveM (le tien ou un mapping téléchargé) sans lancer le jeu : fxmanifest.lua,\nfichiers en double dans stream, objets sans type, types sans modèle ni textures, fichiers trop gros..."));
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.Settings}  Paramètres..."), KeyText(KeyAction.Settings)))
                _settingsWindow.Open();
            ImGui.EndMenu();
        }

        if (Menu(L("Affichage")))
        {
            ImGui.MenuItem(L(ScenePanel.Title), null, ref _showScene);
            ImGui.MenuItem(L(LayersTitle), null, ref _showLayers);
            ImGui.MenuItem(L(PropertiesPanel.Title), null, ref _showProperties);
            ImGui.MenuItem(L(StatusPanel.Title), null, ref _showStatus);
            ImGui.MenuItem(L(ProjectPanel.Title), null, ref _showProject);
            ImGui.MenuItem(L(HistoryPanel.Title), null, ref _showHistory);
            ImGui.Separator();
            bool showGrid = _state.Settings.ShowGrid;
            if (ImGui.MenuItem(L("Grille au sol"), KeyText(KeyAction.Grid), ref showGrid))
            {
                _state.Settings.ShowGrid = showGrid;
                _state.SaveSettings();
            }
            DrawBookmarkMenu();
            if (ImGui.MenuItem(L("Réinitialiser la disposition des panneaux")))
            {
                _showLayers = _showScene = _showProperties = _showStatus = _showLibrary = true;
                _resetLayout = true;
            }
#if DEBUG
            ImGui.Separator();
            ImGui.MenuItem(L("Démo ImGui (développeurs)"), null, ref _showImGuiDemo);
#endif
            ImGui.EndMenu();
        }

        if (Menu(L("Aide")))
        {
            if (ImGui.MenuItem(L("Écran d'accueil")))
                _welcomeWindow.Open();
            if (ImGui.MenuItem(L($"{Icons.Keyboard}  Raccourcis clavier"), KeyText(KeyAction.Shortcuts)))
                _shortcutsWindow.Toggle();
            if (ImGui.MenuItem(L($"{Icons.TriangleAlert}  Erreurs et rapport...")))
                _errorWindow.Open();
            ImGui.Separator();
            if (ImGui.MenuItem(L($"À propos de {Program.Name}")))
                _showAbout = true;
            ImGui.EndMenu();
        }

        // Author credit, right-aligned before the window buttons.
        string credit = L($"{Program.Name} par {Program.Author}");
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
        if (ImGui.MenuItem(L($"{Icons.Trash}  Supprimer l'occluder sélectionné"), KeyText(KeyAction.Delete), false, occluder is not null))
            _state.DeleteSelectedOccluder();
    }

    // Toolbar's project button: the project items of both the File and Project menus.
    private void DrawToolbarProjectMenu()
    {
        DrawFileProjectItems();
        ImGui.Separator();
        DrawProjectContentItems();
    }

    // File menu: new, open, recent, close.
    private void DrawFileProjectItems()
    {
        if (ImGui.MenuItem(L($"{Icons.FolderPlus}  Nouveau projet..."), KeyText(KeyAction.NewProject)))
            ProjectWindow.Open();
        if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Ouvrir un projet..."), KeyText(KeyAction.OpenProject)))
            OpenProjectRequested = true;
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
        DrawOpenProjectsItems(recent);
        if (ImGui.MenuItem(_state.ExtraProjects.Count > 0 ? L($"{Icons.X}  Fermer les projets") : L($"{Icons.X}  Fermer le projet"), null, false, _state.Project is not null))
            CloseProjectRequested = true;
    }

    // Several projects at once: others shown and edited with the open one, each saved into its own folder.
    private void DrawOpenProjectsItems(List<string> recent)
    {
        bool hasProject = _state.Project is not null;
        ImGui.Separator();
        if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Ouvrir un autre projet en plus..."), null, false, hasProject))
            AddViewProjectRequested = "";
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Affiche un autre projet (une autre ressource) en même temps que celui-ci, sans le fermer.\nChaque fichier modifié est enregistré dans son propre projet ; les objets ajoutés vont dans le projet actif.\nLa carte est rechargée."));
        var others = recent.Where(f => !_state.ExtraProjects.Any(p => string.Equals(p.Folder, f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (ImGui.BeginMenu(L("Ouvrir en plus un projet récent"), hasProject && others.Count > 0))
        {
            foreach (var folder in others)
            {
                bool exists = NodeRunner.Editor.MapProject.Exists(folder);
                if (ImGui.MenuItem($"{Path.GetFileName(folder)}##more{folder}", null, false, exists))
                    AddViewProjectRequested = folder;
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(exists ? folder : L($"{folder}\nIntrouvable (déplacé ou supprimé)"));
            }
            ImGui.EndMenu();
        }
        if (_state.Project is { } active && ImGui.BeginMenu(L($"Projets ouverts ({_state.ExtraProjects.Count + 1})"), _state.ExtraProjects.Count > 0))
        {
            ImGui.MenuItem(L($"{active.Name} (actif)") + "##active", null, true, false);
            foreach (var project in _state.ExtraProjects)
            {
                if (ImGui.BeginMenu($"{project.Name}##open{project.Folder}"))
                {
                    if (ImGui.MenuItem(L("Rendre actif")))
                        ActivateProjectRequested = project.Folder;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(L("Le projet actif reçoit les objets ajoutés, les collisions, l'occlusion et les pathnodes, et c'est lui qu'on exporte.\nLa carte est rechargée ; les autres projets restent affichés."));
                    if (ImGui.MenuItem(L("Retirer de la vue")))
                        RemoveViewProjectRequested = project.Folder;
                    ImGui.EndMenu();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(project.Folder);
            }
            ImGui.EndMenu();
        }
        ImGui.Separator();
    }

    // Project menu: imports, files added to the open project, project contents.
    private void DrawProjectContentItems()
    {
        if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Importer un mapping...")))
            ImportMappingRequested = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Ouvre le mapping de quelqu'un d'autre (ressource FiveM ou dossier stream) dans un nouveau projet.\nLes fichiers sont copiés : le dossier d'origine n'est pas modifié.\nAstuce : on peut aussi glisser un dossier, un .oiv ou un .zip sur la fenêtre."));
        if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Importer un mod OpenIV (.oiv) ou un .zip...")))
            ImportPackageRequested = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Les fichiers de carte du mod (modèles, textures, collisions...) vont dans le dossier stream d'un nouveau projet,\nsous leur nom du jeu : ils remplacent les fichiers du jeu, prêts pour l'export en ressource FiveM."));
        ImGui.Separator();
        bool hasProject = _state.Project is not null;
        if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Ajouter des fichiers au projet..."), null, false, hasProject))
            AddFilesRequested = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Ajoute des fichiers au projet ouvert : .ydr, .ytyp, .ybn... ou leurs exports XML (Sollumz, CodeWalker), convertis au passage.\nUn fichier du même nom déjà dans le projet est remplacé (nouvel export depuis Blender), puis la carte est rechargée.\nAstuce : on peut aussi glisser les fichiers sur la fenêtre."));
        if (ImGui.MenuItem(L($"{Icons.ArrowDownToLine}  Ajouter un dossier au projet..."), null, false, hasProject))
            AddFolderRequested = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Ajoute tout un dossier au projet ouvert (dossier d'export de Sollumz, dossier stream...), sous-dossiers compris."));
        if (_state.Project?.WatchedFolder is { } watched)
        {
            if (ImGui.MenuItem(L($"{Icons.RefreshCw}  Délier le dossier Sollumz « {Path.GetFileName(watched)} »")))
                UnlinkFolderRequested = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L($"Dossier lié : {watched}\nÀ chaque nouvel export dans ce dossier, les fichiers sont ajoutés au projet et la carte est rechargée.\nDélier arrête la surveillance (les fichiers déjà ajoutés restent dans le projet)."));
        }
        else if (ImGui.MenuItem(L($"{Icons.RefreshCw}  Lier un dossier Sollumz (surveillé)..."), null, false, hasProject))
            LinkFolderRequested = true;
        if (_state.Project?.WatchedFolder is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Lie au projet le dossier où Sollumz (Blender) exporte : ses fichiers sont ajoutés tout de suite,\npuis à chaque nouvel export (XML convertis au passage), et la carte est rechargée toute seule."));
        ImGui.Separator();
        if (ImGui.MenuItem(L($"{Icons.ListTree}  Contenu du projet (objets ajoutés, déplacés, supprimés)"), null, false, hasProject))
            ShowProjectPanel();
        if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Ouvrir le dossier du projet"), null, false, hasProject)
            && _state.Project is { } project && Directory.Exists(project.Folder))
            System.Diagnostics.Process.Start("explorer.exe", project.Folder);
    }

    // Tools > Props library: managing the packs.
    private void DrawLibraryItems()
    {
        if (ImGui.MenuItem(L("Ajouter un pack (dossier)...")))
            _state.LibraryRequest = LibraryAction.AddFolder;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Copie un pack de props addons (ressource FiveM, dossier stream) dans la bibliothèque :\nses props sont disponibles dans tous les projets (Visionneuse > Bibliothèque > Placer)."));
        if (ImGui.MenuItem(L("Ajouter un pack (.zip)...")))
            _state.LibraryRequest = LibraryAction.AddZip;
        if (ImGui.MenuItem(L("Ouvrir le dossier de la bibliothèque")))
            ViewerPanel.OpenLibraryFolder(_state);
        if (ImGui.MenuItem(L("Relire la bibliothèque")))
            _state.LibraryRequest = LibraryAction.Reload;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Après avoir copié ou retiré un pack à la main dans le dossier : recharge la carte."));
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
