using NodeRunner.App.Configuration;
using NodeRunner.App.World;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

// Application-wide state shared by the window and the UI.
internal sealed class AppState
{
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }

    public GameInstallation? Game { get; private set; }

    public IReadOnlyList<GameInstallation> OtherInstallations { get; private set; } = [];

    public string AdapterName { get; set; } = "?";

    public Camera Camera { get; } = new();

    public WorldLoader World { get; }

    // The main selected object (the last one clicked), null if none.
    public MapEntity? Selection => SelectedEntities is [.., var last] ? last : null;

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

    private static bool IsGone(MapEntity entity) => entity is { IsDeleted: true } or { Interior.IsDeleted: true };

    private readonly List<MapEntity> _selection = [];
    private MapLibrary? _selectionLibrary;

    // A node of the road network selected in the 3D view (an object selection clears it). Forgotten when the network is
    // read again (map loading).
    public Data.Paths.PathNode? SelectedPathNode
    {
        get => _selectedNodeNetwork == World.PathNetwork ? _selectedNode : null;
        set
        {
            _selectedNode = value;
            _selectedNodeNetwork = World.PathNetwork;
        }
    }

    private Data.Paths.PathNode? _selectedNode;
    private Data.Paths.PathNetwork? _selectedNodeNetwork;

    public void Select(MapEntity? entity)
    {
        _selection.Clear();
        SelectedPathNode = null;
        if (entity is not null)
            _selection.Add(entity);
        _selectionLibrary = World.Maps;
    }

    public void SelectAll(IReadOnlyList<MapEntity> entities)
    {
        Select(null);
        _selection.AddRange(entities.Distinct());
    }

    // The selected objects that can be moved or deleted (not those defined by an interior's archetype), the main one
    // last.
    public IReadOnlyList<MapEntity> EditableSelection => SelectedEntities.Where(e => e.CanEdit).ToList();

    public void ToggleSelect(MapEntity entity)
    {
        if (_selectionLibrary != World.Maps)
            Select(null);
        if (!_selection.Remove(entity))
            _selection.Add(entity);
    }

    public bool IsSelected(MapEntity entity) => SelectedEntities.Contains(entity);

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

    // Collision pieces the export will remove (deleted buildings), drawn in red in the 3D view while the export
    // window shows them; null otherwise.
    public IReadOnlyList<CollisionFileCut>? CollisionPreview { get; set; }

    public CollisionFileCut? CollisionPreviewHovered { get; set; }

    // Occluders the export will remove (deleted buildings), drawn in blue like CollisionPreview; null otherwise.
    public IReadOnlyList<OccluderFileCut>? OcclusionPreview { get; set; }

    public OccluderFileCut? OcclusionPreviewHovered { get; set; }

    public DeletionPlan? PendingDeletion { get; private set; }

    public void RequestDelete()
    {
        if (EditableSelection is { Count: > 0 } targets)
            PendingDeletion = DeletionPlan.For(targets);
    }

    public void CancelDelete() => PendingDeletion = null;

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

    // Object type to add, waiting for the main form to find the ground at the centre of the 3D view; 0 if none.
    public uint PlaceRequest { get; set; }

    // Result of the last editing command (add an object, put it on the ground...), shown a few seconds in the status
    // bar.
    public (string Text, bool IsWarning, DateTime Time)? Notice { get; private set; }

    public void Notify(string text, bool warning = false) => Notice = (text, warning, DateTime.Now);

    // Adds an object of type archetypeHash standing on ground, in the project's own map, selects it and takes the
    // "move" tool.
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

    public CollisionQuery CollisionQuery { get; } = new();

    // Position that puts the object on the collision below it, or null when the collisions are not loaded yet or
    // nothing lies below.
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

    // Height of the collision below a point (rays start a little above it), or null when the collisions are not loaded
    // yet or nothing lies below.
    public float? GroundHeightAt(System.Numerics.Vector3 point)
    {
        if (World.Collisions is null)
            return null;
        float from = point.Z + GroundSnap.MaxRise;
        var collisions = CollisionQuery.CollisionIn(World, point with { Z = -1000f }, point with { Z = from });
        return GroundSnap.HighestBelow(collisions, new System.Numerics.Vector2(point.X, point.Y), from);
    }

    // Moves each selected object up or down so that it rests on the collision below it.
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

    public string FormatPosition(System.Numerics.Vector3 position, float? heading = null) =>
        PositionText.Position(position, heading, CopyFormat);

    public string FormatSelection() => PositionText.List(
        SelectedEntities.Select(e => new PositionItem(World.Names?.Describe(e.ArchetypeHash) ?? $"0x{e.ArchetypeHash:X8}", e.Position, Ui.FiveMFormat.Heading(e))),
        CopyFormat);

    public IReadOnlyList<CopiedObject> Copied { get; private set; } = [];

    // Ctrl+C: remembers the selected objects (interiors excepted: they cannot be added yet).
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

    public bool PasteRequest { get; set; }

    // Adds the copied objects to the project's own map, the base of the group on ground; selects them and takes the
    // "move" tool.
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

    // Takes the changes of otherFile (the same map file saved by another project or shipped by another resource) into
    // the loaded map, in one undo step: what only the other version changed is applied, conflicts keep ours.
    // Returns the result and the conflicts, described for the interface.
    public (string Text, bool Success, List<string> Conflicts) TakeChangesFrom(string otherFile, string owner)
    {
        string fileName = Path.GetFileName(otherFile);
        if (World.Maps is not { } library || World.Content is not { } content)
            return ("La carte n'est pas chargée.", false, []);
        var map = library.Maps.FirstOrDefault(m => !m.IsCustom && m.FilePath is { } p
            && string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
        if (map?.FilePath is not { } gamePath || content.ReadFile(gamePath) is not { } gameBytes)
            return ($"{fileName} n'est pas un YMAP de la carte chargée.", false, []);

        MergePlan plan;
        try
        {
            var reader = new YmapReader();
            var game = reader.Read(gameBytes);
            var theirs = reader.Read(ResourceFile.Read(File.ReadAllBytes(otherFile)).Content);
            plan = MapMerge.Plan(game.Entities, map.Entities, theirs.Entities);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ($"Lecture impossible de {otherFile} : {ex.Message}", false, []);
        }

        var names = World.Names;
        var conflicts = plan.Conflicts
            .Select(c => $"{names?.Describe(c.ArchetypeHash) ?? $"0x{c.ArchetypeHash:X8}"} ({c.Position.X:0.0}, {c.Position.Y:0.0}, {c.Position.Z:0.0}) : {c.Reason}")
            .ToList();
        if (plan.IsEmpty)
            return (plan.Conflicts.Count == 0 ? $"Rien à reprendre : {fileName} a déjà tous les changements de « {owner} »."
                : $"Rien à reprendre de « {owner} » sans conflit.", plan.Conflicts.Count == 0, conflicts);

        MapData? addTo = null;
        var added = new List<MapEntity>();
        if (plan.Additions.Count > 0)
        {
            if ((addTo = ProjectMap(library)) is null)
                return ("Ouvre ou crée d'abord un projet : les objets ajoutés par l'autre version vont dans le YMAP du projet.", false, conflicts);
            foreach (var (hash, placement) in plan.Additions)
            {
                var entity = Adding.NewEntity(hash, library.GetArchetype(hash), placement.Position, placement.Orientation);
                entity.SetPlacement(placement.Position, placement.Orientation, placement.Scale);
                added.Add(entity);
            }
        }
        History.Add(MergeEdit.Apply(library, plan, addTo, added, $"Reprendre les changements de « {owner} » ({fileName})"));

        var parts = new List<string>();
        if (plan.Moves.Count > 0)
            parts.Add($"{plan.Moves.Count} déplacé(s)");
        if (plan.Deletions.Count > 0)
            parts.Add($"{plan.Deletions.Count} supprimé(s)");
        if (plan.Additions.Count > 0)
            parts.Add($"{plan.Additions.Count} ajouté(s) au YMAP du projet");
        string text = $"Changements de « {owner} » repris dans {fileName} : {string.Join(", ", parts)}."
            + (conflicts.Count > 0 ? $" {conflicts.Count} conflit(s) : ta version est gardée." : "");
        Notify(text, conflicts.Count > 0);
        return (text, true, conflicts);
    }

    // The project's own map, where added objects go (created if needed); null, with a message, without a project.
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

    // Moves the camera in front of an object, at a distance suited to its size, keeping the viewing direction.
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

    public void FocusOn(System.Numerics.Vector3 center, float radius)
    {
        float distance = Math.Clamp(radius * 2.5f, 6f, 3000f);
        Camera.Position = center - Camera.Forward * distance;
        Camera.LookAt(center);
    }

    public (System.Numerics.Vector3 Position, System.Numerics.Vector2? Facing)? Marker { get; set; }

    // Moves the camera above and behind a point (e.g. a position pasted from a FiveM script), looking down at it:
    // from behind facing if given (the heading of a vector4), otherwise in the current direction.
    public void LookFromAbove(System.Numerics.Vector3 target, System.Numerics.Vector2? facing = null)
    {
        var forward = Camera.Forward;
        var flat = facing ?? new System.Numerics.Vector2(forward.X, forward.Y);
        flat = flat.LengthSquared() > 1e-6f ? System.Numerics.Vector2.Normalize(flat) : System.Numerics.Vector2.UnitY;
        Camera.Position = target + new System.Numerics.Vector3(-flat.X * 18f, -flat.Y * 18f, 12f);
        Camera.LookAt(target);
    }

    // Changes made to the map, for undo / redo.
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

    public void Undo() => SelectAfterUndo(History.Undo());

    public void Redo() => SelectAfterUndo(History.Redo());

    private void SelectAfterUndo(IEdit? edit)
    {
        if (edit is PathNodeMoveEdit nodeEdit)
        {
            Select(null);
            SelectedPathNode = nodeEdit.Node;
        }
        else if (edit is { Selection.Count: > 0 })
            SelectAll(edit.Selection);
    }

    // Map files to write when saving: those changed in this session (even if the change was undone since, so that a
    // file saved earlier is put back) and those saved before.
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

    public ModelViewer Viewer { get; } = new();

    // Map files of a FiveM export: those to save, plus the saved versions taken back when the map was loaded
    // (modified in an earlier session).
    public IReadOnlyList<MapData> MapsToExport =>
        MapsToSave.Concat(World.Maps?.Maps.Where(m => m.IsSaved) ?? []).Distinct().ToList();

    private readonly HashSet<MapData> _savedMaps = [];
    private MapLibrary? _savedMapsLibrary;

    public (string Text, bool Success, DateTime Time)? LastSave { get; private set; }

    // Road network files to write when saving (and exporting): areas changed since the map was loaded, and those taken
    // back from the project.
    public IReadOnlyList<int> PathAreasToSave => World.PathNetwork is { } network
        ? network.ChangedAreas.Concat(network.SavedAreas).Distinct().Order().ToList()
        : [];

    public int PendingSaveCount => MapsToSave.Count + PathAreasToSave.Count;

    // "2 YMAP", "2 YMAP, 1 YND".
    public string PendingSaveText => FilesText(MapsToSave.Count, PathAreasToSave.Count);

    public static string FilesText(int maps, int paths) =>
        paths == 0 ? $"{maps} YMAP" : maps == 0 ? $"{paths} YND" : $"{maps} YMAP, {paths} YND";

    public void SaveMaps()
    {
        if (SaveFolder is not { } folder)
            return;
        var maps = MapsToSave;
        var paths = PathAreasToSave;
        if (maps.Count == 0 && paths.Count == 0)
            return;
        var report = maps.Count > 0 ? MapFileSaver.Save(World, maps, folder) : new MapFileSaver.Report([], []);
        foreach (var map in maps.Where(m => m.FilePath is { } p && report.Saved.Contains(Path.GetFileName(p))))
            _savedMaps.Add(map);
        PathFileSaver.Save(World, paths, folder, report);
        if (report.Errors.Count == 0)
        {
            History.MarkSaved();
            int pathFiles = report.Saved.Count(f => f.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase));
            LastSave = ($"{FilesText(report.Saved.Count - pathFiles, pathFiles)} enregistré(s) dans le projet « {Project!.Name} »", true, DateTime.Now);
        }
        else
            LastSave = ($"Échec de l'enregistrement : {string.Join(" ; ", report.Errors)}", false, DateTime.Now);
    }

    public MapProject? Project { get; private set; }

    public string? SaveFolder => Project?.StreamFolder;

    public string? ProjectError { get; private set; }

    private const int MaxRecentProjects = 8;

    public string ProjectsFolder => Settings.ProjectsFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NodeRunner");

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

    // The save folder of the versions before projects becomes the first project (its .ymap files go to "stream").
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

    // Opens the project of a folder (holding projet.json) and loads its map.
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

    // Creates a project in a folder and opens it.
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

    // Map version and "map after this DLC" (toolbar): changing them reloads the map; kept by the open project.
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

    // Optional names file of the user, in the data folder (one name per line, as CodeWalker's strings.txt).
    public const string UserNamesFileName = "strings.txt";

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

// Tools of the 3D view (floating bar, keys 1 to 4).
public enum TransformTool
{
    Select,
    Move,
    Rotate,
    Scale,
}
