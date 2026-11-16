using NodeRunner.App.Configuration;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

// Application-wide state shared by the window and the UI.
internal sealed partial class AppState
{
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }

    public GameInstallation? Game { get; private set; }

    public IReadOnlyList<GameInstallation> OtherInstallations { get; private set; } = [];

    public string AdapterName { get; set; } = "?";

    // Video memory of the models: used and limit (bytes), set by the main window.
    public Func<(long Used, long Limit)>? ModelMemoryInfo { get; set; }

    public (long Used, long Limit)? ModelMemory => ModelMemoryInfo?.Invoke();

    // The window shows the Windows 11 backdrop (Mica) through the interface (see Settings.Transparency).
    public bool TransparentWindow { get; set; }

    public Camera Camera { get; } = new();

    public WorldLoader World { get; }

    // GTA IV: the folder found on this computer (null if none) and the files opened when it is the session's game.
    public string? Gta4Folder { get; private set; }
    public Gta4World Gta4 { get; } = new();
    public GameKind ActiveGame { get; set; } = GameKind.Gta5;
    public bool IsGta4 => ActiveGame == GameKind.Gta4;

    // GTA IV: the object clicked in the 3D view (read only), forgotten when another map is shown.
    public MapEntity? Gta4Selection
    {
        get => _gta4SelectionScene is not null && _gta4SelectionScene == Gta4.Scene ? _gta4Selection : null;
        set
        {
            _gta4Selection = value;
            _gta4SelectionScene = value is null ? null : Gta4.Scene;
        }
    }

    private MapEntity? _gta4Selection;
    private NodeRunner.Data.Native.Gta4.Gta4Scene? _gta4SelectionScene;

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
            if (value is not null && IsItemLocked(() => NodeRunner.App.Online.SelectedItem.Node(value)))
                return;
            _selectedNode = value;
            _selectedNodeNetwork = World.PathNetwork;
        }
    }

    private Data.Paths.PathNode? _selectedNode;
    private Data.Paths.PathNetwork? _selectedNodeNetwork;

    // A scenario point selected in the 3D view (an object selection clears it). Forgotten when the scenarios are read
    // again (map loading).
    public Data.Scenarios.ScenarioPoint? SelectedScenario
    {
        get => _selectedScenarioSet == World.Scenarios && _selectedScenario is { Region: { } region } point
               && point.Index < region.Points.Count && region.Points[point.Index] == point ? point : null;
        set
        {
            _selectedScenario = value;
            _selectedScenarioSet = World.Scenarios;
        }
    }

    private Data.Scenarios.ScenarioPoint? _selectedScenario;
    private Data.Scenarios.ScenarioSet? _selectedScenarioSet;

    // A box or a mesh of an occluder map file selected in the 3D view (an object selection clears it). Forgotten when
    // the map is loaded again.
    public OccluderSelection? SelectedOccluder
    {
        get => _selectedOccluderLibrary == World.Maps && _selectedOccluder is { } o && !o.File.IsDeleted(o.Kind, o.Index) ? o : null;
        set
        {
            if (value is not null && IsItemLocked(() => NodeRunner.App.Online.SelectedItem.Occluder(value)))
                return;
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
            if (value is not null && IsItemLocked(() => World.Maps is { } library ? NodeRunner.App.Online.SelectedItem.Room(library, value) : null))
                return;
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
            if (value is not null && IsItemLocked(() => World.Maps is { } library ? NodeRunner.App.Online.SelectedItem.Room(library, value, portal: true) : null))
                return;
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

    // A parking spot selected in the 3D view (an object selection clears it). Forgotten when the map is loaded again.
    public CarGeneratorSelection? SelectedCarGenerator
    {
        get => _selectedCarGeneratorLibrary == World.Maps && _selectedCarGenerator is { Spot: not null } s ? s : null;
        set
        {
            if (value is not null && IsItemLocked(() => World.Maps is { } library ? NodeRunner.App.Online.SelectedItem.CarGenerator(library, value) : null))
                return;
            _selectedCarGenerator = value;
            _selectedCarGeneratorLibrary = World.Maps;
        }
    }

    private CarGeneratorSelection? _selectedCarGenerator;

    // A timecycle zone selected in the 3D view (an object selection clears it). Forgotten when the map is loaded again.
    public TimecycleSelection? SelectedTimecycle
    {
        get => _selectedTimecycleLibrary == World.Maps && _selectedTimecycle is { Zone: not null } z ? z : null;
        set
        {
            if (value is not null && IsItemLocked(() => World.Maps is { } library ? NodeRunner.App.Online.SelectedItem.Timecycle(library, value) : null))
                return;
            _selectedTimecycle = value;
            _selectedTimecycleLibrary = World.Maps;
        }
    }

    private TimecycleSelection? _selectedTimecycle;
    private MapLibrary? _selectedTimecycleLibrary;

    // A distant light selected in the 3D view (an object selection clears it). Forgotten when the map is loaded again.
    public LodLightSelection? SelectedLodLight
    {
        get => _selectedLodLightLibrary == World.Maps && _selectedLodLight is { Light: not null } l ? l : null;
        set
        {
            if (value is not null && IsItemLocked(() => World.Maps is { } library ? NodeRunner.App.Online.SelectedItem.Light(library, value) : null))
                return;
            _selectedLodLight = value;
            _selectedLodLightLibrary = World.Maps;
        }
    }

    private LodLightSelection? _selectedLodLight;
    private MapLibrary? _selectedLodLightLibrary;
    private MapLibrary? _selectedCarGeneratorLibrary;

    public void Select(MapEntity? entity)
    {
        if (entity is not null && IsLocked(entity))
            return;
        _selection.Clear();
        Gta4Selection = null;
        SelectedPathNode = null;
        SelectedScenario = null;
        SelectedOccluder = null;
        SelectedCarGenerator = null;
        SelectedTimecycle = null;
        SelectedLodLight = null;
        SelectedRoom = null;
        SelectedPortal = null;
        if (entity is not null)
            _selection.Add(entity);
        _selectionLibrary = World.Maps;
    }

    public void SelectAll(IReadOnlyList<MapEntity> entities)
    {
        Select(null);
        var free = entities.Distinct().Where(e => Online?.LockedBy(e) is null).ToList();
        if (free.Count < entities.Distinct().Count())
            Notify(L($"{entities.Distinct().Count() - free.Count} objet(s) laissé(s) de côté : sélectionnés par un autre participant."), warning: true);
        _selection.AddRange(free);
    }

    // Online: an object another participant has selected cannot be selected here.
    private bool IsLocked(MapEntity entity)
    {
        if (Online?.LockedBy(entity) is not { } who)
            return false;
        Notify(L($"Objet sélectionné par {who} : verrouillé tant que cette personne le garde sélectionné."), warning: true);
        return true;
    }

    private bool IsItemLocked(Func<byte[]?> item)
    {
        if (Online?.LockedBy(item) is not { } who)
            return false;
        Notify(L($"Élément sélectionné par {who} : verrouillé tant que cette personne le garde sélectionné."), warning: true);
        return true;
    }

    // Takes these objects out of the selection.
    public void Deselect(IReadOnlyCollection<MapEntity> entities)
    {
        if (_selectionLibrary == World.Maps)
            _selection.RemoveAll(e => entities.Contains(e));
    }

    // The selected objects that can be moved, the main one last.
    public IReadOnlyList<MapEntity> EditableSelection => SelectedEntities.Where(e => e.CanEdit).ToList();

    // The selected objects that can be deleted (not those defined by an interior's archetype), the main one last.
    public IReadOnlyList<MapEntity> DeletableSelection => SelectedEntities.Where(e => e.CanDelete).ToList();

    public void ToggleSelect(MapEntity entity)
    {
        if (_selectionLibrary != World.Maps)
            Select(null);
        if (!_selection.Remove(entity) && !IsLocked(entity))
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
        History.Add(DeleteEdit.Apply(library, plan, L($"Supprimer {name}")));
        Select(null);
    }

    // Object type to add, waiting for the main form to find the ground at the centre of the 3D view; 0 if none.
    public uint PlaceRequest { get; set; }

    // Prop library action asked from a panel, carried out by the main window (dialogs).
    public LibraryAction LibraryRequest { get; set; }

    // A file dialog asked from a panel, shown by the main window after the frame (a dialog blocks the frame); it gets
    // the window to own the dialog.
    public Action<IWin32Window>? DialogRequest { get; set; }

    // Where to put the object of PlaceRequest: a point of the 3D view (0..1 of its width and height), null = its centre.
    public System.Numerics.Vector2? PlacePoint { get; set; }

    // Pictures of the library's props (Library panel).
    public World.LibraryThumbnails Thumbnails { get; private set; } = null!;

    // Result of the last editing command (add an object, put it on the ground...), shown a few seconds in the status
    // bar.
    public (string Text, bool IsWarning, DateTime Time)? Notice { get; private set; }

    public void Notify(string text, bool warning = false) => Notice = (text, warning, DateTime.Now);

    // Adds an object of type archetypeHash standing on ground, in the project's own map, selects it and takes the
    // "move" tool.
    public void Place(uint archetypeHash, System.Numerics.Vector3 ground)
    {
        Notice = null;
        if (Online.BlocksType(archetypeHash) is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        if (World.Maps is not { } library || ProjectMap(library) is not { } map)
            return;
        var archetype = library.GetArchetype(archetypeHash);
        var entity = Adding.NewEntity(archetypeHash, archetype, Adding.StandingOn(ground, archetype), System.Numerics.Quaternion.Identity);
        // The point found in the picture may be a tree top or a model without collision: rest on the collision below.
        if (GroundPosition(entity, archetype) is { } onCollision)
            entity.SetPlacement(onCollision, entity.Orientation, entity.Scale);
        string name = World.Names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        if (AddToInterior(library, [entity], L($"Ajouter {name}")) is not { } inside)
        {
            History.Add(AddEdit.Apply(library, map, entity, L($"Ajouter {name}")));
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
        var edit = EntitySetMoveEdit.Apply(library, moving, setIndex, target is null ? L($"Sortir {what} de son entity set") : L($"Ranger {what} dans {setName}"));
        History.Add(edit);
        if (target is not null)
            library.SetEntitySetActive(target, true);
        SelectAll(edit.Selection);
        Notify(target is null
            ? L($"{what} : sorti(s) de l'entity set, toujours affiché(s), dans toutes les copies de l'intérieur (enregistré dans son .ytyp).")
            : L($"{what} : rangé(s) dans l'entity set {setName}, dans toutes les copies de l'intérieur (enregistré dans son .ytyp)."));
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
            Notify(objects.Count == 1 ? L("L'objet est déjà dans cette salle.") : L("Les objets sont déjà dans ces salles."));
            return;
        }
        string what = changing.Count == 1 ? World.Names?.Describe(changing[0].Entity.ArchetypeHash) ?? "l'objet" : $"{changing.Count} objets";
        string RoomLabel(int r) => r == 0 ? "limbo" : archetype.InteriorRooms[r].Name is { Length: > 0 } name ? name : $"salle {r}";
        string target = changing.Select(c => c.Room).Distinct().Count() == 1 ? RoomLabel(changing[0].Room) : L("leurs salles");
        var edit = EntityRoomEdit.Apply(library, [.. changing.Select(c => c.Entity)], [.. changing.Select(c => c.Room)], L($"Mettre {what} dans {target}"));
        History.Add(edit);
        SelectAll(edit.Selection);
        Notify(L($"{what} : dans {target}, dans toutes les copies de l'intérieur (enregistré dans son .ytyp au Ctrl+S)."));
    }

    // Attaches the selected objects of the main selection's interior to a portal (in no room, as in the game's files),
    // or takes them off it (-1: back to the room around them), in every copy of the interior.
    public void SetSelectionPortal(int portal)
    {
        if (World.Maps is not { } library || Selection?.Interior is not { } instance
            || library.GetArchetype(instance.ArchetypeHash) is not { Kind: Data.Maps.ArchetypeKind.Interior } archetype
            || portal < -1 || portal >= archetype.InteriorPortals.Count)
            return;
        var objects = SelectedEntities.Where(e => e.Interior == instance && e.Definition is not null && e.InteriorPortal != portal).ToList();
        if (objects.Count == 0)
            return;
        var places = objects.Select(e => portal >= 0 ? (-1, portal)
            : (Math.Max(0, Adding.RoomAt(instance, archetype, e.Position + new System.Numerics.Vector3(0, 0, RoomProbeHeight))), -1)).ToList();
        string what = objects.Count == 1 ? World.Names?.Describe(objects[0].ArchetypeHash) ?? "l'objet" : $"{objects.Count} objets";
        var edit = EntityPortalEdit.Apply(library, objects, places, portal >= 0 ? L($"Rattacher {what} au portail {portal}") : L($"Détacher {what} de son portail"));
        History.Add(edit);
        SelectAll(edit.Selection);
        Notify(portal >= 0
            ? L($"{what} : rattaché(s) au portail {portal}, dans toutes les copies de l'intérieur (enregistré dans son .ytyp au Ctrl+S).")
            : L($"{what} : détaché(s) du portail, rangé(s) dans la salle autour."));
    }

    // Gives the selected objects another type (model), same place; objects of interiors are left out.
    public void SetSelectionArchetype(uint archetypeHash)
    {
        if (Online.BlocksType(archetypeHash) is { } blocked)
        {
            Notify(blocked, warning: true);
            return;
        }
        if (World.Maps is not { } library || library.GetArchetype(archetypeHash) is not { } archetype
            || archetype.Kind == Data.Maps.ArchetypeKind.Interior)
            return;
        var changing = SelectedEntities.Where(e => ArchetypeEdit.CanChange(e) && e.ArchetypeHash != archetypeHash).ToList();
        if (changing.Count == 0)
            return;
        string name = World.Names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        string what = changing.Count == 1 ? World.Names?.Describe(changing[0].ArchetypeHash) ?? $"0x{changing[0].ArchetypeHash:X8}" : L($"{changing.Count} objets");
        var edit = ArchetypeEdit.Apply(changing, archetypeHash, L($"Remplacer {what} par {name}"));
        History.Add(edit);
        SelectAll(edit.Selection);
        Notify(L($"{what} remplacé(s) par {name}."));
    }

    // Gives the selected doors another door model, fitted in their frame.
    public void ReplaceSelectedDoors(uint archetypeHash)
    {
        if (World.Maps is not { } library || library.GetArchetype(archetypeHash) is not { } to)
            return;
        var names = World.Names;
        var doors = SelectedEntities.Where(e => ArchetypeEdit.CanChange(e) && e.ArchetypeHash != archetypeHash
            && Doors.IsDoor(names?.TryGet(e.ArchetypeHash)) && library.GetArchetype(e.ArchetypeHash) is not null).ToList();
        if (doors.Count == 0)
        {
            Notify(SelectedEntities.Any(e => !ArchetypeEdit.CanChange(e))
                ? L("Les portes des intérieurs (MLO) ne peuvent pas encore changer de modèle.") : L("Aucune porte à remplacer."));
            return;
        }
        string name = names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        string toName = names?.TryGet(archetypeHash) ?? name;
        var widths = doors.Select(d => library.GetArchetype(d.ArchetypeHash)!).Select(a => a.BoundsMax.X - a.BoundsMin.X).ToList();
        var after = doors.Select(d => DoorReplaceEdit.Fit(d, library.GetArchetype(d.ArchetypeHash)!, to, toName)).ToList();
        string what = doors.Count == 1 ? names?.Describe(doors[0].ArchetypeHash) ?? L("la porte") : L($"{doors.Count} portes");
        var edit = DoorReplaceEdit.Apply(doors, archetypeHash, after, L($"Remplacer {what} par {name}"));
        History.Add(edit);
        SelectAll(edit.Selection);
        float wider = (to.BoundsMax.X - to.BoundsMin.X) - widths.Max();
        float narrower = (to.BoundsMax.X - to.BoundsMin.X) - widths.Min();
        Notify(wider > 0.05f ? L($"{what} remplacée(s) par {name} : {wider * 100:0} cm plus large, elle peut entrer dans le mur.")
            : narrower < -0.05f ? L($"{what} remplacée(s) par {name} : {-narrower * 100:0} cm plus étroite, un jour peut rester.")
            : L($"{what} remplacée(s) par {name}, recalée(s) dans l'encadrement."));
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
            Notify(L($"Ajouté à l'intérieur {World.Names?.Describe(instance.ArchetypeHash) ?? ""} (salle {roomName}) : enregistré dans son .ytyp, dans toutes ses copies."));
            return edit.Selection;
        }
        return null;
    }

    // Interiors are found by their origin: the biggest ones (tunnels) span a few hundred metres.
    private const float InteriorSearchRadius = 400f;

    public CollisionQuery CollisionQuery { get; } = new();

    // Model workshop in a tab beside the map: open, shown in place of the 3D view, brought to the front at the next frame.
    public bool WorkshopOpen { get; set; }
    public bool WorkshopShown { get; set; }

    // The workshop in its own window (another screen) instead of a tab; while a tab, whether it covers the 3D view.
    public bool WorkshopDetached { get; set; }
    public bool WorkshopCoversMap { get; set; }
    public bool WorkshopFront { get; set; }

    // A map object whose model is to open in the workshop.
    public NodeRunner.Data.Maps.MapEntity? WorkshopRequest { get; set; }

    // The map object the workshop's model was opened from (null: from the viewer), and whether the map around it is
    // drawn in grey in the workshop.
    public NodeRunner.Data.Maps.MapEntity? WorkshopEntity { get; set; }
    public bool WorkshopShowsMap { get; set; }

    // Doors opened in the 3D view (display only).
    public DoorAnimator OpenDoors { get; } = new();

    // Opens or closes the door (with its other leaf, for a double door) or each door in the 3D view.
    public void ToggleDoors(IReadOnlyList<MapEntity> doors)
    {
        if (doors.Count == 1 && World.Maps is { } library && DoorAnimator.Partner(doors[0], library, World.Names) is { } partner)
            doors = [doors[0], partner];
        bool open = !doors.Any(OpenDoors.IsOpen);
        string? message = null;
        foreach (var door in doors)
            message = OpenDoors.Set(door, open, this) ?? message;
        if (message is not null)
            Notify(message, warning: true);
    }

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
            Notify(L("Les collisions ne sont pas encore chargées."), warning: true);
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
        string missing = withoutGround == 0 ? "" : L($" Aucune collision sous {withoutGround} objet(s).");
        if (moved.Count == 0)
        {
            if (withoutGround == entities.Count)
                Notify(CollisionQuery.Errors.Count > 0
                    ? L($"Collision illisible sous {Name(entities[^1])} : {CollisionQuery.Errors[0]}")
                    : entities.Count == 1 ? L($"Aucune collision sous {Name(entities[0])}.") : L("Aucune collision sous les objets sélectionnés."), warning: true);
            else
                Notify((entities.Count == 1 ? L($"{Name(entities[0])} est déjà posé au sol.") : L("Les objets sont déjà posés au sol.")) + missing, withoutGround > 0);
            return;
        }

        if (entities.Count == 1)
        {
            var (entity, height) = moved[0];
            History.Add(FollowCollision(new PlacementEdit(entity, before[0], Placement.Of(entity), L($"Poser au sol {Name(entity)}"))));
            Notify(L($"{Name(entity)} posé au sol ({(height < 0 ? L("descendu") : L("monté"))} de {MathF.Abs(height):0.00} m)."));
            return;
        }
        // One step for the whole selection (objects already on the ground included: they keep their place).
        History.Add(FollowCollision(new GroupPlacementEdit(entities, before, [.. entities.Select(Placement.Of)], L($"Poser au sol {entities.Count} objets"))));
        Notify(L($"{moved.Count} objet(s) posé(s) au sol sur {entities.Count}.{missing}"), withoutGround > 0);
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

    // Ctrl+C: remembers the selected objects; an interior (MLO) is pasted as another instance of it, with its objects.
    public void CopySelection()
    {
        if (World.Maps is not { } library || SelectedEntities is not { Count: > 0 } selected)
            return;
        Copied = ObjectCopy.Copy(selected, library.GetArchetype);
        Notify(selected.Count > 1 ? L($"{selected.Count} objets copiés. Ctrl+V pour les coller au centre de la vue.")
            : selected[0].IsInterior ? L($"Intérieur {World.Names?.Describe(selected[0].ArchetypeHash) ?? ""} copié. Ctrl+V pour en placer une autre copie au centre de la vue (même .ytyp du jeu, mêmes objets).")
            : L($"{World.Names?.Describe(selected[0].ArchetypeHash) ?? L("Objet")} copié. Ctrl+V pour le coller au centre de la vue."));
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
        // An interior never goes inside another one.
        if (!pasted.Any(e => e.IsInterior) && AddToInterior(library, pasted, L($"Coller {name}")) is { } inside)
            pasted = inside;
        else
        {
            History.Add(AddEdit.Apply(library, map, pasted, L($"Coller {name}")));
            if (pasted.FirstOrDefault(e => e.IsInterior) is { } interior)
                Notify(L($"Copie de l'intérieur {World.Names?.Describe(interior.ArchetypeHash) ?? ""} placée dans le YMAP du projet. Ses objets, salles et portails restent ceux de son .ytyp : les modifier change toutes ses copies."));
        }
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
            return (L("La carte n'est pas chargée."), false, []);
        var map = library.Maps.FirstOrDefault(m => !m.IsCustom && m.FilePath is { } p
            && string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
        if (map?.FilePath is not { } gamePath || content.ReadFile(gamePath) is not { } gameBytes)
            return (L($"{fileName} n'est pas un YMAP de la carte chargée."), false, []);

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
            return (L($"Lecture impossible de {otherFile} : {ex.Message}"), false, []);
        }

        var names = World.Names;
        var conflicts = plan.Conflicts
            .Select(c => L($"{names?.Describe(c.ArchetypeHash) ?? $"0x{c.ArchetypeHash:X8}"} ({c.Position.X:0.0}, {c.Position.Y:0.0}, {c.Position.Z:0.0}) : {c.Reason}"))
            .ToList();
        if (plan.IsEmpty)
            return (plan.Conflicts.Count == 0 ? L($"Rien à reprendre : {fileName} a déjà tous les changements de « {owner} ».")
                : L($"Rien à reprendre de « {owner} » sans conflit."), plan.Conflicts.Count == 0, conflicts);

        MapData? addTo = null;
        var added = new List<MapEntity>();
        if (plan.Additions.Count > 0)
        {
            if ((addTo = ProjectMap(library)) is null)
                return (L("Ouvre ou crée d'abord un projet : les objets ajoutés par l'autre version vont dans le YMAP du projet."), false, conflicts);
            foreach (var (hash, placement) in plan.Additions)
            {
                var entity = Adding.NewEntity(hash, library.GetArchetype(hash), placement.Position, placement.Orientation);
                entity.SetPlacement(placement.Position, placement.Orientation, placement.Scale);
                added.Add(entity);
            }
        }
        History.Add(MergeEdit.Apply(library, plan, addTo, added, L($"Reprendre les changements de « {owner} » ({fileName})")));

        var parts = new List<string>();
        if (plan.Moves.Count > 0)
            parts.Add(L($"{plan.Moves.Count} déplacé(s)"));
        if (plan.Deletions.Count > 0)
            parts.Add(L($"{plan.Deletions.Count} supprimé(s)"));
        if (plan.Additions.Count > 0)
            parts.Add(L($"{plan.Additions.Count} ajouté(s) au YMAP du projet"));
        string text = L($"Changements de « {owner} » repris dans {fileName} : {string.Join(", ", parts)}.")
            + (conflicts.Count > 0 ? L($" {conflicts.Count} conflit(s) : ta version est gardée.") : "");
        Notify(text, conflicts.Count > 0);
        return (text, true, conflicts);
    }

    // The project's own map, where added objects go (created if needed); null, with a message, without a project.
    private MapData? ProjectMap(MapLibrary library)
    {
        if (Project is not { } project)
        {
            Notify(L("Ouvre ou crée d'abord un projet : les objets ajoutés vont dans le YMAP du projet."), warning: true);
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
        if ((IsGta4 ? Gta4.Scene?.Library : World.Maps)?.GetArchetype(entity.ArchetypeHash) is { } archetype)
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
                Notify(L("Patientez : la session se synchronise."), warning: true);
            return;
        }
        SelectAfterUndo(undo ? History.Undo() : History.Redo(), undone: undo);
    }

    internal void SelectAfterUndo(IEdit? edit, bool undone)
    {
        if (edit is IPathNodeEdit nodeEdit)
        {
            Select(null);
            SelectedPathNode = edit is PathNodeDeleteEdit && !undone || edit is PathNodeAddEdit && undone ? null : nodeEdit.Node;
        }
        else if (edit is ScenarioEdit scenarioEdit)
        {
            Select(null);
            SelectedScenario = scenarioEdit.Point;
        }
        else if (edit is ScenarioAddEdit or ScenarioDeleteEdit)
        {
            Select(null);
            bool shown = edit is ScenarioAddEdit ? !undone : undone;
            SelectedScenario = shown ? (edit as ScenarioAddEdit)?.Point ?? ((ScenarioDeleteEdit)edit).Point : null;
        }
        else if (edit is OccluderBoxEdit boxEdit)
        {
            Select(null);
            SelectedOccluder = new OccluderSelection(boxEdit.File, OcclusionLayerPart.Boxes, boxEdit.Index);
        }
        else if (edit is ITimecycleEdit zoneEdit)
        {
            Select(null);
            var zone = new TimecycleSelection(zoneEdit.Map, zoneEdit.Index);
            SelectedTimecycle = zone.Zone is null ? null : zone;
        }
        else if (edit is ILodLightEdit lightEdit)
        {
            Select(null);
            SelectedLodLight = new LodLightSelection(lightEdit.Distant, lightEdit.Index, lightEdit.Lod);
        }
        else if (edit is ICarGeneratorEdit spotEdit)
        {
            // A spot deleted again (an addition undone, a deletion redone) leaves nothing selected.
            Select(null);
            var spot = new CarGeneratorSelection(spotEdit.Map, spotEdit.Index);
            SelectedCarGenerator = spot.Spot is null ? null : spot;
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
        else if (edit is CollisionMoveEdit collisionMove)
        {
            Tool = TransformTool.Collision;
            SelectCollision(collisionMove.Polygons, CollisionSelectMode.Replace);
        }
        else if (edit is CollisionEdit collisionEdit)
        {
            Tool = TransformTool.Collision;
            SelectCollision(collisionEdit.Polygons.Where(p => !CollisionEdits.IsRemoved(p.Path, p.Polygon)), CollisionSelectMode.Replace);
        }
        else if (edit is { Selection.Count: > 0 })
            SelectAll(edit.Selection);
    }

    private void Apply(IEdit edit)
    {
        edit.Redo();
        History.Add(edit);
    }

    // Each game keeps its own layers: hiding the GTA V boxes does not hide those of Liberty City.
    private HashSet<string> HiddenLayers => IsGta4 ? Settings.Gta4HiddenLayers : Settings.HiddenLayers;
    private HashSet<string> ShownLayers => IsGta4 ? Settings.Gta4ShownLayers : Settings.ShownLayers;

    public bool IsLayerVisible(string layer) =>
        IsHiddenByDefault(layer) ? ShownLayers.Contains(layer) : !HiddenLayers.Contains(layer);

    // GTA IV: the 3D models are shown, the boxes of the objects only on demand.
    private bool IsHiddenByDefault(string layer) =>
        IsGta4 && layer.StartsWith("Entity.", StringComparison.Ordinal)
        || layer.StartsWith(CollisionLayer.LayerPrefix, StringComparison.Ordinal) || layer.StartsWith(RoomLayer.LayerPrefix, StringComparison.Ordinal)
        || layer.StartsWith(OcclusionLayer.LayerPrefix, StringComparison.Ordinal) || layer == CarGeneratorLayer.LayerName
        || layer == TimecycleLayer.LayerName || layer == LodLightLayer.LayerName;

    public void SetLayerVisible(string layer, bool visible)
    {
        bool changed = IsHiddenByDefault(layer)
            ? visible ? ShownLayers.Add(layer) : ShownLayers.Remove(layer)
            : visible ? HiddenLayers.Remove(layer) : HiddenLayers.Add(layer);
        if (changed)
            SaveSettings();
    }

    public AppState(AppPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
        World = new WorldLoader { UserNamesFile = Path.Combine(paths.DataDirectory, UserNamesFileName), LibraryFolder = LibraryFolder };
        Thumbnails = new World.LibraryThumbnails(Path.Combine(paths.DataDirectory, "vignettes"));
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

        var manual = GameFolderValidator.TryCreate(Settings.GameFolder, L("Choix manuel"));
        Game = manual
               ?? found.FirstOrDefault(i => i.Edition == GameEdition.Legacy)
               ?? found.FirstOrDefault();

        OtherInstallations = found
            .Where(i => Game is null || !string.Equals(i.FolderPath, Game.FolderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Gta4Folder = Gta4InstallLocator.Resolve(Settings.Gta4Folder) ?? Gta4InstallLocator.Find();
    }

    public bool TrySetManualGameFolder(string folder)
    {
        var install = GameFolderValidator.TryCreate(folder, L("Choix manuel"));
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

internal enum LibraryAction { None, AddFolder, AddZip, ChooseFolder, Default, Reload }
