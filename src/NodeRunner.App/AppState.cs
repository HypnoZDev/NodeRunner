using NodeRunner.App.Configuration;
using NodeRunner.App.World;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

/// <summary>Application-wide state shared by the window and the UI.</summary>
internal sealed class AppState
{
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }

    /// <summary>The installation in use, or null if none was found.</summary>
    public GameInstallation? Game { get; private set; }

    /// <summary>Other valid installations found on this PC (e.g. both Legacy and Enhanced).</summary>
    public IReadOnlyList<GameInstallation> OtherInstallations { get; private set; } = [];

    public string AdapterName { get; set; } = "?";

    /// <summary>The 3D view's camera.</summary>
    public Camera Camera { get; } = new();

    /// <summary>Game data read in the background.</summary>
    public WorldLoader World { get; }

    /// <summary>
    /// The main selected object (the last one clicked), null if none: Properties and the handles show it.
    /// Forgotten when the map is loaded again (another version or DLC): it belongs to the previous one; deleted
    /// objects leave the selection.
    /// </summary>
    public MapEntity? Selection => SelectedEntities is [.., var last] ? last : null;

    /// <summary>All the selected objects (Ctrl+click adds or removes one), the main one last.</summary>
    public IReadOnlyList<MapEntity> SelectedEntities
    {
        get
        {
            if (_selectionLibrary != World.Maps)
                return [];
            _selection.RemoveAll(IsGone);
            return _selection;
        }
    }

    /// <summary>Deleted, or inside a deleted interior.</summary>
    private static bool IsGone(MapEntity entity) => entity is { IsDeleted: true } or { Interior.IsDeleted: true };

    private readonly List<MapEntity> _selection = [];
    private MapLibrary? _selectionLibrary;

    /// <summary>Selects only this object (null: nothing).</summary>
    public void Select(MapEntity? entity)
    {
        _selection.Clear();
        if (entity is not null)
            _selection.Add(entity);
        _selectionLibrary = World.Maps;
    }

    /// <summary>Selects these objects, the main one last.</summary>
    public void SelectAll(IReadOnlyList<MapEntity> entities)
    {
        Select(null);
        _selection.AddRange(entities.Distinct());
    }

    /// <summary>The selected objects that can be moved or deleted (not those defined by an interior's archetype), the main one last.</summary>
    public IReadOnlyList<MapEntity> EditableSelection => SelectedEntities.Where(e => e.CanEdit).ToList();

    /// <summary>Ctrl+click: adds the object to the selection (it becomes the main one), or removes it if already in.</summary>
    public void ToggleSelect(MapEntity entity)
    {
        if (_selectionLibrary != World.Maps)
            Select(null);
        if (!_selection.Remove(entity))
            _selection.Add(entity);
    }

    public bool IsSelected(MapEntity entity) => SelectedEntities.Contains(entity);

    /// <summary>Tool of the 3D view: selection only, or handles to move, turn or scale the selected object.</summary>
    public TransformTool Tool
    {
        get => Settings.Tool;
        set
        {
            if (Settings.Tool == value)
                return;
            Settings.Tool = value;
            SaveSettings();
        }
    }

    /// <summary>
    /// Collision pieces the export will remove (deleted buildings), drawn in red in the 3D view while the export window
    /// shows them; null otherwise. A new list is set at each change.
    /// </summary>
    public IReadOnlyList<CollisionFileCut>? CollisionPreview { get; set; }

    /// <summary>File of <see cref="CollisionPreview"/> under the mouse in the export window (lit, the others fade), or null.</summary>
    public CollisionFileCut? CollisionPreviewHovered { get; set; }

    /// <summary>Occluders the export will remove (deleted buildings), drawn in blue like <see cref="CollisionPreview"/>; null otherwise.</summary>
    public IReadOnlyList<OccluderFileCut>? OcclusionPreview { get; set; }

    /// <summary>File of <see cref="OcclusionPreview"/> under the mouse in the export window, or null.</summary>
    public OccluderFileCut? OcclusionPreviewHovered { get; set; }

    /// <summary>Deletion asked for and waiting for confirmation (the window shows what goes), null if none.</summary>
    public DeletionPlan? PendingDeletion { get; private set; }

    /// <summary>Asks to delete the selected objects: shows what goes with them before anything is deleted.</summary>
    public void RequestDelete()
    {
        if (EditableSelection is { Count: > 0 } targets)
            PendingDeletion = DeletionPlan.For(targets);
    }

    public void CancelDelete() => PendingDeletion = null;

    /// <summary>Deletes what <see cref="PendingDeletion"/> lists (one undo step).</summary>
    public void ConfirmDelete()
    {
        if (PendingDeletion is not { } plan || World.Maps is not { } library)
            return;
        PendingDeletion = null;
        string name = plan.Targets.Count > 1 ? $"{plan.Targets.Count} objets"
            : World.Names?.Describe(plan.Target.ArchetypeHash) ?? $"0x{plan.Target.ArchetypeHash:X8}";
        History.Add(DeleteEdit.Apply(library, plan, $"Supprimer {name}"));
        Select(null);
    }

    /// <summary>Object type to add, waiting for the main form to find the ground at the centre of the 3D view; 0 if none.</summary>
    public uint PlaceRequest { get; set; }

    /// <summary>Result of the last editing command (add an object, put it on the ground...), shown a few seconds in the status bar.</summary>
    public (string Text, bool IsWarning, DateTime Time)? Notice { get; private set; }

    public void Notify(string text, bool warning = false) => Notice = (text, warning, DateTime.Now);

    /// <summary>
    /// Adds an object of type <paramref name="archetypeHash"/> standing on <paramref name="ground"/>, in the project's
    /// own map (see <see cref="MapProject.AddedMapFile"/>), selects it and takes the "move" tool. One undo step.
    /// </summary>
    public void Place(uint archetypeHash, System.Numerics.Vector3 ground)
    {
        Notice = null;
        if (World.Maps is not { } library || ProjectMap(library) is not { } map)
            return;
        var archetype = library.GetArchetype(archetypeHash);
        var entity = Adding.NewEntity(archetypeHash, archetype, Adding.StandingOn(ground, archetype), System.Numerics.Quaternion.Identity);
        // The point found in the picture may be a tree top or a model without collision: rest on the collision below.
        if (GroundPosition(entity, archetype) is { } onCollision)
            entity.SetPlacement(onCollision, entity.Orientation, entity.Scale);
        string name = World.Names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        History.Add(AddEdit.Apply(library, map, entity, $"Ajouter {name}"));
        Select(entity);
        if (Tool == TransformTool.Select)
            Tool = TransformTool.Move;
    }

    /// <summary>Finds the collision under the objects being edited.</summary>
    public CollisionQuery CollisionQuery { get; } = new();

    /// <summary>
    /// Position that puts the object on the collision below it (see <see cref="GroundSnap"/>), or null when the
    /// collisions are not loaded yet or nothing lies below.
    /// </summary>
    public System.Numerics.Vector3? GroundPosition(MapEntity entity, Archetype? archetype)
    {
        if (World.Collisions is null)
            return null;
        var box = GroundSnap.WorldBox(entity, archetype) ?? (entity.Position, entity.Position);
        // Everything below the start of the rays, down to the bottom of the map.
        var collisions = CollisionQuery.CollisionIn(World,
            box.Min with { Z = -1000f }, box.Max with { Z = GroundSnap.StartHeight(box.Min.Z, box.Max.Z) });
        return GroundSnap.Snap(entity, archetype, collisions);
    }

    /// <summary>
    /// Moves each selected object up or down so that it rests on the collision below it (see <see cref="GroundSnap"/>).
    /// One undo step.
    /// </summary>
    public void PutSelectionOnGround()
    {
        if (EditableSelection is not { Count: > 0 } entities || World.Maps is not { } library)
            return;
        if (World.Collisions is null)
        {
            Notify("Les collisions ne sont pas encore chargées.", warning: true);
            return;
        }

        var before = entities.Select(Placement.Of).ToArray();
        var moved = new List<(MapEntity Entity, float Height)>();
        int withoutGround = 0;
        foreach (var entity in entities)
        {
            if (GroundPosition(entity, library.GetArchetype(entity.ArchetypeHash)) is not { } position)
            {
                withoutGround++;
                continue;
            }
            float height = position.Z - entity.Position.Z;
            if (MathF.Abs(height) < 0.005f)
                continue;
            entity.SetPlacement(position, entity.Orientation, entity.Scale);
            moved.Add((entity, height));
        }

        string Name(MapEntity e) => World.Names?.Describe(e.ArchetypeHash) ?? $"0x{e.ArchetypeHash:X8}";
        string missing = withoutGround == 0 ? "" : $" Aucune collision sous {withoutGround} objet{(withoutGround > 1 ? "s" : "")}.";
        if (moved.Count == 0)
        {
            if (withoutGround == entities.Count)
                Notify(CollisionQuery.Errors.Count > 0
                    ? $"Collision illisible sous {Name(entities[^1])} : {CollisionQuery.Errors[0]}"
                    : entities.Count == 1 ? $"Aucune collision sous {Name(entities[0])}." : "Aucune collision sous les objets sélectionnés.", warning: true);
            else
                Notify((entities.Count == 1 ? $"{Name(entities[0])} est déjà posé au sol." : "Les objets sont déjà posés au sol.") + missing, withoutGround > 0);
            return;
        }

        if (entities.Count == 1)
        {
            var (entity, height) = moved[0];
            History.Add(new PlacementEdit(entity, before[0], Placement.Of(entity), $"Poser au sol {Name(entity)}"));
            Notify($"{Name(entity)} posé au sol ({(height < 0 ? "descendu" : "monté")} de {MathF.Abs(height):0.00} m).");
            return;
        }
        // One step for the whole selection (objects already on the ground included: they keep their place).
        History.Add(new GroupPlacementEdit(entities, before, [.. entities.Select(Placement.Of)], $"Poser au sol {entities.Count} objets"));
        Notify($"{moved.Count} objet{(moved.Count > 1 ? "s posés" : " posé")} au sol sur {entities.Count}.{missing}", withoutGround > 0);
    }

    /// <summary>Language of the copied positions (Lua, JSON, C#), remembered in the settings.</summary>
    public CopyFormat CopyFormat
    {
        get => Settings.CopyFormat;
        set
        {
            if (Settings.CopyFormat == value)
                return;
            Settings.CopyFormat = value;
            SaveSettings();
        }
    }

    /// <summary>A position written in the chosen <see cref="CopyFormat"/>, with the heading if given.</summary>
    public string FormatPosition(System.Numerics.Vector3 position, float? heading = null) =>
        PositionText.Position(position, heading, CopyFormat);

    /// <summary>The selected objects (type name, position, heading) as a list in the chosen <see cref="CopyFormat"/>.</summary>
    public string FormatSelection() => PositionText.List(
        SelectedEntities.Select(e => new PositionItem(World.Names?.Describe(e.ArchetypeHash) ?? $"0x{e.ArchetypeHash:X8}", e.Position, Ui.FiveMFormat.Heading(e))),
        CopyFormat);

    /// <summary>Objects copied by Ctrl+C (see <see cref="ObjectCopy"/>), empty if none.</summary>
    public IReadOnlyList<CopiedObject> Copied { get; private set; } = [];

    /// <summary>
    /// Ctrl+C: remembers the selected objects (interiors excepted: they cannot be added yet). Objects of an interior
    /// can be copied: the copies are ordinary objects of the project.
    /// </summary>
    public void CopySelection()
    {
        if (World.Maps is not { } library || SelectedEntities is not { Count: > 0 } selected)
            return;
        var copied = selected.Where(e => !e.IsInterior).ToList();
        if (copied.Count == 0)
        {
            Notify("Les intérieurs (MLO) ne peuvent pas encore être copiés.", warning: true);
            return;
        }
        Copied = ObjectCopy.Copy(copied, library.GetArchetype);
        string skipped = copied.Count < selected.Count ? " (intérieurs non copiés)" : "";
        Notify(copied.Count == 1
            ? $"{World.Names?.Describe(copied[0].ArchetypeHash) ?? "Objet"} copié{skipped}. Ctrl+V pour le coller au centre de la vue."
            : $"{copied.Count} objets copiés{skipped}. Ctrl+V pour les coller au centre de la vue.");
    }

    /// <summary>Ctrl+V asked: waiting for the main form to find the ground at the centre of the 3D view.</summary>
    public bool PasteRequest { get; set; }

    /// <summary>
    /// Adds the copied objects to the project's own map, the base of the group on <paramref name="ground"/>; selects them
    /// and takes the "move" tool. One undo step.
    /// </summary>
    public void Paste(System.Numerics.Vector3 ground)
    {
        Notice = null;
        if (Copied.Count == 0 || World.Maps is not { } library || ProjectMap(library) is not { } map)
            return;
        var pasted = ObjectCopy.Paste(Copied, ground, library.GetArchetype);
        string name = pasted.Count == 1 ? World.Names?.Describe(pasted[0].ArchetypeHash) ?? $"0x{pasted[0].ArchetypeHash:X8}" : $"{pasted.Count} objets";
        History.Add(AddEdit.Apply(library, map, pasted, $"Coller {name}"));
        SelectAll(pasted);
        if (Tool == TransformTool.Select)
            Tool = TransformTool.Move;
    }

    /// <summary>The project's own map, where added objects go (created if needed); null, with a message, without a project.</summary>
    private MapData? ProjectMap(MapLibrary library)
    {
        if (Project is not { } project)
        {
            Notify("Ouvre ou crée d'abord un projet : les objets ajoutés vont dans le YMAP du projet.", warning: true);
            return null;
        }
        bool named = project.AddedMapName is not null;
        string file = project.AddedMapFile();
        if (!named)
            SaveProject();
        return library.FindCustomMap(file) ?? library.CreateCustomMap(file);
    }

    /// <summary>Moves the camera in front of an object, at a distance suited to its size, keeping the viewing direction.</summary>
    public void FocusOn(MapEntity entity)
    {
        var center = entity.Position;
        float radius = 5f;
        if (World.Maps?.GetArchetype(entity.ArchetypeHash) is { } archetype)
        {
            center = System.Numerics.Vector3.Transform((archetype.BoundsMin + archetype.BoundsMax) * 0.5f, entity.Transform);
            radius = System.Numerics.Vector3.Distance(archetype.BoundsMin, archetype.BoundsMax) * 0.5f * Math.Max(entity.Scale.X, entity.Scale.Z);
        }
        FocusOn(center, radius);
    }

    /// <summary>Moves the camera so that a sphere fills the view, keeping the viewing direction.</summary>
    public void FocusOn(System.Numerics.Vector3 center, float radius)
    {
        float distance = Math.Clamp(radius * 2.5f, 6f, 3000f);
        Camera.Position = center - Camera.Forward * distance;
        Camera.LookAt(center);
    }

    /// <summary>Point marked in the 3D view (a position reached by the global search), with its heading if any.</summary>
    public (System.Numerics.Vector3 Position, System.Numerics.Vector2? Facing)? Marker { get; set; }

    /// <summary>
    /// Moves the camera above and behind a point (e.g. a position pasted from a FiveM script), looking down at it:
    /// from behind <paramref name="facing"/> if given (the heading of a vector4), otherwise in the current direction.
    /// </summary>
    public void LookFromAbove(System.Numerics.Vector3 target, System.Numerics.Vector2? facing = null)
    {
        var forward = Camera.Forward;
        var flat = facing ?? new System.Numerics.Vector2(forward.X, forward.Y);
        flat = flat.LengthSquared() > 1e-6f ? System.Numerics.Vector2.Normalize(flat) : System.Numerics.Vector2.UnitY;
        Camera.Position = target + new System.Numerics.Vector3(-flat.X * 18f, -flat.Y * 18f, 12f);
        Camera.LookAt(target);
    }

    /// <summary>
    /// Changes made to the map, for undo / redo. Emptied when the map is loaded again (another version or DLC):
    /// the changes were made to the previous one, and are lost.
    /// </summary>
    public EditHistory History
    {
        get
        {
            if (_historyLibrary != World.Maps)
            {
                _history.Clear();
                _historyLibrary = World.Maps;
            }
            return _history;
        }
    }

    private readonly EditHistory _history = new();
    private MapLibrary? _historyLibrary;

    /// <summary>Undoes the last change and selects the objects it concerned (those still on the map).</summary>
    public void Undo()
    {
        if (History.Undo() is { Selection.Count: > 0 } edit)
            SelectAll(edit.Selection);
    }

    /// <summary>Does the last undone change again and selects the objects it concerned (those still on the map).</summary>
    public void Redo()
    {
        if (History.Redo() is { Selection.Count: > 0 } edit)
            SelectAll(edit.Selection);
    }

    /// <summary>
    /// Map files to write when saving: those changed in this session (even if the change was undone since,
    /// so that a file saved earlier is put back) and those saved before. Empty when nothing was changed.
    /// </summary>
    public IReadOnlyList<MapData> MapsToSave
    {
        get
        {
            var history = History; // Also forgets the saved maps of a previous map loading.
            if (_savedMapsLibrary != World.Maps)
            {
                _savedMaps.Clear();
                _savedMapsLibrary = World.Maps;
            }
            var maps = history.All
                .SelectMany(e => e.Entities)
                .Select(entity => (entity.Interior ?? entity).Map)
                .OfType<MapData>()
                .Concat(_savedMaps)
                .Distinct()
                .ToList();

            // Deleting objects renumbers the ones left in their map: the maps whose objects point to them (to their
            // lower-detail parent) are rewritten too, with the new numbers.
            if (World.Maps is { } library)
            {
                var withDeletions = maps.Where(m => m.Entities.Any(e => e.IsDeleted) || history.All.OfType<DeleteEdit>().SelectMany(d => d.Entities).Any(e => e.Map == m)).ToList();
                maps.AddRange(withDeletions.SelectMany(library.ChildMaps).Where(m => !maps.Contains(m)).Distinct().ToList());

                // Their parent maps too when a simplified model lost children (now or in an earlier session): the number of
                // children it declares is rewritten, otherwise the game shows it even up close. Up to the coarsest level.
                var edited = maps.Concat(library.Maps.Where(m => m.IsSaved)).Distinct().ToList();
                while (edited.Count > 0)
                {
                    var parents = edited.Select(library.ParentMap).OfType<MapData>().Distinct()
                        .Where(p => !maps.Contains(p) && MapLibrary.HasLostChildren(p)).ToList();
                    maps.AddRange(parents);
                    edited = parents;
                }
            }
            return maps;
        }
    }

    /// <summary>The model viewer (vehicles, object types), drawn by the main form, shown by the "Visionneuse" panel.</summary>
    public ModelViewer Viewer { get; } = new();

    /// <summary>
    /// Map files of a FiveM export: those to save, plus the saved versions taken back when the map was loaded
    /// (modified in an earlier session).
    /// </summary>
    public IReadOnlyList<MapData> MapsToExport =>
        MapsToSave.Concat(World.Maps?.Maps.Where(m => m.IsSaved) ?? []).Distinct().ToList();

    private readonly HashSet<MapData> _savedMaps = [];
    private MapLibrary? _savedMapsLibrary;

    /// <summary>Result of the last save, for the status bar: text, success, time.</summary>
    public (string Text, bool Success, DateTime Time)? LastSave { get; private set; }

    /// <summary>Writes the modified map files to the open project's "stream" folder (nothing without a project).</summary>
    public void SaveMaps()
    {
        if (SaveFolder is not { } folder)
            return;
        var maps = MapsToSave;
        if (maps.Count == 0)
            return;
        var report = MapFileSaver.Save(World, maps, folder);
        foreach (var map in maps.Where(m => m.FilePath is { } p && report.Saved.Contains(Path.GetFileName(p))))
            _savedMaps.Add(map);
        if (report.Errors.Count == 0)
        {
            History.MarkSaved();
            LastSave = ($"{report.Saved.Count} YMAP enregistré(s) dans le projet « {Project!.Name} »", true, DateTime.Now);
        }
        else
            LastSave = ($"Échec de l'enregistrement : {string.Join(" ; ", report.Errors)}", false, DateTime.Now);
    }

    /// <summary>The open project, null if none: changes can then be made, but saving asks for a project first.</summary>
    public MapProject? Project { get; private set; }

    /// <summary>Folder of the saved map files: the open project's "stream" folder.</summary>
    public string? SaveFolder => Project?.StreamFolder;

    /// <summary>Why the last project could not be opened (shown in the status panel), null if it was.</summary>
    public string? ProjectError { get; private set; }

    private const int MaxRecentProjects = 8;

    /// <summary>Folder proposed for new projects.</summary>
    public string ProjectsFolder => Settings.ProjectsFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NodeRunner");

    /// <summary>At start-up: turns the save folder of earlier versions into a project, then opens the last project.</summary>
    public void RestoreProject()
    {
        MigrateSaveFolder();
        if (Settings.ProjectFolder is not { } folder)
            return;
        try
        {
            Project = MapProject.Load(folder);
            ApplyProjectMap(Project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ProjectError = $"Projet introuvable ou illisible ({folder}) : {ex.Message}";
            Settings.ProjectFolder = null;
            SaveSettings();
        }
    }

    /// <summary>The save folder of the versions before projects becomes the first project (its .ymap files go to "stream").</summary>
    private void MigrateSaveFolder()
    {
        if (Settings.SaveFolder is not { } folder)
            return;
        if (Settings.ProjectFolder is null && Directory.Exists(folder))
        {
            try
            {
                MapProject project;
                if (MapProject.Exists(folder))
                    project = MapProject.Load(folder);
                else
                {
                    project = MapProject.Create(folder, Path.GetFileName(Path.GetFullPath(folder)), Settings.MapVersion, Settings.LastDlcPack);
                    if (Settings.ExportResourceName is { } name && FiveMResource.IsValidName(name))
                        project.ResourceName = name;
                    project.ExportFolder = Settings.ExportFolder;
                    project.Save();
                }
                Settings.ProjectFolder = project.Folder;
                AddRecentProject(project.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                return; // Tried again at the next start.
            }
        }
        Settings.SaveFolder = null;
        SaveSettings();
    }

    /// <summary>Opens the project of a folder (holding projet.json) and loads its map. Returns the error, null if opened.</summary>
    public string? OpenProject(string folder)
    {
        MapProject project;
        try
        {
            project = MapProject.Load(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return ex.Message;
        }
        UseProject(project);
        return null;
    }

    /// <summary>
    /// Creates a project in a folder and opens it. Without an open project, the changes in progress are kept and go
    /// into the new project; otherwise, or when the folder already holds map files, the map is loaded again
    /// (without the previous project's files, with the folder's ones).
    /// </summary>
    public string? CreateProject(string folder, string name)
    {
        bool reload = Project is not null || MapProject.HasMapFiles(folder);
        MapProject project;
        try
        {
            project = MapProject.Create(folder, name, Settings.MapVersion, Settings.LastDlcPack);
            project.ExportFolder = Settings.ExportFolder;
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        UseProject(project, reload);
        return null;
    }

    /// <summary>Closes the project: the game's map, without any saved file.</summary>
    public void CloseProject()
    {
        if (Project is null)
            return;
        Project = null;
        Settings.ProjectFolder = null;
        SaveSettings();
        LastSave = null;
        World.RequestMapReload();
    }

    private void UseProject(MapProject project, bool reload = true)
    {
        Project = project;
        ProjectError = null;
        LastSave = null;
        Settings.ProjectFolder = project.Folder;
        AddRecentProject(project.Folder);
        ApplyProjectMap(project);
        SaveSettings();
        if (reload)
            World.RequestMapReload();
    }

    /// <summary>The project's map version and DLC become the ones shown.</summary>
    private void ApplyProjectMap(MapProject project)
    {
        Settings.MapVersion = project.MapVersion;
        Settings.LastDlcPack = project.LastDlcPack;
    }

    private void AddRecentProject(string folder)
    {
        Settings.RecentProjects.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        Settings.RecentProjects.Insert(0, folder);
        if (Settings.RecentProjects.Count > MaxRecentProjects)
            Settings.RecentProjects.RemoveRange(MaxRecentProjects, Settings.RecentProjects.Count - MaxRecentProjects);
    }

    public void ForgetRecentProject(string folder)
    {
        if (Settings.RecentProjects.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) > 0)
            SaveSettings();
    }

    /// <summary>Map version and "map after this DLC" (toolbar): changing them reloads the map; kept by the open project.</summary>
    public void SetMapSelection(MapVersion version, string? lastPack)
    {
        if (Settings.MapVersion == version && Settings.LastDlcPack == lastPack)
            return;
        Settings.MapVersion = version;
        Settings.LastDlcPack = lastPack;
        SaveSettings();
        if (Project is { } project)
        {
            project.MapVersion = version;
            project.LastDlcPack = lastPack;
            SaveProject();
        }
    }

    /// <summary>Name of the FiveM resource exported: the project's.</summary>
    public string ExportResourceName
    {
        get => Project?.ResourceName ?? Settings.ExportResourceName ?? FiveMResource.DefaultName;
        set
        {
            Settings.ExportResourceName = value;
            SaveSettings();
            if (Project is { } project)
            {
                project.ResourceName = value;
                SaveProject();
            }
        }
    }

    /// <summary>Folder where the FiveM resource is exported: the project's, else the last one used.</summary>
    public string? ExportFolder
    {
        get => Project?.ExportFolder ?? Settings.ExportFolder;
        set
        {
            Settings.ExportFolder = value;
            SaveSettings();
            if (Project is { } project)
            {
                project.ExportFolder = value;
                SaveProject();
            }
        }
    }

    private void SaveProject()
    {
        try
        {
            Project?.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProjectError = $"projet.json non enregistré : {ex.Message}";
        }
    }

    public bool IsLayerVisible(string layer) =>
        IsHiddenByDefault(layer) ? Settings.ShownLayers.Contains(layer) : !Settings.HiddenLayers.Contains(layer);

    /// <summary>Layers that are off until the user turns them on (heavy, or covering the map).</summary>
    private static bool IsHiddenByDefault(string layer) =>
        layer.StartsWith(CollisionLayer.LayerPrefix, StringComparison.Ordinal) || layer.StartsWith(RoomLayer.LayerPrefix, StringComparison.Ordinal);

    public void SetLayerVisible(string layer, bool visible)
    {
        bool changed = IsHiddenByDefault(layer)
            ? visible ? Settings.ShownLayers.Add(layer) : Settings.ShownLayers.Remove(layer)
            : visible ? Settings.HiddenLayers.Remove(layer) : Settings.HiddenLayers.Add(layer);
        if (changed)
            SaveSettings();
    }

    public AppState(AppPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
        World = new WorldLoader { UserNamesFile = Path.Combine(paths.DataDirectory, UserNamesFileName) };
    }

    /// <summary>Optional names file of the user, in the data folder (one name per line, as CodeWalker's strings.txt).</summary>
    public const string UserNamesFileName = "strings.txt";

    /// <summary>Uses the manual folder from settings if valid, otherwise automatic detection.</summary>
    public void RedetectGame(bool clearManualChoice = false)
    {
        if (clearManualChoice && Settings.GameFolder is not null)
        {
            Settings.GameFolder = null;
            SaveSettings();
        }

        var found = GameInstallLocator.CreateDefault().FindAll();

        var manual = GameFolderValidator.TryCreate(Settings.GameFolder, "Choix manuel");
        Game = manual
               ?? found.FirstOrDefault(i => i.Edition == GameEdition.Legacy)
               ?? found.FirstOrDefault();

        OtherInstallations = found
            .Where(i => Game is null || !string.Equals(i.FolderPath, Game.FolderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Validates a folder picked by the user. Returns false if it is not a game folder.</summary>
    public bool TrySetManualGameFolder(string folder)
    {
        var install = GameFolderValidator.TryCreate(folder, "Choix manuel");
        if (install is null)
            return false;

        Settings.GameFolder = install.FolderPath;
        SaveSettings();
        RedetectGame();
        return true;
    }

    public void UseInstallation(GameInstallation installation) => TrySetManualGameFolder(installation.FolderPath);

    public void SaveSettings()
    {
        try
        {
            Settings.Save(Paths.SettingsFile);
        }
        catch (Exception)
        {
            // Not fatal: settings simply won't persist this time.
        }
    }
}

/// <summary>Tools of the 3D view (floating bar, keys 1 to 4).</summary>
public enum TransformTool
{
    Select,
    Move,
    Rotate,
    Scale,
}
