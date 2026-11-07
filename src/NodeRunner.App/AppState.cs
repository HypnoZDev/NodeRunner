using System.Numerics;
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

    // The window shows the Windows 11 backdrop (Mica) through the interface (see Settings.Transparency).
    public bool TransparentWindow { get; set; }

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

    // A box or a mesh of an occluder map file selected in the 3D view (an object selection clears it). Forgotten when
    // the map is loaded again.
    public OccluderSelection? SelectedOccluder
    {
        get => _selectedOccluderLibrary == World.Maps && _selectedOccluder is { } o && !o.File.IsDeleted(o.Kind, o.Index) ? o : null;
        set
        {
            _selectedOccluder = value;
            _selectedOccluderLibrary = World.Maps;
        }
    }

    private OccluderSelection? _selectedOccluder;
    private MapLibrary? _selectedOccluderLibrary;

    // A room of an interior selected in the 3D view or in the interior's properties (an object selection clears it).
    public RoomSelection? SelectedRoom
    {
        get => _selectedRoomLibrary == World.Maps && _selectedRoom is { Interior.IsDeleted: false } room ? room : null;
        private set
        {
            _selectedRoom = value;
            _selectedRoomLibrary = World.Maps;
        }
    }

    private RoomSelection? _selectedRoom;
    private MapLibrary? _selectedRoomLibrary;

    public void SelectRoom(RoomSelection room)
    {
        Select(null);
        SelectedRoom = room;
    }

    // A portal of an interior, selected the same way as a room.
    public RoomSelection? SelectedPortal
    {
        get => _selectedPortalLibrary == World.Maps && _selectedPortal is { Interior.IsDeleted: false } portal ? portal : null;
        private set
        {
            _selectedPortal = value;
            _selectedPortalLibrary = World.Maps;
        }
    }

    private RoomSelection? _selectedPortal;
    private MapLibrary? _selectedPortalLibrary;

    public void SelectPortal(RoomSelection portal)
    {
        Select(null);
        SelectedPortal = portal;
    }

    public void Select(MapEntity? entity)
    {
        _selection.Clear();
        SelectedPathNode = null;
        SelectedOccluder = null;
        SelectedRoom = null;
        SelectedPortal = null;
        if (entity is not null)
            _selection.Add(entity);
        _selectionLibrary = World.Maps;
    }

    public void SelectAll(IReadOnlyList<MapEntity> entities)
    {
        Select(null);
        _selection.AddRange(entities.Distinct());
    }

    // The selected objects that can be moved, the main one last.
    public IReadOnlyList<MapEntity> EditableSelection => SelectedEntities.Where(e => e.CanEdit).ToList();

    // The selected objects that can be deleted (not those defined by an interior's archetype), the main one last.
    public IReadOnlyList<MapEntity> DeletableSelection => SelectedEntities.Where(e => e.CanDelete).ToList();

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
        if (DeletableSelection is { Count: > 0 } targets)
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
        if (AddToInterior(library, [entity], $"Ajouter {name}") is not { } inside)
        {
            History.Add(AddEdit.Apply(library, map, entity, $"Ajouter {name}"));
            inside = [entity];
        }
        SelectAll(inside);
        if (Tool == TransformTool.Select)
            Tool = TransformTool.Move;
    }

    // Moves the selected objects of the main selection's interior into its entity set setIndex (-1 = out of any set),
    // in every copy of the interior; the set is shown so that they stay in sight.
    public void MoveSelectionToEntitySet(int setIndex)
    {
        if (World.Maps is not { } library || Selection?.Interior is not { } instance || setIndex >= instance.EntitySets.Count)
            return;
        var target = setIndex >= 0 ? instance.EntitySets[setIndex] : null;
        var moving = SelectedEntities.Where(e => e.Interior == instance && e.Definition is not null && e.EntitySet != target).ToList();
        if (moving.Count == 0)
            return;
        string setName = target is null ? "" : World.Names?.Describe(target.NameHash) ?? $"0x{target.NameHash:X8}";
        string what = moving.Count == 1 ? World.Names?.Describe(moving[0].ArchetypeHash) ?? "l'objet" : $"{moving.Count} objets";
        var edit = EntitySetMoveEdit.Apply(library, moving, setIndex, target is null ? $"Sortir {what} de son entity set" : $"Ranger {what} dans {setName}");
        History.Add(edit);
        if (target is not null)
            library.SetEntitySetActive(target, true);
        SelectAll(edit.Selection);
        Notify(target is null
            ? $"{what} : sorti(s) de l'entity set, toujours affiché(s), dans toutes les copies de l'intérieur (enregistré dans son .ytyp)."
            : $"{what} : rangé(s) dans l'entity set {setName}, dans toutes les copies de l'intérieur (enregistré dans son .ytyp).");
    }

    // Puts the selected objects of the main selection's interior in a room (0 = limbo), or with AutomaticRoom each in
    // the smallest room around it, in every copy of the interior.
    public void SetSelectionRoom(int room)
    {
        if (World.Maps is not { } library || Selection?.Interior is not { } instance
            || library.GetArchetype(instance.ArchetypeHash) is not { Kind: Data.Maps.ArchetypeKind.Interior } archetype
            || room < AutomaticRoom || room >= archetype.InteriorRooms.Count)
            return;
        var objects = SelectedEntities.Where(e => e.Interior == instance && e.Definition is not null).ToList();
        var rooms = objects.Select(e => room != AutomaticRoom ? room
            : Math.Max(0, Adding.RoomAt(instance, archetype, e.Position + new System.Numerics.Vector3(0, 0, RoomProbeHeight)))).ToList();
        var changing = objects.Select((e, i) => (Entity: e, Room: rooms[i])).Where(c => c.Entity.InteriorRoom != c.Room).ToList();
        if (changing.Count == 0)
        {
            Notify(objects.Count == 1 ? "L'objet est déjà dans cette salle." : "Les objets sont déjà dans ces salles.");
            return;
        }
        string what = changing.Count == 1 ? World.Names?.Describe(changing[0].Entity.ArchetypeHash) ?? "l'objet" : $"{changing.Count} objets";
        string RoomLabel(int r) => r == 0 ? "limbo" : archetype.InteriorRooms[r].Name is { Length: > 0 } name ? name : $"salle {r}";
        string target = changing.Select(c => c.Room).Distinct().Count() == 1 ? RoomLabel(changing[0].Room) : "leurs salles";
        var edit = EntityRoomEdit.Apply(library, [.. changing.Select(c => c.Entity)], [.. changing.Select(c => c.Room)], $"Mettre {what} dans {target}");
        History.Add(edit);
        SelectAll(edit.Selection);
        Notify($"{what} : dans {target}, dans toutes les copies de l'intérieur (enregistré dans son .ytyp au Ctrl+S).");
    }

    public const int AutomaticRoom = -2;

    // Lift of the point tested against the rooms: an object standing on a floor is just above the room's bottom.
    private const float RoomProbeHeight = 0.3f;

    // New objects placed in a room of an interior go into the interior's archetype (.ytyp), as the game draws the
    // objects of a map file (.ymap) badly inside interiors; null when the group's first object is in no room.
    private IReadOnlyList<MapEntity>? AddToInterior(MapLibrary library, IReadOnlyList<MapEntity> entities, string description)
    {
        var probe = entities[0].Position + new System.Numerics.Vector3(0, 0, RoomProbeHeight);
        foreach (var instance in library.EntitiesAround(new System.Numerics.Vector2(probe.X, probe.Y), InteriorSearchRadius))
        {
            if (!instance.IsInterior || library.GetArchetype(instance.ArchetypeHash) is not { Kind: Data.Maps.ArchetypeKind.Interior } archetype
                || Adding.RoomAt(instance, archetype, probe) is not (> 0 and var room))
                continue;
            var definitions = entities.Select(e =>
            {
                int own = Adding.RoomAt(instance, archetype, e.Position + new System.Numerics.Vector3(0, 0, RoomProbeHeight));
                return Adding.InteriorDefinition(e, instance, own > 0 ? own : room);
            }).ToList();
            var edit = InteriorAddEdit.Apply(library, instance, definitions, description);
            History.Add(edit);
            string roomName = archetype.InteriorRooms[room].Name;
            Notify($"Ajouté à l'intérieur {World.Names?.Describe(instance.ArchetypeHash) ?? ""} (salle {roomName}) : enregistré dans son .ytyp, dans toutes ses copies.");
            return edit.Selection;
        }
        return null;
    }

    // Interiors are found by their origin: the biggest ones (tunnels) span a few hundred metres.
    private const float InteriorSearchRadius = 400f;

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
            box.Min with { Z = -1000f }, box.Max with { Z = GroundSnap.StartHeight(box.Min.Z, box.Max.Z) }, CollisionEdits);
        return GroundSnap.Snap(entity, archetype, collisions);
    }

    // Height of the collision below a point (rays start a little above it), or null when the collisions are not loaded
    // yet or nothing lies below.
    public float? GroundHeightAt(System.Numerics.Vector3 point)
    {
        if (World.Collisions is null)
            return null;
        float from = point.Z + GroundSnap.MaxRise;
        var collisions = CollisionQuery.CollisionIn(World, point with { Z = -1000f }, point with { Z = from }, CollisionEdits);
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
        if (AddToInterior(library, pasted, $"Coller {name}") is { } inside)
            pasted = inside;
        else
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

    public void Undo() => UndoOrRedo(undo: true);

    public void Redo() => UndoOrRedo(undo: false);

    // In an online session, a guest asks the host, which undoes the session's last change for everyone.
    private void UndoOrRedo(bool undo)
    {
        if (Online.IsGuest)
        {
            if (!(undo ? History.CanUndo : History.CanRedo))
                return;
            if (!Online.Session!.RequestUndo(undo))
                Notify("Patientez : la session se synchronise.", warning: true);
            return;
        }
        SelectAfterUndo(undo ? History.Undo() : History.Redo(), undone: undo);
    }

    internal void SelectAfterUndo(IEdit? edit, bool undone)
    {
        if (edit is PathNodeMoveEdit nodeEdit)
        {
            Select(null);
            SelectedPathNode = nodeEdit.Node;
        }
        else if (edit is OccluderBoxEdit boxEdit)
        {
            Select(null);
            SelectedOccluder = new OccluderSelection(boxEdit.File, OcclusionLayerPart.Boxes, boxEdit.Index);
        }
        else if (edit is OccluderPresenceEdit presenceEdit)
        {
            Select(null);
            SelectedOccluder = presenceEdit.Occluders.FirstOrDefault(o => !o.File.IsDeleted(o.Kind, o.Index));
        }
        else if (edit is RoomEdit roomEdit)
            SelectRoom(new RoomSelection(roomEdit.Interior, roomEdit.Index));
        else if (edit is PortalEdit portalEdit)
            SelectPortal(new RoomSelection(portalEdit.Interior, portalEdit.Index));
        else if (edit is InteriorLayoutEdit layoutEdit)
        {
            // The room or portal is there again: selected; gone: its interior is.
            var place = new RoomSelection(layoutEdit.Interior, layoutEdit.Index);
            if (layoutEdit.Added == undone)
                Select(layoutEdit.Interior);
            else if (layoutEdit.IsPortal)
                SelectPortal(place);
            else
                SelectRoom(place);
        }
        else if (edit is CollisionEdit collisionEdit)
        {
            Tool = TransformTool.Collision;
            SelectCollision(collisionEdit.Polygons.Where(p => !CollisionEdits.IsRemoved(p.Path, p.Polygon)), CollisionSelectMode.Replace);
        }
        else if (edit is { Selection.Count: > 0 })
            SelectAll(edit.Selection);
    }

    // --- Occluders ---

    public string OccluderFileName => $"{Project?.ResourceName ?? FiveMResource.DefaultName}_occl.ymap";

    // A new 10 m box occluder 25 m in front of the camera, standing on the ground there.
    public void CreateOccluderBox()
    {
        if (OccluderMaps.ProjectFile(World, OccluderFileName) is not { } file)
            return;
        var forward = Camera.Forward with { Z = 0 };
        forward = forward.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(forward) : System.Numerics.Vector3.UnitY;
        var point = Camera.Position + forward * 25f;
        float ground = GroundHeightAt(point) ?? Camera.Position.Z - 10f;
        var size = new System.Numerics.Vector3(10f);
        var box = new OccluderBox(0, point with { Z = ground + size.Z / 2 }, size, MathF.Atan2(forward.Y, forward.X)).Quantized();
        AddOccluderBoxes(file, [box], "Nouvelle boîte d'occlusion");
    }

    // Box occluders inside each selected object (OccluderFit): inside the walls of its collision, one per closed
    // building, or its model's box a little smaller where there is no collision.
    public void CreateOccluderBoxesFromSelection()
    {
        if (World.Maps is not { } library || OccluderMaps.ProjectFile(World, OccluderFileName) is not { } file)
            return;
        var boxes = new List<OccluderBox>();
        int fromModel = 0, open = 0, skipped = 0, mlo = 0, mloEmpty = 0;
        // An interior, or an object inside one, gets boxes in the outer walls of the interior: a box filling it would
        // hide what is in its rooms.
        foreach (var interior in SelectedEntities.Select(e => e.IsInterior ? e : e.Interior).OfType<MapEntity>().Distinct())
        {
            if (library.GetArchetype(interior.ArchetypeHash) is not { } archetype)
                continue;
            var walls = MloOccluders.Fit(interior, archetype, InteriorCollision(interior), World.Collisions?.Materials);
            boxes.AddRange(walls);
            if (walls.Count > 0)
                mlo++;
            else
                mloEmpty++;
        }
        foreach (var entity in SelectedEntities.Where(e => !e.IsInterior && e.Interior is null))
        {
            switch (FitOccluderBoxes(entity, library.GetArchetype(entity.ArchetypeHash)))
            {
                case { Source: OccluderFitSource.NoClosedSpace }:
                    open++;
                    break;
                case { Boxes.Count: > 0 } fit:
                    boxes.AddRange(fit.Boxes);
                    if (fit.Source == OccluderFitSource.Model)
                        fromModel++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        var notes = new List<string>();
        if (mloEmpty > 0)
            notes.Add($"{mloEmpty} intérieur(s) sans mur extérieur plein");
        if (open > 0)
            notes.Add($"{open} objet(s) qui ne sont pas des bâtiments fermés");
        if (skipped > 0)
            notes.Add($"{skipped} objet(s) sans volume ou trop plat(s)");
        if (fromModel > 0)
            notes.Add($"{fromModel} objet(s) sans collision ni modèle lisible : boîte du modèle");
        if (boxes.Count == 0)
        {
            Notify($"aucune boîte créée : {string.Join(", ", notes)}", warning: true);
            return;
        }
        AddOccluderBoxes(file, boxes, mlo > 0 ? $"{boxes.Count} boîtes d'occlusion dans les murs de l'intérieur"
            : boxes.Count == 1 ? "Boîte d'occlusion sur l'objet" : $"{boxes.Count} boîtes d'occlusion sur les objets");
        if (notes.Count > 0)
            Notify($"{boxes.Count} boîte(s) créée(s) ; {string.Join(", ", notes)}", warning: true);
    }

    // The interior's own collision, in its local space.
    private Data.Collisions.CollisionMesh? InteriorCollision(MapEntity interior)
    {
        if (World.Collisions is not { } catalog || catalog.FindInteriorFile(interior.ArchetypeHash) is not { } path)
            return null;
        try
        {
            return catalog.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private OccluderFitResult? FitOccluderBoxes(MapEntity entity, Archetype? archetype)
    {
        if (GroundSnap.WorldBox(entity, archetype) is not var (min, max) || World.Maps is not { } library)
            return OccluderFit.Fit(entity, archetype);
        var collisions = World.Collisions is null ? null : CollisionQuery.CollisionIn(World, min, max, CollisionEdits);
        // Interiors (MLO) placed there stay free of occluders.
        var interiors = new List<(System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        var center = new System.Numerics.Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2);
        float radius = System.Numerics.Vector2.Distance(center, new System.Numerics.Vector2(max.X, max.Y)) + 200f;
        foreach (var interior in library.EntitiesAround(center, radius))
        {
            if (interior.IsInterior && GroundSnap.WorldBox(interior, library.GetArchetype(interior.ArchetypeHash)) is { } box
                && box.Min.X <= max.X && box.Max.X >= min.X && box.Min.Y <= max.Y && box.Max.Y >= min.Y && box.Min.Z <= max.Z && box.Max.Z >= min.Z)
                interiors.Add(box);
        }
        return OccluderFit.Fit(entity, archetype, collisions, ModelTriangles(entity, archetype), interiors);
    }

    // The triangles of an object's model in world positions (three points each), or null when it cannot be read.
    private List<System.Numerics.Vector3>? ModelTriangles(MapEntity entity, Archetype? archetype)
    {
        if (archetype is null || World.Models?.Locate(archetype) is not { } location || World.ModelReader is not { } reader)
            return null;
        try
        {
            if (reader.ReadModels(location.Path).GetValueOrDefault(location.ModelHash) is not { } model)
                return null;
            var transform = entity.Transform;
            var triangles = new List<System.Numerics.Vector3>(model.TriangleCount * 3);
            foreach (var mesh in model.Meshes)
            {
                foreach (ushort index in mesh.Indices)
                    triangles.Add(System.Numerics.Vector3.Transform(mesh.Positions[index], transform));
            }
            return triangles;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private void AddOccluderBoxes(OccluderMapFile file, IReadOnlyList<OccluderBox> boxes, string description)
    {
        var edit = OccluderAddEdit.Apply(file, boxes, description);
        var added = edit.Added;
        History.Add(edit);
        SetLayerVisible(OcclusionLayer.LayerName(OcclusionLayerPart.Boxes), true);
        Select(null);
        SelectedOccluder = added[^1];
        Notify(added.Count == 1 ? $"boîte d'occlusion ajoutée à {file.FileName}" : $"{added.Count} boîtes d'occlusion ajoutées à {file.FileName}");
    }

    // Selects an occluder, shows its layer and moves the camera in front of it.
    public void ShowOccluder(OccluderSelection occluder)
    {
        if (occluder.File.IsDeleted(occluder.Kind, occluder.Index))
            return;
        Select(null);
        SelectedOccluder = occluder;
        SetLayerVisible(OcclusionLayer.LayerName(occluder.Kind), true);
        var (min, max) = occluder.Bounds();
        FocusOn((min + max) / 2, System.Numerics.Vector3.Distance(min, max) / 2);
    }

    // --- Rooms and portals of interiors ---

    // A room of 4 x 4 x 3 m in front of the camera, in the interior's space; selected.
    public void AddRoom(MapEntity interior)
    {
        if (World.Maps is not { } library || library.GetArchetype(interior.ArchetypeHash) is not { } archetype
            || !Matrix4x4.Invert(interior.Transform, out var toInterior))
            return;
        var center = Vector3.Transform(Camera.Position + Camera.Forward * 6f, toInterior);
        string name = $"room{archetype.InteriorRooms.Count}";
        var room = new InteriorRoom(center - new Vector3(2f, 2f, 1f), center + new Vector3(2f, 2f, 2f)) { Name = name };
        var edit = InteriorLayoutEdit.Apply(library, interior, () => library.AddRoom(interior.ArchetypeHash, room),
            $"Ajouter la salle {name}", isPortal: false, added: true);
        History.Add(edit);
        SelectRoom(new RoomSelection(interior, edit.Index));
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms), true);
        Notify($"Salle {edit.Index} « {name} » ajoutée devant la caméra : ajustez ses coins dans Propriétés, puis ajoutez-lui un portail.");
    }

    public void DeleteSelectedRoom()
    {
        if (SelectedRoom is not { Index: > 0 } selected || World.Maps is not { } library
            || library.GetArchetype(selected.Interior.ArchetypeHash) is not { } archetype || selected.Index >= archetype.InteriorRooms.Count)
            return;
        var room = archetype.InteriorRooms[selected.Index];
        int portals = archetype.InteriorPortals.Count(p => p.RoomFrom == selected.Index || p.RoomTo == selected.Index);
        string name = room.Name.Length > 0 ? room.Name : $"{selected.Index}";
        History.Add(InteriorLayoutEdit.Apply(library, selected.Interior, () =>
        {
            library.RemoveRoom(selected.Interior.ArchetypeHash, selected.Index);
            return selected.Index;
        }, $"Supprimer la salle {name}", isPortal: false, added: false));
        Select(selected.Interior);
        Notify($"Salle « {name} » supprimée{(portals > 0 ? $" avec ses {portals} portail(s)" : "")} ; ses objets vont dans la salle qui les entoure "
            + "(sinon dans limbo). Ctrl+Z pour annuler.");
    }

    // A portal of 1.2 x 2.2 m (at most) at the bottom of the side of the room facing the camera, to the outside; selected.
    public void AddPortal(MapEntity interior, int roomIndex)
    {
        if (World.Maps is not { } library || library.GetArchetype(interior.ArchetypeHash) is not { } archetype
            || roomIndex < 1 || roomIndex >= archetype.InteriorRooms.Count || !Matrix4x4.Invert(interior.Transform, out var toInterior))
            return;
        var room = archetype.InteriorRooms[roomIndex];
        var min = room.BoundsMin;
        var max = room.BoundsMax;
        var center = (min + max) / 2;
        var half = Vector3.Max((max - min) / 2, new Vector3(0.05f));
        var toCamera = Vector3.Transform(Camera.Position, toInterior) - center;
        bool alongX = MathF.Abs(toCamera.X) / half.X >= MathF.Abs(toCamera.Y) / half.Y;
        float width = MathF.Min(1.2f, 2 * (alongX ? half.Y : half.X) - 0.2f) / 2;
        float bottom = min.Z;
        float top = MathF.Min(min.Z + 2.2f, max.Z - 0.05f);
        Vector3[] corners = alongX
            ? [new(toCamera.X >= 0 ? max.X : min.X, center.Y - width, top), new(toCamera.X >= 0 ? max.X : min.X, center.Y + width, top),
               new(toCamera.X >= 0 ? max.X : min.X, center.Y + width, bottom), new(toCamera.X >= 0 ? max.X : min.X, center.Y - width, bottom)]
            : [new(center.X - width, toCamera.Y >= 0 ? max.Y : min.Y, top), new(center.X + width, toCamera.Y >= 0 ? max.Y : min.Y, top),
               new(center.X + width, toCamera.Y >= 0 ? max.Y : min.Y, bottom), new(center.X - width, toCamera.Y >= 0 ? max.Y : min.Y, bottom)];
        var portal = new InteriorPortal(roomIndex, 0, 0) { Corners = corners };
        var edit = InteriorLayoutEdit.Apply(library, interior, () => library.AddPortal(interior.ArchetypeHash, portal),
            $"Ajouter un portail à la salle {roomIndex}", isPortal: true, added: true);
        History.Add(edit);
        SelectPortal(new RoomSelection(interior, edit.Index));
        SetLayerVisible(RoomLayer.LayerName(RoomLayerPart.OutsidePortals), true);
        Notify($"Portail {edit.Index} ajouté sur le côté de la salle face à la caméra, vers l'extérieur : placez ses coins sur l'ouverture "
            + "et choisissez les salles qu'il relie.");
    }

    public void DeleteSelectedPortal()
    {
        if (SelectedPortal is not { } selected || World.Maps is not { } library
            || library.GetArchetype(selected.Interior.ArchetypeHash) is not { } archetype || selected.Index >= archetype.InteriorPortals.Count)
            return;
        History.Add(InteriorLayoutEdit.Apply(library, selected.Interior, () =>
        {
            library.RemovePortal(selected.Interior.ArchetypeHash, selected.Index);
            return selected.Index;
        }, $"Supprimer le portail {selected.Index}", isPortal: true, added: false));
        Select(selected.Interior);
        Notify($"Portail {selected.Index} supprimé. Ctrl+Z pour annuler.");
    }

    public void DeleteSelectedOccluder()
    {
        if (SelectedOccluder is not { } occluder)
            return;
        OccluderMaps.SetDeleted(occluder.File, occluder.Kind, occluder.Index, true);
        string what = occluder.Kind == OcclusionLayerPart.Boxes ? "la boîte" : "le maillage";
        History.Add(new OccluderPresenceEdit([occluder], deleted: true, $"Supprimer {what} d'occlusion {occluder.Index}"));
        SelectedOccluder = null;
        Notify($"{what} d'occlusion {occluder.Index} supprimé{(occluder.Kind == OcclusionLayerPart.Boxes ? "e" : "")} de {occluder.File.FileName}");
    }

    // --- Collision editing (tool 5) ---

    // Hand changes to the collision files of the project.
    public Data.Collisions.CollisionEdits CollisionEdits { get; private set; } = new();

    private readonly HashSet<CollisionPolygon> _selectedCollision = [];

    public IReadOnlySet<CollisionPolygon> SelectedCollision => _selectedCollision;

    // Incremented when the collision selection changes (the 3D view rebuilds its highlight).
    public int CollisionSelectionVersion { get; private set; }

    public void SelectCollision(IEnumerable<CollisionPolygon> polygons, CollisionSelectMode mode)
    {
        if (mode == CollisionSelectMode.Replace)
            _selectedCollision.Clear();
        foreach (var polygon in polygons)
        {
            if (mode == CollisionSelectMode.Toggle && !_selectedCollision.Add(polygon))
                _selectedCollision.Remove(polygon);
            else
                _selectedCollision.Add(polygon);
        }
        CollisionSelectionVersion++;
    }

    public void DeleteSelectedCollision()
    {
        if (_selectedCollision.Count == 0)
            return;
        Apply(CollisionEdit.Remove(CollisionEdits, [.. _selectedCollision]));
        int count = _selectedCollision.Count;
        SelectCollision([], CollisionSelectMode.Replace);
        Notify(count == 1 ? "1 surface de collision supprimée" : $"{count} surfaces de collision supprimées");
    }

    public void SetSelectedCollisionMaterial(byte material)
    {
        if (_selectedCollision.Count == 0 || World.Collisions is not { } catalog)
            return;
        Apply(CollisionEdit.SetMaterial(CollisionEdits, [.. _selectedCollision], material, catalog.Materials.Name(material)));
        CollisionSelectionVersion++;
    }

    private void Apply(IEdit edit)
    {
        edit.Redo();
        History.Add(edit);
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
                .Where(entity => entity.Interior is null).Select(entity => entity.Map)
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

    // Archetype files (.ytyp) to write when saving: those of the interiors whose objects were moved in this session.
    public IReadOnlyList<string> InteriorFilesToSave => World.Maps is { } library
        ? History.All.SelectMany(e => e.Entities).Select(e => e.Interior).OfType<MapEntity>()
            .Concat(History.All.OfType<IInteriorEdit>().Select(e => e.Interior))
            .Select(i => library.GetArchetypeFile(i.ArchetypeHash)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    // Archetype files of a FiveM export: those to save, plus the project's versions taken back when the map was loaded.
    public IReadOnlyList<string> InteriorFilesToExport => World.Maps is { } library
        ? InteriorFilesToSave.Concat(ProjectInteriorFiles(library)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    private (MapLibrary Library, List<string> Files)? _projectInteriorFiles;

    // The interiors' archetype files taken from the project's stream folder when the map was loaded.
    private List<string> ProjectInteriorFiles(MapLibrary library)
    {
        if (_projectInteriorFiles is not { } cached || cached.Library != library)
        {
            var files = library.GetInteriorArchetypeHashes().Select(library.GetArchetypeFile).OfType<string>()
                .Where(p => p.StartsWith(Data.FileSystem.GameContent.LooseFilesRoot + "\\", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _projectInteriorFiles = cached = (library, files);
        }
        return cached.Files;
    }

    public int PendingSaveCount => MapsToSave.Count + PathAreasToSave.Count + InteriorFilesToSave.Count + (CollisionsPending ? Math.Max(1, CollisionEdits.Files.Count) : 0)
        + (OccludersPending ? Math.Max(1, OccluderEdits.StreamFileNames.Count()) : 0);

    // Occluder changes not written to the project yet.
    public bool OccludersPending => OccluderMaps.HasUnsavedChanges(World);

    // The occluder changes as they would be saved now.
    public OccluderEdits OccluderEdits => OccluderMaps.Capture(World, OccluderFileName);

    // The project's occluder changes (occlusion.json next to projet.json), put back at the next map loading.
    private void LoadOccluderEdits()
    {
        var edits = OccluderEdits.Empty;
        try
        {
            edits = OccluderEdits.Load(Project?.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Diagnostics.ErrorLog.Add("Projet", $"{OccluderEdits.FileName} illisible : {ex.Message}", ex);
        }
        OccluderMaps.UseSaved(edits);
        World.OccluderFileNames = edits.StreamFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Writes occlusion.json and the changed occluder map files into the stream folder (files no longer changed are removed).
    private void SaveOccluderEdits(string stream, MapFileSaver.Report report)
    {
        if (Project is not { } project)
            return;
        var edits = OccluderEdits;
        int errors = report.Errors.Count;
        var written = OccluderFileSaver.Save(World, [], OccluderFileName, stream, report);
        if (report.Errors.Count > errors)
            return;
        string file = Path.Combine(project.Folder, OccluderEdits.FileName);
        try
        {
            foreach (var old in OccluderMaps.Saved.StreamFileNames.Except(written, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(stream, old);
                if (File.Exists(path))
                    File.Delete(path);
            }
            if (edits.IsEmpty)
                File.Delete(file);
            else
                File.WriteAllText(file, edits.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{OccluderEdits.FileName} : {ex.Message}");
            return;
        }
        OccluderMaps.MarkSaved(edits);
        World.OccluderFileNames = written.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Collision changes not written to the project yet.
    public bool CollisionsPending => CollisionEdits.Revision != _savedCollisionRevision;

    private int _savedCollisionRevision;

    private const string CollisionEditsFileName = "collisions.json";

    // The project's collision changes (collisions.json next to projet.json); none without a project.
    private void LoadProjectEdits()
    {
        LoadOccluderEdits();
        CollisionEdits = new Data.Collisions.CollisionEdits();
        if (Project is { } project && Path.Combine(project.Folder, CollisionEditsFileName) is var file && File.Exists(file))
        {
            try
            {
                CollisionEdits = Data.Collisions.CollisionEdits.FromJson(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                Diagnostics.ErrorLog.Add("Projet", $"{CollisionEditsFileName} illisible : {ex.Message}", ex);
            }
        }
        _savedCollisionRevision = CollisionEdits.Revision;
        SelectCollision([], CollisionSelectMode.Replace);
    }

    // Writes collisions.json and the changed .ybn files into the stream folder (files no longer changed are removed).
    private void SaveCollisionEdits(string stream, MapFileSaver.Report report)
    {
        if (Project is not { } project)
            return;
        string file = Path.Combine(project.Folder, CollisionEditsFileName);
        try
        {
            if (File.Exists(file))
            {
                var before = Data.Collisions.CollisionEdits.FromJson(File.ReadAllText(file));
                foreach (var path in before.Files.Keys.Where(p => CollisionEdits.For(p) is null))
                {
                    string old = Path.Combine(stream, Path.GetFileName(path));
                    if (File.Exists(old))
                        File.Delete(old);
                }
            }
            if (CollisionEdits.Files.Count == 0)
                File.Delete(file);
            else
                File.WriteAllText(file, CollisionEdits.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            report.Errors.Add($"{CollisionEditsFileName} : {ex.Message}");
            return;
        }
        CollisionFileSaver.Save(World, CollisionEdits, [], stream, report);
    }

    // "2 YMAP", "2 YMAP, 1 YND".
    public string PendingSaveText => FilesText(MapsToSave.Count, PathAreasToSave.Count)
        + (InteriorFilesToSave.Count is var interiors and > 0 ? $", {interiors} YTYP" : "")
        + (CollisionsPending ? $", {CollisionEdits.Files.Count} YBN" : "")
        + (OccludersPending ? $", {OccluderEdits.StreamFileNames.Count()} YMAP d'occlusion" : "");

    public static string FilesText(int maps, int paths) =>
        paths == 0 ? $"{maps} YMAP" : maps == 0 ? $"{paths} YND" : $"{maps} YMAP, {paths} YND";

    public void SaveMaps()
    {
        if (Online.IsGuest)
        {
            LastSave = ("Session Online : seul l'hôte enregistre le projet.", false, DateTime.Now);
            return;
        }
        if (SaveFolder is not { } folder)
            return;
        var maps = MapsToSave;
        var paths = PathAreasToSave;
        bool collisions = CollisionsPending;
        bool occluders = OccludersPending;
        var interiorFiles = InteriorFilesToSave;
        if (maps.Count == 0 && paths.Count == 0 && !collisions && !occluders && interiorFiles.Count == 0)
            return;
        var report = maps.Count > 0 ? MapFileSaver.Save(World, maps, folder) : new MapFileSaver.Report([], []);
        foreach (var map in maps.Where(m => m.FilePath is { } p && report.Saved.Contains(Path.GetFileName(p))))
            _savedMaps.Add(map);
        PathFileSaver.Save(World, paths, folder, report);
        InteriorFileSaver.Save(World, interiorFiles, folder, report);
        if (collisions)
            SaveCollisionEdits(folder, report);
        if (occluders)
            SaveOccluderEdits(folder, report);
        if (report.Errors.Count == 0)
        {
            _savedCollisionRevision = CollisionEdits.Revision;
            History.MarkSaved();
            int pathFiles = report.Saved.Count(f => f.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase));
            int typeFiles = report.Saved.Count(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase));
            string text = FilesText(report.Saved.Count - pathFiles - typeFiles, pathFiles) + (typeFiles > 0 ? $", {typeFiles} YTYP" : "");
            LastSave = ($"{text} enregistré(s) dans le projet « {Project!.Name} »", true, DateTime.Now);
        }
        else
        {
            LastSave = ($"Échec de l'enregistrement : {string.Join(" ; ", report.Errors)}", false, DateTime.Now);
            Diagnostics.ErrorLog.Add("Enregistrement", string.Join(" ; ", report.Errors));
        }
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
            LoadProjectEdits();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ProjectError = $"Projet introuvable ou illisible ({folder}) : {ex.Message}";
            Diagnostics.ErrorLog.Add("Projet", ProjectError, ex);
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
        if (Online.Blocks() is { } blocked)
            return blocked;
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
        if (Online.Blocks() is { } blocked)
            return blocked;
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
        if (Online.Blocks() is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        Project = null;
        Settings.ProjectFolder = null;
        SaveSettings();
        LastSave = null;
        LoadProjectEdits();
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
        // Without reload, the changes in progress go into the new project (they are kept).
        if (reload)
        {
            LoadProjectEdits();
            World.RequestMapReload();
        }
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
        if (Online.Blocks() is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
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
            Diagnostics.ErrorLog.Add("Projet", ProjectError, ex);
        }
    }

    public bool IsLayerVisible(string layer) =>
        IsHiddenByDefault(layer) ? Settings.ShownLayers.Contains(layer) : !Settings.HiddenLayers.Contains(layer);

    private static bool IsHiddenByDefault(string layer) =>
        layer.StartsWith(CollisionLayer.LayerPrefix, StringComparison.Ordinal) || layer.StartsWith(RoomLayer.LayerPrefix, StringComparison.Ordinal)
        || layer.StartsWith(OcclusionLayer.LayerPrefix, StringComparison.Ordinal);

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
        Online = new NodeRunner.App.Online.OnlineController(this);
        Backup = new AutoBackup(this);
    }

    // Rescue copy of the unsaved changes (data/secours).
    public AutoBackup Backup { get; }

    // NodeRunner Online: session with other computers on the same map.
    public NodeRunner.App.Online.OnlineController Online { get; }

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

public enum CollisionSelectMode
{
    Replace,
    Add,
    Toggle,
}

// Tools of the 3D view (floating bar, keys 1 to 5).
public enum TransformTool
{
    Select,
    Move,
    Rotate,
    Scale,
    Collision,
}
