using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Models;

namespace NodeRunner.App.Ui;

// Dockable "Visionneuse" panel: list of the vehicles and object types on the left (search), the model in the middle
// (turn, pan, zoom, display options, captures), and an inspector on the right: paint (vehicles), parts, textures,
// vehicle sheet or object type, file.
internal sealed unsafe partial class ViewerPanel(AppState state)
{
    public const string Title = $"{Icons.Box}  Visionneuse###Visionneuse";

    private static string[] Filters => [L("Véhicules"), L("Objets"), L("Bibliothèque"), L("Sons")];
    private const float ListWidth = 230f;
    private const float InspectorWidth = 340f;

    private static VehiclePaintSlot[] PaintSlots => VehiclePaintColors.Slots;
    private static int[] DefaultPaint => VehiclePaintColors.Default;

    // The catalogue's model, or the workshop's while its tab is drawn.
    private ModelViewer Viewer => _inWorkshop ? state.WorkshopViewer : state.Viewer;
    private bool _inWorkshop;

    // The model viewer shows the game NodeRunner runs on: GTA V, or GTA IV (game or episode chosen).
    private bool IsGta4 => state.IsGta4;
    private MapLibrary? Library => state.ShownMaps;
    private HashNames? Names => state.ShownNames;
    private ModelCatalog? Models => state.Other is { } other ? other.Models : state.World.Models;
    private VehiclePaints? Paints => IsGta4 ? state.Gta4.Vehicles?.Paints : state.IsClassic ? null : state.World.Vehicles?.Paints;

    private Task<ViewerCatalog>? _building;
    private ViewerCatalog? _catalog;
    private MapLibrary? _catalogLibrary;
    private int _filter;
    private string _search = "";
    private List<ViewerItem> _rows = [];
    private (int Filter, string Search, ViewerCatalog? Catalog, bool Short, int Favorites) _rowsBuiltFor;
    private bool _scrollToItem;

    private readonly int[] _paint = (int[])DefaultPaint.Clone();
    private int _paintSet;
    private ViewerItem? _paintFor;
    private bool _paintOn = true;
    private int _pickingSlot = -1;
    private string _paletteSearch = "";
    private int _paletteFamily;
    private readonly Random _random = new();

    private int _selectedMesh = -1;
    private string _partSearch = "";
    private PreviewTexture? _openTexture;
    private int _captureScale = 2;

    // Model workshop: the pieces of the open model chosen for removal.
    private ModelPartTool? _workshop;
    private string? _workshopError;
    private ViewerItem? _workshopFor;
    private readonly WorkshopGizmo _gizmo = new();
    private Vector3 _moveBy, _turnBy; // Exact move of the chosen piece: metres, degrees.

    // Adding a piece in the workshop: shape (0 cube, 1 flat, 2 the copied piece), material, size, texture repeated each
    // metre. The copied piece is kept from one model to the next.
    private int _addShape, _addMaterial = -1;
    private Vector3 _addSize = Vector3.One;
    private bool _addPerMetre = true;
    private static CopiedPiece? _copiedPiece;
    private int _workshopRevision = -1;
    private (ViewerItem Item, object? Reader)? _reopen;
    private bool _captureTransparent;

    // Opening cut through a wall: placed by a click on the model (while _openingPlacing), then moved by exact values
    // (right, up) and sized before the cut.
    private bool _openingPlacing;
    private Opening? _openingPlaced;
    private Vector2 _openingShift;
    private float _openingWidth = Opening.DefaultWidth, _openingHeight = Opening.DefaultHeight, _openingDepth = Opening.DefaultDepth;

    private Opening? CurrentOpening => _openingPlaced is { } o
        ? o with
        {
            Bottom = o.Bottom + o.Right * _openingShift.X + new Vector3(0, 0, _openingShift.Y),
            Width = _openingWidth,
            Height = _openingHeight,
            Depth = _openingDepth,
        }
        : null;

    // Image being converted for a texture of the workshop (on a background thread).
    private sealed record TextureJob(ModelPartTool Tool, string Source, uint NameHash, byte[]? Pixels, string? Note, string? Error)
    {
        // The texture at its new size (size change instead of a new image).
        public Data.Textures.TextureData? Resized { get; init; }
    }

    // The texture whose size is being chosen (popup), read from its file.
    private (ModelPartTool Tool, string Source, Data.Textures.TextureData Texture)? _resizing;
    private Task<TextureJob>? _textureJob;
    // Texture dictionaries of the open model already changed in the project (asked once per model).
    private readonly Dictionary<string, bool> _changedSources = new(StringComparer.OrdinalIgnoreCase);

    public void OpenArchetype(uint archetypeHash)
    {
        if (Library?.GetArchetype(archetypeHash) is { } archetype)
            OpenArchetype(archetype);
    }

    // A type as read from its file (it may not be the one the loaded map uses).
    public bool OpenArchetype(Archetype archetype)
    {
        if (ItemOf(archetype) is not { } item)
            return false;
        Open(item);
        _filter = 1;
        _search = "";
        _scrollToItem = true;
        return true;
    }

    private ViewerItem? ItemOf(Archetype archetype)
    {
        if (Models?.Locate(archetype) is not { } location)
            return null;
        uint archetypeHash = archetype.NameHash;
        string name = Names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        if (state.IsOtherGame && !name.StartsWith("0x", StringComparison.Ordinal))
            name = name.ToLowerInvariant();
        return new ViewerItem(name, ViewerKind.Object, location);
    }

    // ---- Workshop tab (beside the map) ----

    public string? WorkshopName => state.WorkshopViewer.Item?.Name;

    public bool WorkshopChanged => _workshop is { ChangeCount: > 0 } && _workshopFor is not null && _workshopFor == state.WorkshopViewer.Item;

    // The model of a map object in the workshop tab.
    public void OpenWorkshop(MapEntity entity)
    {
        if (state.IsOtherGame)
            return;
        if (Library?.GetArchetype(entity.ArchetypeHash) is not { } archetype || ItemOf(archetype) is not { } item)
        {
            state.Notify(L("Modèle introuvable pour ce type (aucun fichier .ydr / .ydd / .yft de ce nom)"), warning: true);
            return;
        }
        OpenWorkshop(item, entity);
    }

    private void OpenWorkshop(ViewerItem item, MapEntity? entity = null)
    {
        state.WorkshopFront = true;
        var viewer = state.WorkshopViewer;
        if (state.WorkshopOpen && viewer.Item == item)
        {
            if (entity is not null && entity != state.WorkshopEntity)
                SetWorkshopEntity(entity);
            return;
        }
        if (WorkshopChanged)
        {
            state.Notify(L($"Enregistrez ou abandonnez d'abord les modifications de {viewer.Item!.Name} (onglet Atelier)."), warning: true);
            return;
        }
        CloseWorkshop();
        viewer.Open(item, state.World.ModelReader, state.World.Textures, state.World.TextureReader);
        state.WorkshopOpen = true;
        SetWorkshopEntity(entity);
    }

    // Other objects of the map placed where the model is (a fix laid over it, a copy): on the map they may hide parts
    // of it, alone in the workshop they don't.
    private List<string> _workshopCovers = [];

    private void SetWorkshopEntity(MapEntity? entity)
    {
        state.WorkshopEntity = entity;
        _workshopCovers = [];
        if (entity is null || state.World.Maps is not { } maps)
            return;
        var at = entity.Position;
        _workshopCovers = maps.EntitiesAround(new Vector2(at.X, at.Y), 2f)
            .Where(e => e != entity && !e.IsDeleted && e.ArchetypeHash != entity.ArchetypeHash && Vector3.Distance(e.Position, at) < CoverDistance)
            .Select(e => Names?.Describe(e.ArchetypeHash) ?? $"0x{e.ArchetypeHash:X8}")
            .Distinct()
            .ToList();
    }

    private const float CoverDistance = 0.5f; // Metres between the two objects' origins.

    // Closes the workshop tab; its changes not saved are dropped.
    public void CloseWorkshop()
    {
        _workshop = null;
        _workshopFor = null;
        _reopen = null;
        _openingPlaced = null;
        _openingPlacing = false;
        _gizmo.Cancel();
        state.WorkshopViewer.OverlayEdges.Clear();
        state.WorkshopViewer.Model.Close();
        state.WorkshopOpen = false;
        state.WorkshopShown = false;
        state.WorkshopCoversMap = false;
        state.WorkshopEntity = null;
        _workshopCovers = [];
    }

    // Once per frame, before the viewer and the workshop are drawn: each view asks for its image again while shown.
    public void BeginFrame()
    {
        state.Viewer.RequestedSize = Vector2.Zero;
        state.WorkshopViewer.RequestedSize = Vector2.Zero;
        _inWorkshop = true;
        ReopenChangedModel();
        _inWorkshop = false;
    }

    // The workshop's tools: chosen in the bar over its view (or keys 1 to 5), their settings in Properties.
    private enum WorkshopTool { Choose, Move, Open, Add, Textures }

    private WorkshopTool _workshopTool;
    private string? _workshopStatus; // Why there is no tool (map reloading, model unreadable).

    private static string[] WorkshopToolNames =>
        [L($"{Icons.MousePointer}  Choisir"), L($"{Icons.Move}  Déplacer"), L($"{Icons.DoorOpen}  Percer"), L($"{Icons.Box}  Ajouter"), L($"{Icons.Layers}  Textures")];

    private static string[] WorkshopToolTips =>
    [
        L("Choisir des morceaux (clic, Ctrl+clic), les retirer, les dupliquer, les copier pour un autre modèle (touche 1)"),
        L("Déplacer ou tourner le morceau choisi avec les poignées ou des valeurs exactes (touche 2)"),
        L("Percer une ouverture dans un mur, pour une porte dessinée (touche 3)"),
        L("Ajouter un cube, un plan ou un morceau copié d'un autre modèle (touche 4)"),
        L("Remplacer, exporter ou redimensionner les textures du modèle (touche 5)"),
    ];

    // The workshop tab: its tools' bar, then the model large.
    public void DrawWorkshop()
    {
        _inWorkshop = true;
        try
        {
            if (Viewer.Item is { } item)
                UpdateWorkshop(item);
            int chosen = (int)_workshopTool;
            if (ImGui.BeginChild("##workshop_tools_bar", new Vector2(Math.Min(ImGui.GetContentRegionAvail().X, 620f), ImGui.GetFrameHeight()),
                    ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar))
            {
                if (Widgets.Segmented("workshop_tool", WorkshopToolNames, ref chosen, WorkshopToolTips))
                    ChooseWorkshopTool((WorkshopTool)chosen);
            }
            ImGui.EndChild();
            if (ImGui.BeginChild("##workshop_view", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                DrawView();
            ImGui.EndChild();
        }
        finally
        {
            _inWorkshop = false;
        }
    }

    private void ChooseWorkshopTool(WorkshopTool tool)
    {
        if (tool == _workshopTool)
            return;
        _workshopTool = tool;
        _openingPlacing = false;
        _gizmo.Cancel();
    }

    // What the mouse and the keys do in the workshop with the tool in use, for the status bar.
    public IReadOnlyList<(string Keys, string Action)> WorkshopHints()
    {
        (string, string) look = (L("Clic droit maintenu"), L("regarder autour"));
        (string, string) frame = ("F", L("cadrer le modèle"));
        (string, string) tools = ("1 - 5", L("outils"));
        return _workshopTool switch
        {
            WorkshopTool.Move => [(L("Tirer une poignée"), L("déplacer ou tourner le morceau")), ("Ctrl", L("par pas")), (L("Clic"), L("choisir un morceau")), look, frame, tools],
            WorkshopTool.Open => [(L("Clic sur le mur"), L("placer l'ouverture")), look, (L("Clic gauche glissé"), L("tourner autour")), frame, tools],
            _ => [(L("Clic"), L("choisir un morceau")), (L("Ctrl+clic"), L("ajouter / retirer")), look, (L("Clic gauche glissé"), L("tourner autour")),
                  (L("Molette"), L("zoom")), frame, tools],
        };
    }

    // Open a building: the workshop on the cutting tool (the opening is then placed by a click on the wall).
    public void ShowOpeningTool() => ChooseWorkshopTool(WorkshopTool.Open);

    // The building being opened (Properties > Ouvrir ce bâtiment) when it is the workshop's object: the Percer tool
    // places its opening and shows its settings (drawn by Properties).
    private BuildingOpeningTool? OpeningBuilding =>
        state.OpeningTool is { } building && building.Building == state.WorkshopEntity ? building : null;

    public Action<MapEntity>? BuildingOpeningContent { get; set; }

    private int _openingBuildingRevision = -1;

    // The building's opening cut in the workshop's model only, to be looked at before opening (one change, undone
    // when the opening moves or the opening stops).
    private bool _previewCut;
    private int _previewRevision = -1;

    private void DrawOpeningPreview(ViewerItem item, ModelPartTool tool, BuildingOpeningTool building, float width)
    {
        if (building.Current is not { } opening)
            return;
        if (_previewCut)
        {
            Widgets.Hint(L("Aperçu : le modèle est percé seulement ici, rien n'est enregistré. « Carte autour » montre les objets voisins."), wrap: true);
            if (Widgets.Button(L($"{Icons.Undo}  Retirer l'aperçu"), null, width))
                UndoOpeningPreview(item, tool);
            return;
        }
        if (Widgets.Button(L($"{Icons.Eye}  Aperçu de la découpe"),
                L("Perce le modèle dans l'Atelier seulement, pour voir ce qui disparaît avant d'ouvrir le bâtiment."), width)
            && Matrix4x4.Invert(building.Building.Transform, out var toModel))
        {
            var (_, error) = tool.CutOpening(NodeRunner.Editor.BuildingOpening.ToModel(opening, toModel));
            if (error is not null)
                state.Notify(error, warning: true);
            else
            {
                _previewCut = true;
                _previewRevision = building.Revision;
                state.WorkshopShowsMap = true;
                ShowWorkshopChanges(item, tool);
            }
        }
    }

    private void UndoOpeningPreview(ViewerItem item, ModelPartTool tool)
    {
        _previewCut = false;
        if (tool.UndoLast())
            ShowWorkshopChanges(item, tool);
    }

    public void OpenFile(string path)
    {
        if (path.EndsWith(".awc", StringComparison.OrdinalIgnoreCase))
        {
            OpenSoundFile(path);
            return;
        }
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var kind = ViewerCatalog.IsVehicleFile(path) ? ViewerKind.Vehicle : ViewerKind.Object;
        uint hash = Joaat.Hash(name);
        // Textures: those of the object type of that name, a vehicle's dictionary of its name.
        uint textures = Library?.GetArchetype(hash)?.TextureDictionaryHash ?? (kind == ViewerKind.Vehicle ? hash : 0);
        Open(new ViewerItem(name, kind, new ModelLocation(path, hash, textures)));
        _filter = kind == ViewerKind.Vehicle ? 0 : 1;
        _search = "";
        _scrollToItem = true;
    }

    private void Open(ViewerItem item)
    {
        ApplySavedSettings();
        if (item != Viewer.Item)
        {
            _selectedMesh = -1;
            _openTexture = null;
        }
        if (!_inWorkshop)
        {
            AddRecent(item);
            AddToHistory(item);
        }
        if (state.Other is { } other)
            Viewer.Open(item, other.ModelReader, other.Textures, other.TextureReader);
        else
            Viewer.Open(item, state.World.ModelReader, state.World.Textures, state.World.TextureReader);
    }

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(1240f, 700f), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool visible = ImGui.Begin(L(Title), ref open);
        ImGui.PopStyleVar();
        if (visible)
        {
            ApplySavedSettings();
            UpdateCatalog();
            RestoreLastModel();
            UpdatePaint();
            ShowCaptureResult();
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float available = ImGui.GetContentRegionAvail().X;
            bool inspector = available > ListWidth + InspectorWidth + 300f;
            if (ImGui.BeginChild("##viewer_list", new Vector2(ListWidth, 0), ImGuiChildFlags.None))
                DrawList();
            ImGui.EndChild();
            ImGui.SameLine();
            float viewWidth = inspector ? available - ListWidth - InspectorWidth - spacing * 2 : 0f;
            bool sounds = _filter == SoundsFilter;
            if (!sounds)
                PauseSound();
            if (ImGui.BeginChild("##viewer_view", new Vector2(viewWidth, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (sounds)
                    DrawSoundBank();
                else
                    DrawView();
            }
            ImGui.EndChild();
            if (inspector)
            {
                ImGui.SameLine();
                if (ImGui.BeginChild("##viewer_inspector", Vector2.Zero, ImGuiChildFlags.None))
                {
                    if (sounds)
                        DrawSoundInspector();
                    else
                        DrawInspector();
                }
                ImGui.EndChild();
            }
            RememberSettings();
            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput)
                HandleCatalogKeys();
        }
        ImGui.End();
    }

    private void UpdateCatalog()
    {
        var library = Library;
        if (library != _catalogLibrary)
        {
            _catalogLibrary = library;
            _catalog = null;
            var world = state.World;
            var scene = state.Gta4.Scene;
            var classic = state.Classic.Scene;
            _building = library is null ? null
                : IsGta4 && scene is not null ? Task.Run(() => ViewerCatalog.BuildGta4(scene))
                : state.IsClassic && classic is not null ? Task.Run(() => ViewerCatalog.BuildClassic(classic))
                : Task.Run(() => ViewerCatalog.Build(world));
        }
        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            _catalog = finished.Result;
        }
    }

    // ---- Paint ----

    // GTA V's vehicle sheets (none in GTA IV).
    private VehicleData? Vehicles => state.IsOtherGame ? null : state.World.Vehicles;

    private VehicleVariation? Variation =>
        Viewer.Item is { Kind: ViewerKind.Vehicle } item ? Paints?.VariationOf(item.Location.ModelHash) : null;

    // GTA IV gives its colours by number (carcols.dat: colours 1 to 4).
    private string SlotName(VehiclePaintSlot slot) => IsGta4 ? L($"Couleur {(int)slot}") : ShaderNames.Slot(slot);

    private int SlotCount => IsGta4 ? 4 : PaintSlots.Length;

    // A vehicle opened: its first planned combination, once the palette is read.
    private void UpdatePaint()
    {
        var item = Viewer.Item;
        if (item?.Kind != ViewerKind.Vehicle)
        {
            Viewer.PaintColors = null;
            return;
        }
        if (Paints is not { } paints)
        {
            Viewer.PaintColors = null;
            return;
        }
        if (_paintFor != item)
        {
            _paintFor = item;
            _paintSet = -1;
            if (paints.VariationOf(item.Location.ModelHash) is { ColorSets.Count: > 0 })
                ApplySet(0);
            else
                DefaultPaint.CopyTo(_paint, 0);
        }
        Viewer.PaintColors = _paintOn ? VehiclePaintColors.From(paints, _paint) : null;
    }

    private void ApplySet(int index)
    {
        if (Variation is not { } variation || index < 0 || index >= variation.ColorSets.Count)
            return;
        var set = variation.ColorSets[index];
        for (int i = 0; i < _paint.Length; i++)
            _paint[i] = set[i] >= 0 ? set[i] : DefaultPaint[i];
        _paintSet = index;
    }

    private static Vector3 Display(VehicleColor color) => VehiclePaintColors.Display(color);

    // ---- List ----

    private void DrawList()
    {
        string[] labels = _catalog is { } c
            ? [$"{Filters[0]} ({Widgets.Count(c.Vehicles.Count)})", $"{Filters[1]} ({Widgets.Count(c.Objects.Count)})", $"{Filters[2]} ({Widgets.Count(c.Library.Count)})", Filters[3]]
            : Filters;
        if (state.IsOtherGame)
        {
            // No prop library in the other games.
            _filter = Math.Min(_filter, 1);
            Widgets.Segmented("viewer_filter_gta4", labels[..2], ref _filter,
                [IsGta4 ? L("Tous les véhicules du jeu ou de l'épisode choisi (vehicles.img)") : L("Tous les véhicules du jeu (sections cars des .ide)"),
                 L("Tous les types d'objets de la carte (définitions .ide)")]);
        }
        else
        {
            Widgets.Segmented("viewer_filter", labels, ref _filter,
                [L("Tous les véhicules du jeu, DLC compris (nom de spawn)"), L("Tous les types d'objets de la carte (archétypes)"),
                 L($"Props addons de ta bibliothèque, utilisables dans tous les projets :\n{state.LibraryFolder}\nÀ l'export FiveM, seuls les fichiers des props placés sont copiés dans la ressource."),
                 L("Tous les sons du jeu (fichiers .awc), à écouter ici")]);
        }
        bool sounds = _filter == SoundsFilter;
        float starWidth = sounds ? 0f : ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        Widgets.SearchField("##viewer_search", _filter switch
        {
            0 => state.IsOtherGame ? L("Véhicule (ex. infernus)") : L("Nom, marque ou hash (ex. adder, pegassi)"),
            SoundsFilter => L("Fichier de sons (ex. doors, ambience)"),
            1 => IsGta4 ? L("Type (ex. bm_nylamp1)") : state.IsClassic ? L("Type (ex. palm)") : L("Type ou hash (ex. prop_bench)"),
            _ => L("Prop ou pack"),
        }, ref _search, width: ImGui.GetContentRegionAvail().X - starWidth);
        if (!sounds)
        {
            ImGui.SameLine();
            if (Widgets.IconButton(Icons.Star, "viewer_shortlist", L("Favoris et récents : seulement les modèles mis en favori (clic droit sur une ligne, ou étoile sous la vue), puis les derniers ouverts"), active: _shortList))
                _shortList = !_shortList;
        }
        if (_filter == 2)
            DrawLibraryButtons();
        if (_filter == SoundsFilter)
        {
            DrawSoundList();
            return;
        }

        if (_catalog is null)
        {
            Widgets.Hint(Library is null ? L("En attente du chargement de la carte...") : L("Préparation de la liste..."));
            return;
        }

        UpdateRows();
        if (_filter == 2 && _catalog.Library.Count == 0)
        {
            Widgets.Hint(L("Bibliothèque vide. Ajoute un pack de props addons (dossier d'une ressource FiveM ou .zip avec ses .ytyp, .ydr et .ytd) : ses props seront disponibles dans tous les projets."), wrap: true);
            return;
        }
        if (_shortList)
            Widgets.Hint(_rows.Count == 0 ? L("Aucun favori ni modèle récent ici. Clic droit sur une ligne : Ajouter aux favoris.") : L($"{Icons.Star} favoris, puis derniers ouverts"), wrap: true);
        else
            Widgets.Hint(_rows.Count == 0 ? L("Aucun résultat") : L($"{Widgets.Count(_rows.Count)} modèle(s)"));
        if (!ImGui.BeginChild("##viewer_rows", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        if (_scrollToItem)
        {
            _scrollToItem = false;
            int index = _rows.FindIndex(r => r.Location == Viewer.Item?.Location);
            if (index >= 0)
                ImGui.SetScrollY(Math.Max(0, index * rowHeight - ImGui.GetWindowHeight() * 0.4f));
        }

        // Up / down arrows: previous / next model of the list.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput && _rows.Count > 0)
        {
            int current = _rows.FindIndex(r => r.Location == Viewer.Item?.Location);
            int next = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? current + 1 : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? current - 1 : current;
            if (next != current && next >= 0 && next < _rows.Count)
            {
                Open(_rows[next]);
                _scrollToItem = true;
            }
        }

        var vehicles = Vehicles?.Info;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(_rows.Count, rowHeight);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var item = _rows[i];
                ImGui.PushID(i);
                string icon = item.Kind == ViewerKind.Vehicle ? Icons.Car : item.Pack is null ? Icons.Box : Icons.Package;
                if (ImGui.Selectable($"{icon}  {item.Name}", item.Location == Viewer.Item?.Location))
                    Open(item);
                DrawFavoriteMenu(item);
                float right = ImGui.GetItemRectMax().X - 4f;
                if (IsFavorite(item))
                {
                    float star = ImGui.CalcTextSize(Icons.Star).X;
                    right -= star;
                    ImGui.GetWindowDrawList().AddText(new Vector2(right, ImGui.GetItemRectMin().Y), ImGui.GetColorU32(new Vector4(1f, 0.78f, 0.25f, 1f)), Icons.Star);
                    right -= 6f;
                }
                if (_filter == 2 && !string.IsNullOrEmpty(item.Pack))
                {
                    // Pack name, dimmed, on the right of the row.
                    string pack = Widgets.Fit(item.Pack, ImGui.GetItemRectSize().X * 0.45f);
                    float width = ImGui.CalcTextSize(pack).X;
                    ImGui.GetWindowDrawList().AddText(new Vector2(right - width, ImGui.GetItemRectMin().Y),
                        ImGui.GetColorU32(UiTheme.TextMuted), pack);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                {
                    string tip = item.Pack is { } from
                        ? L($"Pack : {(from.Length == 0 ? L("(racine de la bibliothèque)") : from)}\n{state.World.ProjectFiles?.LibraryFileOf(item.Location.Path)?.Disk ?? item.Location.Path}")
                        : item.Location.Path;
                    if (item.Kind == ViewerKind.Vehicle && vehicles?.VehicleOf(item.Location.ModelHash) is { Count: > 0 } sheet)
                        tip = $"{VehicleInfoCatalog.Value(sheet, "vehicleMakeName")} {VehicleInfoCatalog.Value(sheet, "gameName")} · {ClassName(VehicleInfoCatalog.Value(sheet, "vehicleClass"))}\n{tip}";
                    ImGui.SetTooltip(tip);
                }
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    private void UpdateRows()
    {
        if (_rowsBuiltFor == (_filter, _search, _catalog, _shortList, _favoritesRevision))
            return;
        _rowsBuiltFor = (_filter, _search, _catalog, _shortList, _favoritesRevision);
        string search = _search.Trim().ToLowerInvariant();
        if (_shortList)
        {
            _rows = ShortList(search);
            return;
        }
        var source = _filter switch { 0 => _catalog!.Vehicles, 1 => _catalog!.Objects, _ => _catalog!.Library };
        var words = SearchWords(search);
        _rows = words.Length == 0
            ? source.ToList()
            : source.Where(r => Matches(r, words))
                .OrderBy(r => r.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
    }

    private static string[] SearchWords(string search) => search.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // Every word must be found: in the name, the pack, a vehicle's make and name in the game, or as the model's hash
    // (0x1234ABCD, or the number a FiveM script prints).
    private bool Matches(ViewerItem item, string[] words)
    {
        foreach (string word in words)
        {
            if (HashOf(word) is { } hash)
            {
                if (item.Location.ModelHash != hash && item.ArchetypeHash != hash)
                    return false;
                continue;
            }
            if (item.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || item.Pack?.Contains(word, StringComparison.OrdinalIgnoreCase) == true)
                continue;
            if (item.Kind == ViewerKind.Vehicle && VehicleWords(item).Contains(word, StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }
        return true;
    }

    private static uint? HashOf(string word)
    {
        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(word.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return hex;
        // Model names are never all digits: a long number is a hash, signed as GetHashKey gives it.
        if (word.Length >= 6 && int.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int signed))
            return unchecked((uint)signed);
        if (word.Length >= 6 && uint.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out uint plain))
            return plain;
        return null;
    }

    // Make and name in the game of a vehicle (vehicles.meta), e.g. "PEGASSI ZENTORNO".
    private string VehicleWords(ViewerItem item)
    {
        if (state.IsOtherGame || Vehicles?.Info.VehicleOf(item.Location.ModelHash) is not { Count: > 0 } sheet)
            return "";
        return $"{VehicleInfoCatalog.Value(sheet, "vehicleMakeName")} {VehicleInfoCatalog.Value(sheet, "gameName")}";
    }

    public void ShowLibrary()
    {
        ApplySavedSettings();
        _filter = 2;
        _search = "";
    }

    private void DrawLibraryButtons()
    {
        if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Ajouter un pack...##library_add"), L("Copie un pack (dossier d'une ressource FiveM, dossier stream) dans la bibliothèque, puis recharge la carte.")))
            state.LibraryRequest = LibraryAction.AddFolder;
        ImGui.SameLine(0, 4f);
        if (Widgets.Button(L(".zip...##library_zip"), L("Ajoute un pack livré en archive .zip (un .rar ou un .7z est à décompresser d'abord).")))
            state.LibraryRequest = LibraryAction.AddZip;
        ImGui.SameLine(0, 4f);
        if (Widgets.IconButton(Icons.FolderOpen, "library_open", L("Ouvrir le dossier de la bibliothèque (après y avoir copié ou retiré un pack à la main : bouton Recharger)")))
            OpenLibraryFolder(state);
        ImGui.SameLine(0, 4f);
        if (Widgets.IconButton(Icons.RefreshCw, "library_reload", L("Relire la bibliothèque (recharge la carte)")))
            state.LibraryRequest = LibraryAction.Reload;
    }

    public static void OpenLibraryFolder(AppState state)
    {
        try
        {
            Directory.CreateDirectory(state.LibraryFolder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{state.LibraryFolder}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            state.Notify(L($"Dossier de la bibliothèque inaccessible : {ex.Message}"), warning: true);
        }
    }

    // ---- View ----

    private void DrawView()
    {
        DrawViewToolbar();
        float infoHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var size = ImGui.GetContentRegionAvail() - new Vector2(0, infoHeight);
        size = new Vector2(MathF.Floor(Math.Max(size.X, 2)), MathF.Floor(Math.Max(size.Y, 2)));
        Viewer.RequestedSize = size * UiTheme.Scale; // In pixels, for a sharp image.

        ImGui.InvisibleButton("##viewer_image", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        if (Viewer.ImageId != IntPtr.Zero)
            drawList.AddImage(Viewer.ImageId, min, max);
        else
            drawList.AddRectFilled(min, max, Widgets.Col(UiTheme.Field));
        UpdateWorkshopGizmo(min, size);
        HandleMouse(size);
        if (_inWorkshop && (ImGui.IsItemHovered() || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)))
            FlyWithKeys();

        // Name, make and class, then loading / error, over the top-left corner of the image.
        var pos = min + new Vector2(12, 10);
        bool light = Viewer.Background == 2;
        var text = light ? new Vector4(0.08f, 0.08f, 0.1f, 1f) : UiTheme.Text;
        var muted = light ? new Vector4(0.25f, 0.26f, 0.3f, 1f) : UiTheme.TextMuted;
        if (Viewer.Item is { } item)
        {
            ImGui.PushFont(UiTheme.TitleFont);
            drawList.AddText(pos, Widgets.Col(text), item.Name);
            pos.Y += ImGui.GetTextLineHeight() + 2;
            ImGui.PopFont();
            if (Subtitle(item) is { } subtitle)
            {
                drawList.AddText(pos, Widgets.Col(muted), subtitle);
                pos.Y += ImGui.GetTextLineHeight() + 2;
            }
        }
        string? status = Viewer.IsLoading ? L($"{Icons.LoaderCircle}  Chargement...")
            : Viewer.Error is { } error ? L($"{Icons.TriangleAlert}  Impossible d'afficher ce modèle : {error}")
            : Viewer.Item is null ? L("Choisis un véhicule ou un objet dans la liste.") : null;
        if (status is not null)
            drawList.AddText(pos, Widgets.Col(Viewer.Error is not null ? UiTheme.Error : muted), status);
        if (Viewer.TriangleCount > 0)
        {
            var s = Viewer.Size;
            string dims = L($"L {s.X:0.00} m · l {s.Y:0.00} m · h {s.Z:0.00} m");
            drawList.AddText(new Vector2(max.X - ImGui.CalcTextSize(dims).X - 12, min.Y + 10), Widgets.Col(muted), dims);
        }
        DrawViewHelp(drawList, new Vector2(min.X + 20f, max.Y - 20f), light ? muted : UiTheme.TextFaint);

        DrawInfo();
    }

    // A "?" in the corner of the view: the mouse and keys when hovered.
    private void DrawViewHelp(ImDrawListPtr drawList, Vector2 center, Vector4 color)
    {
        const float radius = 9f;
        bool hovered = Vector2.DistanceSquared(ImGui.GetIO().MousePos, center) <= (radius + 3f) * (radius + 3f)
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        uint col = Widgets.Col(hovered ? UiTheme.Text : color);
        drawList.AddCircle(center, radius, col, 0, 1.5f);
        var size = ImGui.CalcTextSize("?");
        drawList.AddText(center - size * 0.5f, col, "?");
        if (!hovered)
            return;
        string text;
        if (_inWorkshop)
        {
            text = L("Touches de la carte : avancer, monter (Maj : plus vite)\nClic droit maintenu : regarder\nClic gauche glissé : tourner autour\nMolette : zoom\nF : cadrer");
        }
        else
        {
            var keys = state.Keys;
            string K(Input.KeyAction action) => keys.Text(action) is { Length: > 0 } key ? key : "-";
            text = L("Clic gauche glissé : tourner\nClic droit ou molette enfoncée : déplacer\nMolette : zoom\nDouble-clic : vue par défaut")
                + "\n\n" + L($"{K(Input.KeyAction.FrameModel)} : cadrer · 1 à 8 : vues prédéfinies\n{K(Input.KeyAction.ViewerSpin)} : rotation · {K(Input.KeyAction.ViewerGround)} : sol · {K(Input.KeyAction.ViewerBounds)} : boîte\n{K(Input.KeyAction.ViewerBack)} / {K(Input.KeyAction.ViewerForward)} : modèle précédent / suivant\nFlèches haut / bas dans la liste : modèle précédent / suivant de la liste");
        }
        ImGui.SetTooltip(text);
    }

    private string? Subtitle(ViewerItem item)
    {
        if (item.Kind == ViewerKind.Vehicle && IsGta4)
        {
            return state.Gta4.Vehicles?.Find(item.Location.ModelHash) is { } definition
                ? string.Join(" · ", new[] { Gta4TypeName(definition.Type), definition.HandlingId }.Where(t => t.Length > 0))
                : null;
        }
        if (item.Kind == ViewerKind.Vehicle)
        {
            if (Vehicles?.Info.VehicleOf(item.Location.ModelHash) is not { Count: > 0 } sheet)
                return null;
            string? make = VehicleInfoCatalog.Value(sheet, "vehicleMakeName");
            string? game = VehicleInfoCatalog.Value(sheet, "gameName");
            return string.Join(" · ", new[] { $"{make} {game}".Trim(), ClassName(VehicleInfoCatalog.Value(sheet, "vehicleClass")),
                TypeName(VehicleInfoCatalog.Value(sheet, "type")) }.Where(t => t.Length > 0));
        }
        if (Library?.GetArchetype(item.ArchetypeHash) is { } archetype)
            return L($"{KindName(archetype.Kind)} · distance d'affichage {archetype.LodDistance:0} m");
        return null;
    }

    private void DrawViewToolbar()
    {
        bool enabled = Viewer.Item is not null;
        if (!_inWorkshop)
        {
            string back = state.Keys.Text(Input.KeyAction.ViewerBack), forward = state.Keys.Text(Input.KeyAction.ViewerForward);
            if (Widgets.IconButton(Icons.ChevronLeft, "viewer_back", back.Length > 0 ? L($"Modèle précédent ({back})") : L("Modèle précédent"), enabled: _back.Count > 0))
                GoBack();
            ImGui.SameLine(0, 2);
            if (Widgets.IconButton(Icons.ChevronRight, "viewer_forward", forward.Length > 0 ? L($"Modèle suivant ({forward})") : L("Modèle suivant"), enabled: _forward.Count > 0))
                GoForward();
            ImGui.SameLine();
        }
        int shading = (int)Viewer.Shading;
        string[] shadings = [L($"{Icons.Box}  Textures"), L($"{Icons.Mountain}  Argile"), L($"{Icons.Grid}  Fil de fer")];
        if (ImGui.BeginChild("##viewer_shading", new Vector2(300f, ImGui.GetFrameHeight()), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar))
        {
            if (Widgets.Segmented("viewer_shading_mode", shadings, ref shading,
                    [L("Le modèle avec ses textures (et sa peinture)"), L("Sans texture : seulement les formes et la lumière"),
                     L("Seulement les arêtes des triangles")]))
                Viewer.Shading = (ViewerShading)shading;
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Waypoints, "viewer_edges", L("Arêtes des triangles sur le modèle"), active: Viewer.Edges))
            Viewer.Edges = !Viewer.Edges;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Grid, "viewer_ground", WithKey(L("Sol quadrillé sous le modèle (carreaux de 1 m, 5 m pour les grands modèles)"), Input.KeyAction.ViewerGround), active: Viewer.ShowGround))
            Viewer.ShowGround = !Viewer.ShowGround;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Ruler, "viewer_bounds", WithKey(L("Boîte englobante du modèle (ses dimensions)"), Input.KeyAction.ViewerBounds), active: Viewer.ShowBounds))
            Viewer.ShowBounds = !Viewer.ShowBounds;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Rotate, "viewer_spin", WithKey(L("Rotation automatique (vitrine)"), Input.KeyAction.ViewerSpin), active: Viewer.AutoRotate))
            Viewer.AutoRotate = !Viewer.AutoRotate;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Sun, "viewer_light", L("Lumière : réglages"), active: !Viewer.LightFollowsCamera))
            ImGui.OpenPopup("##viewer_light_popup");
        DrawLightPopup();
        if (_inWorkshop)
        {
            ImGui.SameLine(0, 2);
            bool canShow = state.WorkshopEntity is not null;
            if (Widgets.ToolbarButton(Icons.Map, L("Carte autour"), canShow
                    ? L("Carte autour : les objets voisins de la carte, en gris, à leur place autour du modèle")
                    : L("Carte autour : seulement pour un modèle ouvert depuis un objet de la carte (double-clic, clic droit, Propriétés)"),
                    enabled: canShow, active: canShow && state.WorkshopShowsMap))
                state.WorkshopShowsMap = !state.WorkshopShowsMap;
        }
        Widgets.ToolbarSeparator();

        if (Widgets.ToolbarButton(Icons.Camera, L("Vue"), _inWorkshop ? L("Vues prédéfinies et champ de vision") : L("Vues prédéfinies (touches 1 à 8) et champ de vision"), enabled))
            ImGui.OpenPopup("##viewer_views");
        DrawViewsPopup();
        ImGui.SameLine();
        if (Widgets.ToolbarButton(Icons.Layers, L("Fond"), L("Couleur du fond (vert : pour l'incrustation dans une vidéo)")))
            ImGui.OpenPopup("##viewer_background");
        if (ImGui.BeginPopup("##viewer_background"))
        {
            for (int i = 0; i < ModelViewer.Backgrounds.Length; i++)
            {
                var (name, color) = ModelViewer.Backgrounds[i];
                ImGui.ColorButton($"##bg{i}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine();
                if (ImGui.Selectable(L(name), Viewer.Background == i))
                    Viewer.Background = i;
            }
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (Widgets.ToolbarButton(Icons.Save, L("Capture"), L("Enregistrer l'image de la vue en PNG"), enabled && Viewer.TriangleCount > 0))
            ImGui.OpenPopup("##viewer_capture");
        DrawCapturePopup();
    }

    // In the catalogue, a button's tooltip gives its key.
    private string WithKey(string tooltip, Input.KeyAction action) =>
        !_inWorkshop && state.Keys.Text(action) is { Length: > 0 } key ? $"{tooltip} ({key})" : tooltip;

    private void DrawLightPopup()
    {
        if (!ImGui.BeginPopup("##viewer_light_popup"))
            return;
        bool follow = Viewer.LightFollowsCamera;
        if (ImGui.Checkbox(L("Lumière qui suit la caméra"), ref follow))
            Viewer.LightFollowsCamera = follow;
        ImGui.BeginDisabled(follow);
        float angle = Viewer.LightAngle * 180f / MathF.PI;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat(L("Direction##light_angle"), ref angle, -180f, 180f, "%.0f°"))
            Viewer.LightAngle = angle * MathF.PI / 180f;
        ImGui.EndDisabled();
        float height = Viewer.LightHeight;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat(L("Hauteur##light_height"), ref height, 0.05f, 3f, "%.2f"))
            Viewer.LightHeight = height;
        ImGui.EndPopup();
    }

    // A vehicle faces +Y: yaw 0 looks along +Y (from behind). Keys 1 to 8 in the viewer.
    private static (string Name, float Yaw, float Pitch)[] Views =>
    [
        (L("Trois quarts avant"), -0.93f, -0.32f), (L("Trois quarts arrière"), 0.93f + MathF.PI, -0.32f),
        (L("Avant"), MathF.PI, -0.05f), (L("Arrière"), 0f, -0.05f), (L("Gauche"), -MathF.PI / 2f, -0.05f),
        (L("Droite"), MathF.PI / 2f, -0.05f), (L("Dessus"), MathF.PI, -1.5f), (L("Dessous"), MathF.PI, 1.5f),
    ];

    private void DrawViewsPopup()
    {
        if (!ImGui.BeginPopup("##viewer_views"))
            return;
        var views = Views;
        for (int i = 0; i < views.Length; i++)
        {
            var (name, yaw, pitch) = views[i];
            if (ImGui.Selectable(name, false, ImGuiSelectableFlags.None, new Vector2(200f, 0)))
                Viewer.SetView(yaw, pitch);
            // Its key, on the right of the row.
            string key = _inWorkshop ? "" : $"{i + 1}";
            ImGui.SameLine(200f - ImGui.CalcTextSize(key).X + ImGui.GetStyle().WindowPadding.X);
            ImGui.TextColored(UiTheme.TextFaint, key);
        }
        ImGui.Separator();
        float fov = Viewer.FieldOfViewDegrees;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat(L("Champ de vision"), ref fov, 10f, 90f, "%.0f°"))
            Viewer.FieldOfViewDegrees = fov;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Petit angle : effet téléobjectif, sans déformation (bien pour une vignette)"));
        ImGui.EndPopup();
    }

    private void DrawCapturePopup()
    {
        if (!ImGui.BeginPopup("##viewer_capture"))
            return;
        ImGui.TextColored(UiTheme.TextMuted, L("Taille de l'image"));
        var size = Viewer.RequestedSize;
        for (int scale = 1; scale <= 4; scale *= 2)
        {
            if (scale > 1)
                ImGui.SameLine();
            if (ImGui.RadioButton(L($"x{scale} ({(int)size.X * scale} x {(int)size.Y * scale})"), _captureScale == scale))
                _captureScale = scale;
        }
        ImGui.Checkbox(L("Fond transparent (sans le sol)"), ref _captureTransparent);
        ImGui.Spacing();
        string folder = CaptureFolder;
        if (Widgets.PrimaryButton(L($"{Icons.Save}  Enregistrer")) && Viewer.Item is { } item)
        {
            string file = $"{item.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            Viewer.RequestCapture(Path.Combine(folder, file), _captureScale, _captureTransparent);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.FolderOpen}  Ouvrir le dossier"), folder))
            OpenFolder(folder);
        Widgets.Hint(L($"Dans {folder}"));
        ImGui.EndPopup();
    }

    private static string CaptureFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NodeRunner Studio");

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.ErrorLog.Add(L("Visionneuse"), ex);
        }
    }

    private void ShowCaptureResult()
    {
        if (Viewer.CaptureResult is not { } result)
            return;
        Viewer.CaptureResult = null;
        state.Notify(result.Error ? L($"Capture impossible : {result.Text}") : L($"Image enregistrée : {result.Text}"), result.Error);
    }

    // Model workshop: handles on the chosen piece; the piece is moved when the mouse is released.
    private void UpdateWorkshopGizmo(Vector2 min, Vector2 size)
    {
        if (!_inWorkshop || _workshopTool != WorkshopTool.Move || _workshopFor != Viewer.Item || Viewer.Item is not { } item
            || _workshop is not { SelectionCenter: { } pivot } tool)
        {
            _gizmo.Cancel();
            return;
        }
        var move = _gizmo.Update(min, size, Viewer.LastViewProjection, Viewer.ModelOffset, Viewer.FieldOfViewDegrees, pivot,
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem));
        if (_gizmo.Preview is { } preview)
        {
            Viewer.OverlayEdges.Clear();
            Viewer.OverlayEdges.AddRange(tool.Edges(preview));
            _workshopRevision = -1;
        }
        if (move is { } done)
            MoveWorkshopPiece(item, tool, done);
    }

    private void MoveWorkshopPiece(ViewerItem item, ModelPartTool tool, Matrix4x4 move)
    {
        if (tool.MoveSelected(move) is { } error)
            state.Notify(error, warning: true);
        else
            ShowWorkshopChanges(item, tool);
        _workshopRevision = -1;
    }

    // Workshop: the map's keys move the camera (speed from the model's size, Shift faster), and frame the model.
    private void FlyWithKeys()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput || io.KeyCtrl || io.KeyAlt)
            return;
        var keys = state.Keys;
        if (keys.Pressed(Input.KeyAction.FrameModel))
            Viewer.Frame();
        var move = Vector3.Zero;
        if (keys.Held(Input.KeyAction.MoveForward)) move += Viewer.Forward;
        if (keys.Held(Input.KeyAction.MoveBackward)) move -= Viewer.Forward;
        if (keys.Held(Input.KeyAction.MoveRight)) move += Viewer.Right;
        if (keys.Held(Input.KeyAction.MoveLeft)) move -= Viewer.Right;
        if (keys.Held(Input.KeyAction.MoveUp)) move += Vector3.UnitZ;
        if (keys.Held(Input.KeyAction.MoveDown)) move -= Vector3.UnitZ;
        if (move == Vector3.Zero)
            return;
        // The model's size crossed in about two seconds.
        float speed = Math.Max(0.5f, Viewer.Radius) * (io.KeyShift ? 5f : 1f);
        Viewer.Fly(Vector3.Normalize(move) * speed * Math.Min(io.DeltaTime, 0.1f));
    }

    private void HandleMouse(Vector2 size)
    {
        var io = ImGui.GetIO();
        bool gizmo = _gizmo.IsDragging || _gizmo.IsHovered || _gizmo.JustReleased;
        if (ImGui.IsItemActive() && !gizmo)
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            {
                Viewer.Yaw -= io.MouseDelta.X * 0.008f;
                Viewer.Pitch -= io.MouseDelta.Y * 0.008f;
            }
            else if (_inWorkshop && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f))
            {
                // Workshop: right button held looks around, as on the map.
                float sensitivity = 0.004f * Math.Clamp(state.Settings.MouseSensitivity, MainForm.MinMouseSensitivity, MainForm.MaxMouseSensitivity);
                var delta = io.MouseDelta * UiTheme.Scale; // In pixels, as on the map.
                Viewer.Look(-delta.X * sensitivity, -delta.Y * sensitivity * (state.Settings.InvertMouseY ? -1f : 1f));
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f))
            {
                Viewer.PanBy(io.MouseDelta, size.Y);
            }
        }
        if (!ImGui.IsItemHovered())
            return;
        // Model workshop: a click without moving the mouse picks a piece.
        if (!gizmo && _inWorkshop && _workshopFor == Viewer.Item && _workshop is { } tool && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && io.MouseDragMaxDistanceSqr[0] < 16f
            && Matrix4x4.Invert(Viewer.LastViewProjection, out var inverse))
        {
            var at = (io.MousePos - ImGui.GetItemRectMin()) / size;
            float cx = at.X * 2f - 1f, cy = 1f - at.Y * 2f;
            // Reversed depth with an infinite far plane: 1 is the near plane.
            var near = Vector4.Transform(new Vector4(cx, cy, 1f, 1f), inverse);
            var far = Vector4.Transform(new Vector4(cx, cy, 0.5f, 1f), inverse);
            var from = new Vector3(near.X, near.Y, near.Z) / near.W;
            var to = new Vector3(far.X, far.Y, far.Z) / far.W;
            if (_workshopTool == WorkshopTool.Open && OpeningBuilding is { } building)
            {
                // Opening the building: the click goes to its tool, in map space.
                var placement = building.Building.Transform;
                var local = from - Viewer.ModelOffset;
                if (!building.Click(Vector3.Transform(local, placement), Vector3.Normalize(Vector3.TransformNormal(to - from, placement)), state.GroundHeightAt))
                    state.Notify(L("Cliquez sur un mur de ce bâtiment (une surface debout de son modèle)."), warning: true);
            }
            else if (_openingPlacing)
            {
                if (tool.PlaceOpening(from - Viewer.ModelOffset, Vector3.Normalize(to - from), _openingWidth, _openingHeight) is { } opening)
                {
                    _openingPlaced = opening;
                    _openingShift = Vector2.Zero;
                    _openingPlacing = false;
                    _workshopRevision = -1;
                }
                else
                    state.Notify(L("Cliquez sur un mur du modèle (une surface debout, pas un sol ni un toit)."), warning: true);
            }
            else
                tool.ClickModel(from - Viewer.ModelOffset, Vector3.Normalize(to - from), io.KeyCtrl);
        }
        if (io.MouseWheel != 0)
            Viewer.Distance = Math.Clamp(Viewer.Distance * MathF.Pow(0.88f, io.MouseWheel), Viewer.MinDistance, Viewer.MaxDistance);
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Viewer.ResetView();
    }

    private void DrawInfo()
    {
        if (Viewer.Item is not { } item || Viewer.TriangleCount == 0)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.TextMuted, L($"{Widgets.Count(Viewer.TriangleCount)} triangles · {Viewer.MeshCount} pièce(s) · {Viewer.TextureCount} texture(s)")
            + (Viewer.MissingTextureCount > 0 ? L($", {Viewer.MissingTextureCount} introuvable(s)") : ""));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L($"Fichier : {item.Location.Path}")
                + (Viewer.MissingTextureCount > 0 ? L($"\nTextures introuvables : {string.Join(", ", Viewer.MissingTextures)}") : ""));

        bool canPlace = item.Kind == ViewerKind.Object && state.Features.Editing && state.World.Maps is not null && !_inWorkshop;
        bool canEdit = state.Features.Editing && item.Kind != ViewerKind.Ped && !_inWorkshop;
        float buttons = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(L("Copier le nom")).X + 70f + (canPlace ? ImGui.CalcTextSize(L("Placer")).X + 50f : 0f)
            + (canEdit ? ImGui.CalcTextSize(L("Modifier dans l'Atelier")).X + 50f : 0f);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - buttons));
        if (canEdit)
        {
            if (Widgets.Button(L($"{Icons.Box}  Modifier dans l'Atelier"),
                    L("Ouvre ce modèle en grand dans un onglet Atelier à côté de la carte : retirer, déplacer, ajouter des morceaux, changer ses textures.")))
                OpenWorkshop(item);
            ImGui.SameLine();
        }
        if (canPlace)
        {
            if (Widgets.PrimaryButton(L($"{Icons.MapPinned}  Placer")))
                state.PlaceRequest = item.ArchetypeHash;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(state.Project is null
                    ? L("Ajouter cet objet à la carte : ouvre ou crée d'abord un projet.")
                    : L($"Ajouter cet objet à la carte, posé au sol au centre de la vue 3D,\ndans le YMAP du projet ({state.Project.AddedMapName ?? state.Project.ResourceName + "_props"}.ymap).\nEnsuite : poignées 3D pour l'ajuster, Ctrl+Z pour annuler."));
            ImGui.SameLine();
        }
        if (KeyOf(item) is not null)
        {
            bool favorite = IsFavorite(item);
            if (Widgets.IconButton(Icons.Star, "viewer_favorite", favorite ? L("Retirer des favoris") : L("Ajouter aux favoris (bouton étoile au-dessus de la liste pour les retrouver)"), active: favorite))
                ToggleFavorite(item);
            ImGui.SameLine();
        }
        if (state.IsOtherGame)
        {
            if (Widgets.Button(L($"{Icons.Copy}  Copier le nom"), L("Nom du modèle")))
                ImGui.SetClipboardText(item.Name);
        }
        else
        {
            if (Widgets.Button(L($"{Icons.Copy}  Copier {Icons.ChevronDown}"), L("Nom, hash ou code Lua pour le faire apparaître dans FiveM")))
                ImGui.OpenPopup("##viewer_copy");
            DrawCopyMenu(item);
        }
    }

    // ---- Inspector ----

    private void DrawInspector()
    {
        if (Viewer.Item is not { } item)
        {
            Widgets.Hint(L("Choisis un modèle : ses couleurs, ses pièces, ses textures et sa fiche s'affichent ici."), wrap: true);
            return;
        }
        if (!ImGui.BeginTabBar("##viewer_tabs"))
            return;
        if (item.Kind == ViewerKind.Vehicle && ImGui.BeginTabItem(L("Peinture")))
        {
            Scroll("paint", DrawPaintTab);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Pièces")))
        {
            Scroll("parts", DrawPartsTab);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Textures")))
        {
            Scroll("textures", DrawTexturesTab);
            ImGui.EndTabItem();
        }
        if (item.Kind == ViewerKind.Vehicle ? ImGui.BeginTabItem(L("Fiche")) : ImGui.BeginTabItem(L("Type d'objet")))
        {
            Scroll("sheet", () =>
            {
                if (item.Kind == ViewerKind.Vehicle && IsGta4)
                    DrawGta4VehicleSheet(item);
                else if (item.Kind == ViewerKind.Vehicle)
                    DrawVehicleSheet(item);
                else
                    DrawArchetypeSheet(item);
            });
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Fichier")))
        {
            Scroll("file", () => DrawFileTab(item));
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    // ---- Model workshop ----

    // Each frame the workshop is shown: its tool on the model, the lines over the chosen piece, a converted texture.
    private void UpdateWorkshop(ViewerItem item)
    {
        // While the map reloads (after a change of the model), its models are not readable: the tool waits.
        if (_reopen is not null || state.World.IsBusy || state.World.Models is null)
        {
            // The model may have changed meanwhile (a building opened from Properties): read again after the reload.
            _reopen ??= (item, state.World.ModelReader);
            _previewCut = false;
            _workshop = null;
            _workshopFor = null;
            Viewer.OverlayEdges.Clear();
            _workshopStatus = L($"{Icons.LoaderCircle}  Rechargement de la carte...");
            return;
        }
        if (_workshopFor != item)
        {
            if (_workshop is { ChangeCount: > 0 } dropped && _workshopFor is not null)
                state.Notify(L($"Modifications de {Path.GetFileName(dropped.Path)} non enregistrées : abandonnées."), warning: true);
            _workshopFor = item;
            _addMaterial = -1;
            _openingPlaced = null;
            _openingPlacing = false;
            _changedSources.Clear();
            (_workshop, _workshopError) = ModelPartTool.Open(item.Location, state.World);
            _workshopRevision = -1;
            Viewer.OverlayEdges.Clear();
        }
        if (_workshop is not { } tool)
        {
            _workshopStatus = _workshopError ?? L("Modèle 3D introuvable.");
            return;
        }
        _workshopStatus = null;
        var building = OpeningBuilding;
        if (_previewCut && (building is null || building.Revision != _previewRevision))
            UndoOpeningPreview(item, tool);
        if (_openingBuildingRevision != (building?.Revision ?? -1))
        {
            _openingBuildingRevision = building?.Revision ?? -1;
            _workshopRevision = -1;
        }
        if (_workshopRevision != tool.Revision)
        {
            _workshopRevision = tool.Revision;
            Viewer.OverlayEdges.Clear();
            Viewer.OverlayEdges.AddRange(tool.Edges());
            if (CurrentOpening is { } opening)
                Viewer.OverlayEdges.AddRange(opening.Edges());
            // The building's opening is placed on the map: brought back into the model's space.
            if (building?.Current is { } placed && Matrix4x4.Invert(building.Building.Transform, out var toModel))
                Viewer.OverlayEdges.AddRange(placed.Edges().Select(e => (Vector3.Transform(e.From, toModel), Vector3.Transform(e.To, toModel))));
        }
        FinishTextureJob(item, tool);
        // The map's five tool keys choose the workshop's five tools.
        if (!ImGui.GetIO().WantTextInput)
        {
            Input.KeyAction[] tools = [Input.KeyAction.ToolSelect, Input.KeyAction.ToolMove, Input.KeyAction.ToolRotate, Input.KeyAction.ToolScale, Input.KeyAction.ToolCollision];
            for (int i = 0; i < tools.Length; i++)
            {
                if (state.Keys.Pressed(tools[i]))
                    ChooseWorkshopTool((WorkshopTool)i);
            }
        }
    }

    // Properties while the workshop is shown: the settings of its tool, then saving.
    public void DrawWorkshopProperties()
    {
        _inWorkshop = true;
        try
        {
            if (Viewer.Item is not { } item)
                return;
            ImGui.PushFont(UiTheme.TitleFont);
            ImGui.TextUnformatted(WorkshopToolNames[(int)_workshopTool]);
            ImGui.PopFont();
            Widgets.Hint(item.Name);
            if (_workshopCovers.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.Warning);
                ImGui.TextWrapped(L($"{Icons.TriangleAlert}  Sur la carte, {string.Join(", ", _workshopCovers)} est posé au même endroit que ce modèle et peut en cacher une partie : ")
                    + L("ce que vous voyez ici seul n'est pas forcément visible en jeu. Bouton « Carte autour » pour les voir ensemble."));
                ImGui.PopStyleColor();
            }
            if (_workshop is not { } tool || _workshopFor != item)
            {
                Widgets.Hint(_workshopStatus ?? L("Chargement..."), wrap: true);
                return;
            }
            float width = ImGui.GetContentRegionAvail().X;
            ImGui.Spacing();
            if (Widgets.BeginProperties("workshop"))
            {
                Widgets.PropertyText(L("Fichier"), Path.GetFileName(tool.Path), tool.Path);
                Widgets.PropertyText(L("Morceau choisi"), tool.SelectedCount == 0 ? L("aucun") : L($"{Widgets.Count(tool.SelectedCount)} triangles"));
                Widgets.EndProperties();
            }
            switch (_workshopTool)
            {
                case WorkshopTool.Choose:
                    DrawWorkshopChoose(item, tool, width);
                    break;
                case WorkshopTool.Move:
                    if (tool.SelectedCount > 0)
                        DrawWorkshopMove(item, tool, width);
                    else
                        Widgets.Hint(L("Cliquez d'abord sur un morceau dans la vue (ou dans la liste Scène) : les poignées apparaissent dessus."), wrap: true);
                    break;
                case WorkshopTool.Open:
                    if (OpeningBuilding is { } building && BuildingOpeningContent is { } drawBuilding)
                    {
                        ImGui.Spacing();
                        ImGui.SeparatorText(L("Ouvrir le bâtiment"));
                        DrawOpeningPreview(item, tool, building, width);
                        drawBuilding(building.Building);
                    }
                    else
                        DrawWorkshopOpening(item, tool, width);
                    break;
                case WorkshopTool.Add:
                    DrawWorkshopAdd(item, tool, width);
                    break;
                case WorkshopTool.Textures:
                    ImGui.Spacing();
                    DrawWorkshopTextures(item, tool, width);
                    break;
            }
            DrawWorkshopSave(item, tool, width);
        }
        finally
        {
            _inWorkshop = false;
        }
    }

    // Scene while the workshop is shown: the parts of the model (one per material), to choose them whole.
    public void DrawWorkshopParts()
    {
        _inWorkshop = true;
        try
        {
            if (Viewer.Item is not { } item || _workshop is not { } tool || _workshopFor != item)
            {
                Widgets.Hint(_workshopStatus ?? L("Chargement..."), wrap: true);
                return;
            }
            Widgets.Hint(L("Les parties du modèle, une par matière. Clic : la choisir entière ; Ctrl+clic : l'ajouter ou la retirer. ")
                + L("Un clic dans la vue choisit seulement le morceau touché."), wrap: true);
            if (!ImGui.BeginChild("##workshop_parts", Vector2.Zero, ImGuiChildFlags.None))
            {
                ImGui.EndChild();
                return;
            }
            var materials = tool.Materials;
            for (int i = 0; i < materials.Count; i++)
            {
                var (_, texture, triangles) = materials[i];
                int chosen = tool.SelectedIn(i);
                ImGui.PushID(i);
                if (ImGui.Selectable(L($"{Icons.Box}  Partie {i + 1} : {texture ?? L("sans texture")}"), chosen > 0))
                    tool.SelectMesh(i, ImGui.GetIO().KeyCtrl);
                string count = chosen > 0 && chosen < triangles ? $"{Widgets.Count(chosen)} / {Widgets.Count(triangles)}" : Widgets.Count(triangles);
                var max = ImGui.GetItemRectMax();
                float w = ImGui.CalcTextSize(count).X;
                ImGui.GetWindowDrawList().AddText(new Vector2(max.X - w - 4f, ImGui.GetItemRectMin().Y), ImGui.GetColorU32(UiTheme.TextMuted), count);
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        finally
        {
            _inWorkshop = false;
        }
    }

    // Choose tool: what can be done with the chosen piece.
    private void DrawWorkshopChoose(ViewerItem item, ModelPartTool tool, float width)
    {
        ImGui.Spacing();
        Widgets.Hint(L("Cliquez sur un morceau dans la vue : il s'entoure de rouge. Ctrl+clic : en ajouter ou en retirer un autre."), wrap: true);
        // Changes are shown at once in the view and kept in memory; the file and the map change only when saved.
        if (Widgets.Button(L($"{Icons.Trash}  Retirer ce morceau"),
                L("Le morceau disparaît de la vue tout de suite. Le fichier et la carte ne changent qu'avec « Enregistrer le modèle »."),
                width, enabled: tool.SelectedCount > 0))
        {
            tool.RemoveSelected();
            ShowWorkshopChanges(item, tool);
        }
        if (Widgets.Button(L($"{Icons.Copy}  Dupliquer ce morceau"),
                L("Ajoute une copie du morceau au même endroit (avec ses versions simplifiées) et la choisit :\ndéplacez-la ensuite avec l'outil Déplacer."),
                width, enabled: tool.SelectedCount > 0))
        {
            if (tool.DuplicateSelected() is { } error)
                state.Notify(error, warning: true);
            else
            {
                ShowWorkshopChanges(item, tool);
                ChooseWorkshopTool(WorkshopTool.Move);
                state.Notify(L("Copie ajoutée au même endroit : déplacez-la avec les poignées (elle est choisie)."));
            }
        }
        if (Widgets.Button(L($"{Icons.Copy}  Copier pour un autre modèle"),
                L("Garde ce morceau pour l'ajouter à un autre modèle : ouvrez l'autre modèle dans l'atelier, puis outil Ajouter."),
                width, enabled: tool.SelectedCount > 0))
        {
            if (tool.CopySelected(item.Name) is { } piece)
            {
                _copiedPiece = piece;
                _addShape = 2;
                state.Notify(L($"Morceau copié ({Widgets.Count(piece.Indices.Length / 3)} triangles) : ouvrez un autre modèle dans l'atelier pour l'ajouter."));
            }
            else
                state.Notify(L("Les sommets de ce modèle sont dans un format que NodeRunner ne sait pas encore modifier."), warning: true);
        }
        if (Widgets.Button(L("Tout désélectionner"), null, width, enabled: tool.SelectedCount > 0))
            tool.Clear();
    }

    // Under every tool: undo, save, drop, or put the original file back.
    private void DrawWorkshopSave(ViewerItem item, ModelPartTool tool, float width)
    {
        ImGui.Spacing();
        ImGui.SeparatorText(L("Modifications"));
        if (Widgets.Button(L($"{Icons.Undo}  Annuler la dernière modification"),
                L("Remet le dernier morceau retiré, déplacé ou dupliqué, ou la dernière texture remplacée (pas encore enregistrés)."),
                width, enabled: tool.ChangeCount > 0))
        {
            tool.UndoLast();
            ShowWorkshopChanges(item, tool);
        }
        if (tool.ChangeCount > 0)
        {
            Widgets.Hint(L($"{tool.ChangeCount} modification(s) pas encore enregistrée(s) : la carte et les fichiers ne changent qu'à l'enregistrement."), wrap: true);
            if (Widgets.PrimaryButton(L($"{Icons.Save}  Enregistrer le modèle"), width))
            {
                var (done, message) = tool.Save(state);
                state.Notify(message, warning: !done);
                if (done)
                {
                    _reopen = (item, state.World.ModelReader);
                    _workshopFor = null;
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Écrit le fichier du modèle dans le dossier stream du projet, puis recharge la carte une seule fois.\n")
                    + L("Toutes les copies de cet objet changent. « Remettre le modèle d'origine » annule ensuite."));
            if (Widgets.Button(L("Tout abandonner"), L("Oublie les morceaux retirés, déplacés ou dupliqués et les textures remplacées depuis le dernier enregistrement."), width))
            {
                _workshopFor = null;
                Viewer.Reload(item, state.World.ModelReader, state.World.Textures, state.World.TextureReader);
            }
        }
        else if (ModelPartTool.ChangedFile(state, tool.Path) is not null
            && Widgets.Button(L($"{Icons.Undo}  Remettre le modèle d'origine"),
                L("Annule tous les changements de ce modèle : morceaux retirés, déplacés ou dupliqués (le fichier d'avant est remis)."), width))
        {
            state.Notify(ModelPartTool.Restore(state, tool.Path));
            _reopen = (item, state.World.ModelReader);
            _workshopFor = null;
        }
    }

    // An opening through a wall (a door painted on the building): the triangles inside the rectangle go, those across its
    // sides are cut so the rest of the wall keeps its texture.
    private void DrawWorkshopOpening(ViewerItem item, ModelPartTool tool, float width)
    {
        ImGui.Spacing();
        ImGui.SeparatorText(L("Percer une ouverture"));
        Widgets.Hint(L("Pour une porte dessinée sur un mur : découpe un trou de la taille d'une porte, le reste du mur garde sa texture. ")
            + L("« Placer », puis cliquez sur le mur dans l'image (le bas se pose sur le sol devant le mur quand le modèle en a un)."), wrap: true);
        if (_openingPlacing)
        {
            Widgets.Hint(L($"{Icons.MousePointer}  Cliquez sur le mur dans l'image (Échap : annuler)."), wrap: true);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape) || Widgets.Button(L("Annuler"), null, width))
                _openingPlacing = false;
        }
        else if (Widgets.Button(L($"{Icons.Crosshair}  Placer l'ouverture"),
                L("Le clic suivant dans l'image pose l'ouverture sur le mur cliqué, face à vous."), width))
            _openingPlacing = true;

        ImGui.SetNextItemWidth(width * 0.6f);
        bool changed = ImGui.DragFloat(L("Largeur (m)"), ref _openingWidth, 0.01f, 0.3f, 20f, "%.2f");
        ImGui.SetNextItemWidth(width * 0.6f);
        changed |= ImGui.DragFloat(L("Hauteur (m)"), ref _openingHeight, 0.01f, 0.5f, 20f, "%.2f");
        ImGui.SetNextItemWidth(width * 0.6f);
        changed |= ImGui.DragFloat(L("Profondeur (m)"), ref _openingDepth, 0.01f, 0.05f, 5f, "%.2f");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Jusqu'où la découpe traverse le mur, devant et derrière le rectangle : assez pour l'épaisseur du mur ")
                + L("et un renfoncement de porte, pas plus (sinon elle touche ce qui est derrière)."));
        _openingWidth = Math.Clamp(_openingWidth, 0.3f, 20f);
        _openingHeight = Math.Clamp(_openingHeight, 0.5f, 20f);
        _openingDepth = Math.Clamp(_openingDepth, 0.05f, 5f);
        if (_openingPlaced is not null)
        {
            ImGui.SetNextItemWidth(width * 0.6f);
            changed |= ImGui.DragFloat2(L("Décalage (droite, haut)"), ref _openingShift, 0.01f, -20f, 20f, "%.2f");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Déplace l'ouverture le long du mur (mètres) depuis l'endroit cliqué."));
        }
        if (changed)
            _workshopRevision = -1;

        ImGui.BeginDisabled(CurrentOpening is null);
        bool cut = Widgets.PrimaryButton(L($"{Icons.DoorOpen}  Percer"), width);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Découpe le modèle et ses versions simplifiées. La collision du bâtiment n'est pas encore percée par cet outil."));
        if (cut && CurrentOpening is { } opening)
        {
            var (count, error) = tool.CutOpening(opening);
            if (error is not null)
                state.Notify(error, warning: true);
            else
            {
                _openingPlaced = null;
                ShowWorkshopChanges(item, tool);
                state.Notify(L($"Ouverture percée ({Widgets.Count(count)} triangles découpés) : « Enregistrer le modèle » pour l'écrire."));
            }
            _workshopRevision = -1;
        }
    }

    // Adding a piece: a simple shape or the piece copied from a model, with the material of a part of this model.
    private void DrawWorkshopAdd(ViewerItem item, ModelPartTool tool, float width)
    {
        ImGui.Spacing();
        ImGui.SeparatorText(L("Ajouter un morceau"));
        var materials = tool.Materials;
        if (materials.Count == 0)
            return;
        string[] shapes = _copiedPiece is { } copied
            ? [L("Cube"), L("Plan (au sol)"), L($"Morceau copié de {copied.Source} ({Widgets.Count(copied.Indices.Length / 3)} triangles)")]
            : [L("Cube"), L("Plan (au sol)")];
        _addShape = Math.Clamp(_addShape, 0, shapes.Length - 1);
        ImGui.SetNextItemWidth(width * 0.6f);
        ImGui.Combo(L("Forme"), ref _addShape, shapes, shapes.Length);

        // Default material: the one with the copied piece's texture, otherwise the biggest part.
        if (_addMaterial < 0 || _addMaterial >= materials.Count)
        {
            int same = _copiedPiece?.Texture is { } wanted ? materials.ToList().FindIndex(m => m.Texture == wanted) : -1;
            _addMaterial = same >= 0 ? same : materials.Select((m, i) => (m.Triangles, i)).Max().i;
        }
        string Label(int i) => L($"Partie {i + 1} : {materials[i].Texture ?? L("sans texture")}");
        ImGui.SetNextItemWidth(width * 0.6f);
        if (ImGui.BeginCombo(L("Matière"), Label(_addMaterial)))
        {
            for (int i = 0; i < materials.Count; i++)
            {
                if (ImGui.Selectable(Label(i), i == _addMaterial))
                    _addMaterial = i;
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Le morceau ajouté prend la matière (shader et textures) de cette partie du modèle."));

        if (_addShape < 2)
        {
            ImGui.SetNextItemWidth(width * 0.6f);
            ImGui.DragFloat3(L("Taille (m)"), ref _addSize, 0.01f, 0.01f, 100f, "%.2f");
            _addSize = Vector3.Clamp(_addSize, new Vector3(0.01f), new Vector3(100f));
            ImGui.Checkbox(L("Texture répétée chaque mètre"), ref _addPerMetre);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Coché : la texture se répète comme sur un mur ou un sol. Décoché : une fois sur chaque face (logo, affiche)."));
        }
        else
        {
            Widgets.Hint(L("Le morceau garde sa forme et ses coordonnées de texture, mais prend la matière choisie ici : ")
                + L("ses propres textures ne viennent pas avec lui."), wrap: true);
        }
        if (Widgets.Button(L($"{Icons.Box}  Ajouter"),
                L("Ajoute le morceau au milieu du morceau choisi (ou du modèle) et le choisit : déplacez-le ensuite avec les poignées."),
                width))
        {
            var at = tool.PlacePoint;
            var (vertices, indices) = _addShape switch
            {
                0 => DrawableAppend.Cube(at, _addSize, _addPerMetre),
                1 => DrawableAppend.Plane(at, new Vector2(_addSize.X, _addSize.Y), _addPerMetre),
                _ => (_copiedPiece!.Vertices.Select(v => v with { Position = v.Position + at }).ToList(), _copiedPiece.Indices),
            };
            if (tool.AddPiece(_addMaterial, vertices, indices) is { } error)
                state.Notify(error, warning: true);
            else
            {
                ShowWorkshopChanges(item, tool);
                state.Notify(L("Morceau ajouté : déplacez-le avec les poignées (il est choisi)."));
            }
            _workshopRevision = -1;
        }
    }

    // Moving the chosen piece by exact values (the handles in the image do the same with the mouse).
    private void DrawWorkshopMove(ViewerItem item, ModelPartTool tool, float width)
    {
        ImGui.Spacing();
        ImGui.SeparatorText(L("Déplacer le morceau"));
        Widgets.Hint(L("Tirez les flèches dans l'image pour le déplacer sur un axe, le point du milieu pour le glisser à plat, ")
            + L("l'anneau pour le tourner. Ctrl : par pas de 5 cm ou 15°. Échap : annuler."), wrap: true);
        ImGui.SetNextItemWidth(width * 0.6f);
        ImGui.DragFloat3(L("Décalage (m)"), ref _moveBy, 0.005f, -100f, 100f, "%.3f");
        ImGui.SetNextItemWidth(width * 0.6f);
        ImGui.DragFloat3(L("Rotation (°)"), ref _turnBy, 0.5f, -360f, 360f, "%.1f");
        bool any = _moveBy != Vector3.Zero || _turnBy != Vector3.Zero;
        if (Widgets.Button(L($"{Icons.Move}  Appliquer"), L("Décale puis tourne le morceau autour de son centre (axes du modèle)."), width, enabled: any)
            && tool.SelectionCenter is { } pivot)
        {
            const float ToRadians = MathF.PI / 180f;
            var move = Matrix4x4.CreateTranslation(-pivot)
                * Matrix4x4.CreateRotationX(_turnBy.X * ToRadians)
                * Matrix4x4.CreateRotationY(_turnBy.Y * ToRadians)
                * Matrix4x4.CreateRotationZ(_turnBy.Z * ToRadians)
                * Matrix4x4.CreateTranslation(pivot + _moveBy);
            MoveWorkshopPiece(item, tool, move);
            _moveBy = _turnBy = Vector3.Zero;
        }
    }

    // An image converted on a background thread for a texture: put in the model.
    private void FinishTextureJob(ViewerItem item, ModelPartTool tool)
    {
        if (_textureJob is { IsCompleted: true } done)
        {
            _textureJob = null;
            var job = done.Result;
            if (job.Tool != tool)
                state.Notify(L("Texture non remplacée : le modèle a changé pendant la conversion."), warning: true);
            else if ((job.Error ?? (job.Resized is { } resized ? tool.ResizeTexture(state.World, job.Source, resized)
                         : tool.ReplaceTexture(state.World, job.Source, job.NameHash, job.Pixels!))) is { } error)
                state.Notify(error, warning: true);
            else
            {
                ShowWorkshopChanges(item, tool);
                if (job.Resized is { } done2)
                    state.Notify(L($"Texture {done2.Name} passée en {done2.Width} x {done2.Height} (pas encore enregistrée)."));
                else
                    state.Notify(job.Note is null ? L("Texture remplacée (pas encore enregistrée).") : L($"Texture remplacée (pas encore enregistrée). {job.Note}"));
            }
        }
    }

    // The textures of the model: replaced by an image (converted to the texture's size and format), or exported.
    private void DrawWorkshopTextures(ViewerItem item, ModelPartTool tool, float width)
    {
        if (!Widgets.Section(L("Textures"), "workshop_textures"))
            return;
        var textures = Viewer.Model.Textures;
        if (textures.Count == 0)
        {
            Widgets.Hint(Viewer.IsLoading ? L("Chargement...") : L("Aucune texture."));
            return;
        }
        Widgets.Hint(L("Remplacer : choisissez une image (.png, .jpg, .dds...). NodeRunner la met à la taille de la texture et la convertit ")
            + L("dans son format (DXT, BC7...), comme pour un fichier .dds. L'image change tout de suite dans la vue."), wrap: true);
        if (_textureJob is not null)
            Widgets.Hint(L($"{Icons.LoaderCircle}  Conversion de l'image..."));
        float thumb = 40f;
        foreach (var texture in textures)
        {
            ImGui.PushID(texture.Name + texture.Source);
            var start = ImGui.GetCursorScreenPos();
            DrawChecker(ImGui.GetWindowDrawList(), start, new Vector2(thumb));
            if (Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
                ImGui.Image(id, new Vector2(thumb));
            else
                ImGui.Dummy(new Vector2(thumb));
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextUnformatted(texture.Name);
            ImGui.TextColored(UiTheme.TextMuted, $"{texture.Width} x {texture.Height} · {texture.Format}");
            bool embedded = string.Equals(texture.Source, tool.Path, StringComparison.OrdinalIgnoreCase);
            ImGui.TextColored(UiTheme.TextFaint, embedded ? L("dans le modèle") : Widgets.Fit(Path.GetFileName(texture.Source), width - thumb - 12f));
            ImGui.EndGroup();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(texture.Source);

            uint hash = Joaat.Hash(texture.Name);
            float third = (width - ImGui.GetStyle().ItemSpacing.X * 2f) / 3f;
            if (Widgets.Button(L($"{Icons.FolderOpen}  Remplacer..."),
                    embedded ? L("Choisir l'image qui remplace cette texture.")
                        : L($"Choisir l'image qui remplace cette texture. Elle est dans {Path.GetFileName(texture.Source)} : tous les objets qui utilisent ce dictionnaire changent."),
                    third, enabled: _textureJob is null))
                AskTextureImage(tool, texture.Source, hash, texture.Name);
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Exporter..."), L("Enregistrer cette texture en .png (pour la retoucher) ou en .dds (format du jeu)."), third))
                AskTextureExport(tool, texture.Source, hash);
            ImGui.SameLine();
            bool canResize = !embedded && Data.Native.Textures.TextureCodec.CanDecode(texture.Format);
            if (Widgets.Button(L("Taille..."),
                    embedded ? L("Texture rangée dans le fichier du modèle : sa taille ne peut pas encore changer (seulement celles des .ytd).")
                    : !canResize ? L($"Les textures {texture.Format} ne peuvent pas encore changer de taille.")
                    : L("Réduire ou agrandir cette texture. La réduire allège le .ytd (mémoire, limite de taille de FiveM)."),
                    third, enabled: canResize && _textureJob is null))
            {
                if (tool.FindTexture(state.World, texture.Source, hash, out var error) is var (stored, content))
                {
                    _resizing = (tool, texture.Source, stored.Read(content));
                    ImGui.OpenPopup("##texture_size");
                }
                else
                    state.Notify(error ?? L("Texture introuvable dans son fichier."), warning: true);
            }
            DrawTextureSizePopup();
            if (!embedded && ChangedSource(texture.Source)
                && Widgets.Button(L($"{Icons.Undo}  Remettre le dictionnaire d'origine"),
                    L($"Annule toutes les textures remplacées dans {Path.GetFileName(texture.Source)} (le fichier d'avant est remis)."), width))
            {
                state.Notify(ModelPartTool.Restore(state, texture.Source));
                _reopen = (item, state.World.ModelReader);
                _workshopFor = null;
            }
            ImGui.Spacing();
            ImGui.PopID();
        }
    }

    // Sizes offered for a texture: halves and doubles keeping its shape, then the change is made on a background thread.
    private void DrawTextureSizePopup()
    {
        if (!ImGui.BeginPopup("##texture_size"))
            return;
        if (_resizing is not var (tool, source, texture))
        {
            ImGui.EndPopup();
            return;
        }
        ImGui.TextColored(UiTheme.TextMuted, L($"{texture.Name} · {texture.Format}"));
        ImGui.Separator();
        bool blocks = Data.Textures.TextureLayout.IsBlockCompressed(texture.Format);
        for (int k = 2; k >= -5; k--)
        {
            int w = k >= 0 ? texture.Width << k : texture.Width >> -k;
            int h = k >= 0 ? texture.Height << k : texture.Height >> -k;
            if (w < 4 || h < 4 || Math.Max(w, h) > Math.Max(4096, Math.Max(texture.Width, texture.Height))
                || (blocks && (w % 4 != 0 || h % 4 != 0)))
                continue;
            string label = L($"{w} x {h}  ·  {FormatSize(TextureImport.Bytes(texture, w, h))}");
            if (k == 0)
                label += L("  (actuelle)");
            if (ImGui.Selectable(label, k == 0, k == 0 ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
            {
                _textureJob = Task.Run(() =>
                {
                    try
                    {
                        var resized = TextureImport.Resize(texture, w, h);
                        return new TextureJob(tool, source, resized.NameHash, null, null, null) { Resized = resized };
                    }
                    catch (Exception ex)
                    {
                        // Any error (not enough memory for the size asked...) is reported, not raised on the next frame.
                        return new TextureJob(tool, source, texture.NameHash, null, null, ex.Message);
                    }
                });
                _resizing = null;
            }
        }
        ImGui.Separator();
        Widgets.Hint(L("Agrandir ne rend pas l'image plus nette : à faire avant de la remplacer par une image plus grande."), wrap: false);
        ImGui.EndPopup();
    }

    private bool ChangedSource(string source)
    {
        if (!_changedSources.TryGetValue(source, out bool changed))
            _changedSources[source] = changed = ModelPartTool.ChangedFile(state, source) is not null;
        return changed;
    }

    // Asks for the image (dialog shown by the main window), then converts it on a background thread.
    private void AskTextureImage(ModelPartTool tool, string source, uint nameHash, string name)
    {
        state.DialogRequest = owner =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = L($"Remplacer la texture {name}"),
                Filter = L($"Images|{TextureImport.OpenFilter}|Tous les fichiers (*.*)|*.*"),
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            if (tool.FindTexture(state.World, source, nameHash, out var error) is not var (target, _))
            {
                state.Notify(error ?? L("Texture introuvable dans son fichier."), warning: true);
                return;
            }
            if (!target.Packed)
            {
                state.Notify(L("Cette texture est rangée d'une façon que NodeRunner ne sait pas encore réécrire."), warning: true);
                return;
            }
            string file = dialog.FileName;
            _textureJob = Task.Run(() =>
            {
                try
                {
                    var (pixels, note) = TextureImport.Convert(file, target);
                    return new TextureJob(tool, source, nameHash, pixels, note, null);
                }
                catch (Exception ex)
                {
                    // A damaged image file can fail in many ways: reported, not raised on the next frame.
                    return new TextureJob(tool, source, nameHash, null, null, ex.Message);
                }
            });
        };
    }

    private void AskTextureExport(ModelPartTool tool, string source, uint nameHash)
    {
        state.DialogRequest = owner =>
        {
            if (tool.FindTexture(state.World, source, nameHash, out var error) is not var (texture, content))
            {
                state.Notify(error ?? L("Texture introuvable dans son fichier."), warning: true);
                return;
            }
            bool png = Data.Native.Textures.TextureCodec.CanDecode(texture.Format);
            using var dialog = new SaveFileDialog
            {
                Title = L("Exporter la texture"),
                FileName = texture.Name + (png ? ".png" : ".dds"),
                Filter = png ? L("Image PNG (*.png)|*.png|Image DDS, format du jeu (*.dds)|*.dds") : L("Image DDS, format du jeu (*.dds)|*.dds"),
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            try
            {
                TextureImport.Export(texture.Read(content), dialog.FileName);
                state.Notify(L($"Texture exportée : {Path.GetFileName(dialog.FileName)}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Runtime.InteropServices.ExternalException)
            {
                state.Notify(L($"Export impossible : {ex.Message}"), warning: true);
            }
        };
    }

    // The view shows the model as changed in memory.
    private void ShowWorkshopChanges(ViewerItem item, ModelPartTool tool)
    {
        if (state.World.ModelReader is not { } reader)
            return;
        var changed = new PatchedModelReader(reader, tool.Path, tool.Header, (byte[])tool.Content.Clone());
        var textures = state.World.TextureReader is { } textureReader ? new PatchedTextureReader(textureReader, tool.ChangedDictionaries()) : null;
        Viewer.Reload(item, changed, state.World.Textures, textures);
    }

    // Once the map is reloaded after a change of the model's file: the model is read again (from its new place when it
    // moved into the project).
    private void ReopenChangedModel()
    {
        if (_reopen is not var (item, reader) || state.World.ModelReader == reader || state.World.IsBusy || state.World.Maps is null)
            return;
        _reopen = null;
        _workshopFor = null;
        Viewer.Model.Close();
        if (item.Kind == ViewerKind.Object && Library?.GetArchetype(item.ArchetypeHash) is { } archetype && Models?.Locate(archetype) is { } location)
            Open(item with { Location = location });
        else
            Open(item);
    }

    private static void Scroll(string id, Action draw)
    {
        if (ImGui.BeginChild($"##viewer_tab_{id}", Vector2.Zero, ImGuiChildFlags.None))
            draw();
        ImGui.EndChild();
    }

    // ---- Paint tab ----

    private void DrawPaintTab()
    {
        if (Paints is not { } paints)
        {
            Widgets.Hint(IsGta4 ? L("Lecture des couleurs du jeu (carcols.dat)...") : L("Lecture des couleurs du jeu (carcols, carvariations)..."));
            return;
        }
        if (paints.Colors.Count == 0)
        {
            Widgets.Hint(L("Palette des véhicules introuvable dans les fichiers du jeu."), wrap: true);
            return;
        }
        ImGui.Checkbox(L("Afficher la peinture"), ref _paintOn);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Décoché : la carrosserie garde la couleur de sa texture (blanc), comme avant"));

        var variation = Variation;
        var used = UsedSlots();
        if (Widgets.Section(L("Couleurs prévues par le jeu"), "viewer_paint_sets"))
        {
            if (variation is not { ColorSets.Count: > 0 })
            {
                Widgets.Hint(IsGta4 ? L("Aucune combinaison dans carcols.dat pour ce modèle : couleurs par défaut.")
                    : L("Aucune combinaison dans carvariations pour ce modèle : couleurs par défaut."), wrap: true);
            }
            else
            {
                Widgets.Hint(L($"{variation.ColorSets.Count} combinaison(s) : le jeu en tire une au hasard à l'apparition du véhicule."), wrap: true);
                float swatch = 30f;
                float width = ImGui.GetContentRegionAvail().X;
                int perRow = Math.Max(1, (int)((width + 4f) / (swatch + 4f)));
                for (int i = 0; i < variation.ColorSets.Count; i++)
                {
                    if (i % perRow != 0)
                        ImGui.SameLine(0, 4);
                    if (SetSwatch(i, variation.ColorSets[i], paints, swatch))
                        ApplySet(i);
                }
                if (Widgets.Button(L($"{Icons.RefreshCw}  Au hasard"), L("Une combinaison du jeu tirée au hasard")))
                    ApplySet(_random.Next(variation.ColorSets.Count));
            }
        }

        if (Widgets.Section(L("Couleurs"), "viewer_paint_slots"))
        {
            for (int i = 0; i < SlotCount; i++)
                SlotRow(i, paints, used.Contains(PaintSlots[i]));
            if (Widgets.Button(L($"{Icons.Undo}  Couleurs du jeu"), L("Revenir à la première combinaison prévue par le jeu")))
            {
                if (variation is { ColorSets.Count: > 0 })
                    ApplySet(0);
                else
                    DefaultPaint.CopyTo(_paint, 0);
            }
        }

        if (state.Features.Gta5Exports && Widgets.Section(L("Script FiveM (Lua)"), "viewer_paint_script"))
        {
            string script = PaintScript();
            ImGui.PushFont(UiTheme.SmallFont);
            ImGui.TextColored(UiTheme.TextMuted, script);
            ImGui.PopFont();
            if (Widgets.Button(L($"{Icons.Copy}  Copier le script"), L("Peint le véhicule du joueur avec ces couleurs (côté client)")))
                ImGui.SetClipboardText(script);
        }

        if (variation is not null && !IsGta4 && Widgets.Section(L("Autres réglages de carvariations"), "viewer_paint_more", defaultOpen: false))
        {
            if (Widgets.BeginProperties("viewer_variation"))
            {
                Widgets.PropertyText(L("Kits de tuning"), variation.Kits.Count == 0 ? "-" : string.Join(", ", variation.Kits.Select(HashText)));
                Widgets.PropertyText(L("Plaques"), variation.Plates.Count == 0 ? "-"
                    : string.Join(", ", variation.Plates.Select(p => $"{p.Plate} ({p.Weight} %)")));
                Widgets.PropertyText(L("Réglage des feux"), variation.LightSettings.ToString(CultureInfo.InvariantCulture),
                    L("Numéro du réglage des phares dans carcols (lightSettings)"));
                Widgets.PropertyText(L("Réglage des sirènes"), variation.SirenSettings.ToString(CultureInfo.InvariantCulture));
                Widgets.PropertyText(L("Fichier"), Path.GetFileName(variation.Source ?? ""), variation.Source);
                Widgets.EndProperties();
            }
        }
    }

    private static string HashText(string text) => text.StartsWith("hash_", StringComparison.Ordinal) ? "0x" + text[5..] : text;

    // Slots that a part of the model takes its colour from.
    private HashSet<VehiclePaintSlot> UsedSlots() =>
        Viewer.Model.Data?.Meshes.Select(m => VehiclePaints.SlotOf(m.DiffuseColor)).ToHashSet() ?? [];

    // A combination: primary on the left half, secondary on the right, wheels as a strip at the bottom.
    private bool SetSwatch(int index, VehicleColorSet set, VehiclePaints paints, float size)
    {
        ImGui.PushID(index);
        bool clicked = ImGui.InvisibleButton("##set", new Vector2(size));
        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        uint Color(int i) => Widgets.Col(new Vector4(paints.Color(set[i]) is { } c ? Display(c) : Vector3.One, 1f));
        float middle = (min.X + max.X) * 0.5f;
        float strip = max.Y - size * 0.22f;
        drawList.AddRectFilled(min, new Vector2(middle, strip), Color(0));
        drawList.AddRectFilled(new Vector2(middle, min.Y), new Vector2(max.X, strip), Color(1));
        drawList.AddRectFilled(new Vector2(min.X, strip), max, Color(IsGta4 ? 2 : 3));
        bool selected = _paintSet == index;
        drawList.AddRect(min - new Vector2(1), max + new Vector2(1),
            Widgets.Col(selected ? UiTheme.Accent : hovered ? UiTheme.Text : UiTheme.Border), 2f, ImDrawFlags.None, selected ? 2f : 1f);
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(L($"Combinaison {index + 1}"));
            for (int i = 0; i < SlotCount && i < set.Indices.Count; i++)
                ImGui.TextColored(UiTheme.TextMuted, $"{SlotName(PaintSlots[i])} : {ColorName(paints, set[i])}");
            ImGui.EndTooltip();
        }
        ImGui.PopID();
        return clicked;
    }

    private static string ColorName(VehiclePaints paints, int index) =>
        paints.Color(index) is { } c ? $"{index} · {c.Name}" : $"{index}";

    private void SlotRow(int slot, VehiclePaints paints, bool used)
    {
        ImGui.PushID(slot);
        var color = paints.Color(_paint[slot]);
        float height = ImGui.GetFrameHeight();
        if (ImGui.ColorButton("##slot", new Vector4(color is not null ? Display(color) : Vector3.One, 1f),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(height * 1.6f, height)))
        {
            _pickingSlot = slot;
            ImGui.OpenPopup("##viewer_palette");
        }
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(used ? UiTheme.Text : UiTheme.TextFaint, SlotName(PaintSlots[slot]));
        ImGui.PushFont(UiTheme.SmallFont);
        ImGui.TextColored(UiTheme.TextMuted, color is null ? $"{_paint[slot]}" : $"{_paint[slot]} · {color.Name}" + (used ? "" : L(" (aucune pièce de ce modèle)")));
        ImGui.PopFont();
        ImGui.EndGroup();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Clic sur la couleur : choisir dans la palette du jeu"));
        // The popup is opened from this row's id stack: drawn here.
        if (_pickingSlot == slot)
            DrawPalettePopup(paints);
        ImGui.PopID();
    }

    private static readonly (VehicleFinish? Finish, string Name)[] Families =
    [
        (null, "Tout"), (VehicleFinish.Metallic, "Métallisé"), (VehicleFinish.Matte, "Mat"), (VehicleFinish.Util, "Utilitaire"),
        (VehicleFinish.Worn, "Usé"), (VehicleFinish.Metal, "Métal"), (VehicleFinish.Chrome, "Chrome"),
        (VehicleFinish.Chameleon, "Caméléon"), (VehicleFinish.Other, "Autres"),
    ];

    private void DrawPalettePopup(VehiclePaints paints)
    {
        if (_pickingSlot < 0)
            return;
        ImGui.SetNextWindowSize(new Vector2(420f, 0f));
        if (!ImGui.BeginPopup("##viewer_palette"))
            return;
        ImGui.TextUnformatted(L($"{SlotName(PaintSlots[_pickingSlot])} : palette du jeu ({paints.Colors.Count} couleurs)"));
        ImGui.SetNextItemWidth(-float.Epsilon);
        Widgets.SearchField("##palette_search", L("Nom ou numéro (ex. matte, 27)"), ref _paletteSearch);
        ImGui.SetNextItemWidth(-float.Epsilon);
        string[] families = Families.Select(f => L(f.Name)).ToArray();
        ImGui.Combo("##palette_family", ref _paletteFamily, families, families.Length);

        string search = _paletteSearch.Trim();
        var list = paints.Colors.Where(c =>
                (Families[_paletteFamily].Finish is not { } f || c.Finish == f)
                && (search.Length == 0 || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || c.Index.ToString(CultureInfo.InvariantCulture) == search))
            .ToList();
        float swatch = 22f;
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + 3f) / (swatch + 3f)));
        if (ImGui.BeginChild("##palette_grid", new Vector2(0, Math.Min(320f, ((list.Count + perRow - 1) / perRow) * (swatch + 3f) + 8f))))
        {
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (i % perRow != 0)
                    ImGui.SameLine(0, 3);
                bool selected = _paint[_pickingSlot] == c.Index;
                if (selected)
                    ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.Accent);
                if (ImGui.ColorButton($"##p{c.Index}", new Vector4(Display(c), 1f),
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | (selected ? ImGuiColorEditFlags.None : ImGuiColorEditFlags.NoBorder),
                        new Vector2(swatch)))
                {
                    _paint[_pickingSlot] = c.Index;
                    _paintSet = -1;
                }
                if (selected)
                    ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{c.Index} · {c.Name}" + (c.RampTexture is not null ? "\n" + L($"Peinture qui change de couleur ({c.RampTexture})") : ""));
            }
        }
        ImGui.EndChild();
        if (list.Count == 0)
            Widgets.Hint(L("Aucune couleur"));
        ImGui.EndPopup();
    }

    private string PaintScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("local vehicle = GetVehiclePedIsIn(PlayerPedId(), false)");
        sb.AppendLine($"SetVehicleColours(vehicle, {_paint[0]}, {_paint[1]})");
        sb.AppendLine($"SetVehicleExtraColours(vehicle, {_paint[2]}, {_paint[3]})");
        sb.AppendLine($"SetVehicleInteriorColor(vehicle, {_paint[4]})");
        sb.Append($"SetVehicleDashboardColor(vehicle, {_paint[5]})");
        return sb.ToString();
    }

    // ---- Parts tab ----

    private void DrawPartsTab()
    {
        if (Viewer.Model.Data is not { } data)
        {
            Widgets.Hint(Viewer.IsLoading ? L("Chargement...") : L("Aucun modèle."));
            return;
        }
        Widgets.Hint(L("Survol : la pièce s'allume dans la vue. Œil : la cacher. Clic : ses détails."), wrap: true);
        ImGui.SetNextItemWidth(-float.Epsilon);
        Widgets.SearchField("##parts_search", L("Shader ou texture (ex. paint, glass)"), ref _partSearch);
        if (Widgets.Button(L($"{Icons.Eye}  Tout afficher")))
            Viewer.HiddenMeshes.Clear();
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.EyeOff}  Isoler"), L("Cacher toutes les pièces sauf celle choisie"), enabled: _selectedMesh >= 0))
        {
            Viewer.HiddenMeshes.Clear();
            for (int i = 0; i < data.Meshes.Count; i++)
                if (i != _selectedMesh)
                    Viewer.HiddenMeshes.Add(i);
        }

        string search = _partSearch.Trim();
        int hovered = -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        float tableHeight = Math.Max(160f, ImGui.GetContentRegionAvail().Y * (_selectedMesh >= 0 ? 0.5f : 1f));
        if (ImGui.BeginTable("##parts", 4, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##eye", ImGuiTableColumnFlags.WidthFixed, 22f);
            ImGui.TableSetupColumn(L("Shader"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(L("Peinture"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(L("Triangles"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            for (int i = 0; i < data.Meshes.Count; i++)
            {
                var mesh = data.Meshes[i];
                string shader = ShaderNames.Shader(mesh.ShaderHash);
                if (search.Length > 0 && !shader.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !(mesh.DiffuseTextureName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool visible = !Viewer.HiddenMeshes.Contains(i);
                if (Widgets.IconButton(visible ? Icons.Eye : Icons.EyeOff, "eye", null, size: ImGui.GetTextLineHeight() + 2f))
                {
                    if (visible)
                        Viewer.HiddenMeshes.Add(i);
                    else
                        Viewer.HiddenMeshes.Remove(i);
                }
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i + 1}. {shader}", _selectedMesh == i, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedMesh = _selectedMesh == i ? -1 : i;
                if (ImGui.IsItemHovered())
                    hovered = i;
                ImGui.TableNextColumn();
                var slot = VehiclePaints.SlotOf(mesh.DiffuseColor);
                ImGui.TextColored(slot is VehiclePaintSlot.None or VehiclePaintSlot.Unpainted ? UiTheme.TextFaint : UiTheme.Text,
                    slot == VehiclePaintSlot.None ? "-" : SlotName(slot));
                ImGui.TableNextColumn();
                ImGui.TextColored(UiTheme.TextMuted, Widgets.Count(mesh.Indices.Length / 3));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        Viewer.HighlightedMesh = hovered >= 0 ? hovered : _selectedMesh;

        if (_selectedMesh >= 0 && _selectedMesh < data.Meshes.Count)
            DrawMeshDetails(data.Meshes[_selectedMesh], _selectedMesh);
    }

    private void DrawMeshDetails(ModelMesh mesh, int index)
    {
        if (!Widgets.Section(L($"Pièce {index + 1}"), "viewer_part_details"))
            return;
        if (Widgets.BeginProperties("viewer_part"))
        {
            Widgets.PropertyText(L("Shader"), ShaderNames.Shader(mesh.ShaderHash));
            Widgets.PropertyText(L("Rendu"), ShaderNames.BlendMode(mesh.BlendMode));
            var slot = VehiclePaints.SlotOf(mesh.DiffuseColor);
            // In GTA IV the colour comes from the shader's name (gta_vehicle_paint1...), there is no matDiffuseColor.
            if (mesh.DiffuseColor is { } d && !IsGta4)
                Widgets.PropertyText(L("matDiffuseColor"), $"{d.X:0.###}, {d.Y:0.###}, {d.Z:0.###}",
                    slot is VehiclePaintSlot.None ? null : L($"2, n, n = prend la couleur n° n : {ShaderNames.Slot(slot)}"));
            if (slot != VehiclePaintSlot.None)
                Widgets.PropertyText(L("Couleur"), SlotName(slot));
            Widgets.PropertyText(L("Sommets"), Widgets.Count(mesh.Positions.Length));
            Widgets.PropertyText(L("Triangles"), Widgets.Count(mesh.Indices.Length / 3));
            if (mesh.Emission != MeshEmission.None)
                Widgets.PropertyText(L("Lumière propre"), mesh.Emission == MeshEmission.Night ? L("la nuit") : L("toujours"));
            if (mesh.VertexColored)
                Widgets.PropertyText(L("Couleur"), L("couleurs des sommets"));
            Widgets.EndProperties();
        }
        if (mesh.ShaderTextures.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.TextMuted, L("Textures du shader"));
            var texture = index < Viewer.Model.MeshTextures.Count ? Viewer.Model.MeshTextures[index] : null;
            foreach (var (sampler, name) in mesh.ShaderTextures.OrderBy(t => ShaderNames.SamplerOrder(t.Key)))
            {
                bool isColor = name == mesh.DiffuseTextureName;
                if (isColor && texture is not null && Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
                {
                    ImGui.Image(id, new Vector2(32f));
                    if (ImGui.IsItemClicked())
                        _openTexture = texture;
                    ImGui.SameLine();
                }
                ImGui.BeginGroup();
                ImGui.TextUnformatted(name);
                ImGui.PushFont(UiTheme.SmallFont);
                ImGui.TextColored(UiTheme.TextFaint, $"{ShaderNames.SamplerRole(sampler)} · {ShaderNames.SamplerName(sampler)}");
                ImGui.PopFont();
                ImGui.EndGroup();
            }
            Widgets.Hint(L("Seule la texture de couleur est affichée dans NodeRunner pour l'instant."), wrap: true);
        }
    }

    // ---- Textures tab ----

    private void DrawTexturesTab()
    {
        var textures = Viewer.Model.Textures;
        if (textures.Count == 0 && Viewer.MissingTextureCount == 0)
        {
            Widgets.Hint(Viewer.IsLoading ? L("Chargement...") : L("Aucune texture."));
            return;
        }
        if (_openTexture is { } open && textures.Contains(open))
        {
            DrawTexturePreview(open);
            return;
        }
        Widgets.Hint(L($"{textures.Count} texture(s) de couleur chargée(s) · clic : voir en grand"), wrap: true);
        float thumb = 48f;
        foreach (var texture in textures)
        {
            ImGui.PushID(texture.Name + texture.Source);
            var start = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton("##texture", new Vector2(ImGui.GetContentRegionAvail().X, thumb + 4f));
            bool hovered = ImGui.IsItemHovered();
            var drawList = ImGui.GetWindowDrawList();
            if (hovered)
                drawList.AddRectFilled(start, ImGui.GetItemRectMax(), Widgets.Col(new Vector4(1f, 1f, 1f, 0.05f)), UiTheme.FrameRounding);
            DrawChecker(drawList, start + new Vector2(2f), new Vector2(thumb));
            if (Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
                drawList.AddImage(id, start + new Vector2(2f), start + new Vector2(2f + thumb));
            float x = start.X + thumb + 12f;
            drawList.AddText(new Vector2(x, start.Y + 4f), Widgets.Col(UiTheme.Text), texture.Name);
            drawList.AddText(new Vector2(x, start.Y + 4f + ImGui.GetTextLineHeight()), Widgets.Col(UiTheme.TextMuted),
                $"{texture.Width} x {texture.Height} · {texture.Format} · {L($"{texture.MipLevels} niveau(x)")}");
            drawList.AddText(new Vector2(x, start.Y + 4f + ImGui.GetTextLineHeight() * 2), Widgets.Col(UiTheme.TextFaint),
                Widgets.Fit(Path.GetFileName(texture.Source), ImGui.GetContentRegionAvail().X - thumb - 16f));
            if (hovered)
                ImGui.SetTooltip(texture.Source);
            if (clicked)
                _openTexture = texture;
            ImGui.PopID();
        }
        if (Viewer.MissingTextureCount > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Warning, L($"{Icons.TriangleAlert}  Introuvables ({Viewer.MissingTextureCount})"));
            foreach (var name in Viewer.MissingTextures)
                ImGui.TextColored(UiTheme.TextMuted, name);
            Widgets.Hint(L("Textures cherchées dans le modèle, son dictionnaire (.ytd) et ses parents, puis vehshare."), wrap: true);
        }
    }

    private void DrawTexturePreview(PreviewTexture texture)
    {
        if (Widgets.Button(L($"{Icons.ChevronLeft}  Toutes les textures")))
        {
            _openTexture = null;
            return;
        }
        ImGui.TextUnformatted(texture.Name);
        float width = ImGui.GetContentRegionAvail().X;
        float height = width * texture.Height / Math.Max(1, texture.Width);
        var start = ImGui.GetCursorScreenPos();
        DrawChecker(ImGui.GetWindowDrawList(), start, new Vector2(width, height));
        if (Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
            ImGui.Image(id, new Vector2(width, height));
        else
            ImGui.Dummy(new Vector2(width, height));
        if (Widgets.BeginProperties("viewer_texture"))
        {
            Widgets.PropertyText(L("Taille"), $"{texture.Width} x {texture.Height}");
            Widgets.PropertyText(L("Format"), texture.Format.ToString());
            Widgets.PropertyText(L("Niveaux de détail"), texture.MipLevels.ToString(CultureInfo.InvariantCulture), L("Versions réduites gardées dans le fichier (mipmaps)"));
            Widgets.PropertyText(L("Fichier"), Path.GetFileName(texture.Source), texture.Source);
            Widgets.EndProperties();
        }
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom")))
            ImGui.SetClipboardText(texture.Name);
    }

    // Grey squares behind a texture, to see its transparent parts.
    private static void DrawChecker(ImDrawListPtr drawList, Vector2 min, Vector2 size)
    {
        drawList.AddRectFilled(min, min + size, Widgets.Col(new Vector4(0.32f, 0.32f, 0.34f, 1f)));
        float cell = 8f;
        uint dark = Widgets.Col(new Vector4(0.22f, 0.22f, 0.24f, 1f));
        for (float y = 0; y < size.Y; y += cell)
            for (float x = ((int)(y / cell) % 2) * cell; x < size.X; x += cell * 2)
                drawList.AddRectFilled(min + new Vector2(x, y), min + Vector2.Min(new Vector2(x + cell, y + cell), size), dark);
    }

    // ---- Vehicle sheet ----

    private static readonly (string Field, string Label)[] IdentityFields =
    [
        ("gameName", "Nom dans le jeu"), ("vehicleMakeName", "Marque"), ("vehicleClass", "Classe"), ("type", "Type"),
        ("wheelType", "Type de jantes"), ("plateType", "Plaques"), ("dashboardType", "Tableau de bord"),
        ("layout", "Disposition des places"), ("txdName", "Dictionnaire de textures"), ("handlingId", "Conduite (handlingId)"),
        ("audioNameHash", "Son"), ("swankness", "Standing"), ("frequency", "Fréquence d'apparition"), ("maxNum", "Nombre maximum"),
        ("defaultBodyHealth", "Santé de la carrosserie"), ("wheelScale", "Taille des roues avant"), ("wheelScaleRear", "Taille des roues arrière"),
    ];

    private void DrawVehicleSheet(ViewerItem item)
    {
        if (Vehicles is not { } vehicles)
        {
            Widgets.Hint(L("Lecture des fiches du jeu (vehicles.meta, handling.meta)..."));
            return;
        }
        var sheet = vehicles.Info.VehicleOf(item.Location.ModelHash);
        var handling = vehicles.Info.HandlingOf(item.Location.ModelHash);
        if (sheet.Count == 0)
        {
            Widgets.Hint(L("Ce modèle n'est pas dans les vehicles.meta du jeu."), wrap: true);
            return;
        }
        if (Widgets.Section(L("Identité"), "viewer_sheet_identity") && Widgets.BeginProperties("viewer_identity", 0.45f))
        {
            foreach (var (field, label) in IdentityFields)
            {
                if (VehicleInfoCatalog.Value(sheet, field) is { Length: > 0 } value)
                    Widgets.PropertyText(L(label), field switch
                    {
                        "vehicleClass" => $"{ClassName(value)} ({value})",
                        "type" => $"{TypeName(value)} ({value})",
                        _ => value,
                    }, field);
            }
            Widgets.EndProperties();
        }
        if (VehicleInfoCatalog.Value(sheet, "flags") is { Length: > 0 } flags && Widgets.Section(L("Particularités (flags)"), "viewer_sheet_flags", defaultOpen: false))
        {
            ImGui.PushFont(UiTheme.SmallFont);
            foreach (var flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                ImGui.TextColored(UiTheme.TextMuted, flag);
            ImGui.PopFont();
        }
        if (handling.Count > 0 && Widgets.Section(L("Conduite (handling.meta)"), "viewer_sheet_handling") && Widgets.BeginProperties("viewer_handling", 0.5f))
        {
            string? Get(string name) => VehicleInfoCatalog.Value(handling, name);
            float Number(string name) => float.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
            void Row(string label, string? value, string? tooltip = null)
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains("NaN", StringComparison.Ordinal))
                    Widgets.PropertyText(label, value, tooltip);
            }
            Row(L("Masse"), $"{Number("fMass"):0} kg", "fMass");
            Row(L("Vitesse de pointe"), L($"{Number("fInitialDriveMaxFlatVel"):0} km/h (valeur du fichier)"),
                L("fInitialDriveMaxFlatVel : la vitesse réelle en jeu est un peu différente (frottements, rapports)"));
            Row(L("Rapports"), Get("nInitialDriveGears"), "nInitialDriveGears");
            float bias = Number("fDriveBiasFront");
            Row(L("Transmission"), float.IsNaN(bias) ? null
                : bias <= 0.01f ? L("propulsion (roues arrière)") : bias >= 0.99f ? L("traction (roues avant)")
                : L($"4 roues motrices ({bias * 100:0} % à l'avant)"), "fDriveBiasFront");
            Row(L("Force du moteur"), Get("fInitialDriveForce"), "fInitialDriveForce");
            Row(L("Freinage"), Get("fBrakeForce"), "fBrakeForce");
            Row(L("Angle de braquage"), $"{Number("fSteeringLock"):0}°", "fSteeringLock");
            Row(L("Adhérence"), $"{Number("fTractionCurveMax"):0.00} / {Number("fTractionCurveMin"):0.00}", "fTractionCurveMax / fTractionCurveMin");
            Row(L("Réservoir"), $"{Number("fPetrolTankVolume"):0} L", "fPetrolTankVolume");
            Row(L("Valeur"), $"{Number("nMonetaryValue"):0} $", "nMonetaryValue");
            Widgets.EndProperties();
        }
        DrawAllFields(L("Toutes les valeurs de vehicles.meta"), "viewer_sheet_all", sheet);
        if (handling.Count > 0)
            DrawAllFields(L("Toutes les valeurs de handling.meta"), "viewer_handling_all", handling);
    }

    private static void DrawAllFields(string title, string id, IReadOnlyList<VehicleField> fields)
    {
        if (!Widgets.Section(title, id, defaultOpen: false))
            return;
        if (Widgets.Button(L($"{Icons.Copy}  Copier"), L("Toutes les valeurs, une par ligne")))
            ImGui.SetClipboardText(string.Join("\n", fields.Select(f => $"{f.Name} = {f.Value}")));
        ImGui.PushFont(UiTheme.SmallFont);
        if (ImGui.BeginTable($"##{id}", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var field in fields)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(UiTheme.TextMuted, field.Name);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(field.Value);
            }
            ImGui.EndTable();
        }
        ImGui.PopFont();
    }

    private static string ClassName(string? value) => value switch
    {
        "VC_COMPACT" => L("Compacte"), "VC_SEDAN" => L("Berline"), "VC_SUV" => L("SUV"), "VC_COUPE" => L("Coupé"),
        "VC_MUSCLE" => L("Muscle car"), "VC_SPORT_CLASSIC" => L("Sportive classique"), "VC_SPORT" => L("Sportive"),
        "VC_SUPER" => L("Supercar"), "VC_MOTORCYCLE" => L("Moto"), "VC_OFF_ROAD" => L("Tout-terrain"), "VC_INDUSTRIAL" => L("Industriel"),
        "VC_UTILITY" => L("Utilitaire"), "VC_VAN" => L("Van"), "VC_CYCLE" => L("Vélo"), "VC_BOAT" => L("Bateau"),
        "VC_HELICOPTER" => L("Hélicoptère"), "VC_PLANE" => L("Avion"), "VC_SERVICE" => L("Service"), "VC_EMERGENCY" => L("Urgences"),
        "VC_MILITARY" => L("Militaire"), "VC_COMMERCIAL" => L("Commercial"), "VC_RAIL" => L("Train"), "VC_OPEN_WHEEL" => L("Monoplace"),
        null => "",
        _ => value,
    };

    private static string TypeName(string? value) => value switch
    {
        "VEHICLE_TYPE_CAR" => L("Voiture"), "VEHICLE_TYPE_BIKE" => L("Moto"), "VEHICLE_TYPE_BICYCLE" => L("Vélo"),
        "VEHICLE_TYPE_QUADBIKE" => L("Quad"), "VEHICLE_TYPE_BOAT" => L("Bateau"), "VEHICLE_TYPE_HELI" => L("Hélicoptère"),
        "VEHICLE_TYPE_PLANE" => L("Avion"), "VEHICLE_TYPE_TRAILER" => L("Remorque"), "VEHICLE_TYPE_TRAIN" => L("Train"),
        "VEHICLE_TYPE_SUBMARINE" => L("Sous-marin"), "VEHICLE_TYPE_AMPHIBIOUS_AUTOMOBILE" => L("Voiture amphibie"),
        "VEHICLE_TYPE_AMPHIBIOUS_QUADBIKE" => L("Quad amphibie"), "VEHICLE_TYPE_BLIMP" => L("Dirigeable"),
        "VEHICLE_TYPE_SUBMARINECAR" => L("Voiture sous-marine"), null => "",
        _ => value,
    };

    // ---- GTA IV ----

    private static string Gta4TypeName(string type) => type.ToLowerInvariant() switch
    {
        "car" => L("Voiture"),
        "bike" => L("Moto"),
        "heli" => L("Hélicoptère"),
        "boat" => L("Bateau"),
        "plane" => L("Avion"),
        "train" => L("Train"),
        _ => type,
    };

    // The vehicle's line of vehicles.ide.
    private void DrawGta4VehicleSheet(ViewerItem item)
    {
        if (state.Gta4.Vehicles is not { } vehicles)
        {
            Widgets.Hint(L("Lecture des fiches du jeu (vehicles.ide)..."));
            return;
        }
        if (vehicles.Find(item.Location.ModelHash) is not { } d)
        {
            Widgets.Hint(L("Ce modèle n'est pas dans le vehicles.ide du jeu ou de l'épisode."), wrap: true);
            return;
        }
        if (Widgets.Section(L("Définition (vehicles.ide)"), "viewer_gta4_vehicle") && Widgets.BeginProperties("viewer_gta4_vehicle_props", 0.45f))
        {
            string Number(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
            Widgets.PropertyText(L("Modèle"), d.Model);
            Widgets.PropertyText(L("Type"), $"{Gta4TypeName(d.Type)} ({d.Type})");
            Widgets.PropertyText(L("Nom dans le jeu"), d.GameName, L("Clé du texte affiché dans le jeu"));
            Widgets.PropertyText(L("Conduite (handling.dat)"), d.HandlingId);
            Widgets.PropertyText(L("Dictionnaire de textures"), d.TextureDictionary);
            Widgets.PropertyText(L("Animations"), d.Anims);
            Widgets.PropertyText(L("Fréquence d'apparition"), d.Frequency.ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Nombre maximum"), d.MaxNumber.ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Taille des roues avant"), Number(d.WheelScaleFront));
            Widgets.PropertyText(L("Taille des roues arrière"), Number(d.WheelScaleRear));
            Widgets.PropertyText(L("Saleté de départ"), Number(d.DirtLevel), L("0 : de propre à sale, 0,5 : au moins à moitié sale..."));
            Widgets.PropertyText(L("Standing"), d.Swankness.ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Multiplicateur de distance (LOD)"), Number(d.LodMultiplier));
            Widgets.PropertyText(L("Drapeaux"), d.Flags.Replace("+", " + ", StringComparison.Ordinal));
            Widgets.PropertyText(L("Fichier"), Path.GetFileName(d.Source), d.Source);
            Widgets.EndProperties();
        }
    }

    private void DrawGta4FileSection(string path)
    {
        if (!Widgets.Section(L("Fichier"), "viewer_file") || !Widgets.BeginProperties("viewer_file_props", 0.4f))
            return;
        Widgets.PropertyText(L("Nom"), Path.GetFileName(path));
        Widgets.PropertyText(L("Jeu"), Gta4GameOf(path), L("Jeu de base ou épisode qui apporte ce fichier"));
        Widgets.PropertyText(L("Archive"), Widgets.Fit(Path.GetDirectoryName(path) ?? "", ImGui.GetContentRegionAvail().X), Path.GetDirectoryName(path));
        if (state.Gta4.Files.GetInfo(path) is { } i)
        {
            Widgets.PropertyText(L("Taille"), L($"{FormatSize(i.Size)} ({FormatSize(i.StoredSize)} dans l'archive)"));
            Widgets.PropertyText(L("Ressource"), i.IsResource ? L($"oui, type {i.ResourceType}") : L("non"));
        }
        Widgets.EndProperties();
    }

    private static string Gta4GameOf(string path) =>
        path.StartsWith(@"TLAD\", StringComparison.OrdinalIgnoreCase) ? Gta4World.EpisodeName(Data.Native.Gta4.Gta4Episode.LostAndDamned)
        : path.StartsWith(@"TBoGT\", StringComparison.OrdinalIgnoreCase) ? Gta4World.EpisodeName(Data.Native.Gta4.Gta4Episode.BalladOfGayTony)
        : Gta4World.EpisodeName(Data.Native.Gta4.Gta4Episode.Base);

    // ---- Object type ----

    private static string KindName(ArchetypeKind kind) => kind switch
    {
        ArchetypeKind.Time => L("Objet à horaires"),
        ArchetypeKind.Interior => L("Intérieur (MLO)"),
        _ => L("Objet"),
    };

    private void DrawArchetypeSheet(ViewerItem item)
    {
        if (Library?.GetArchetype(item.ArchetypeHash) is not { } a)
        {
            Widgets.Hint(state.IsOtherGame ? L("Type introuvable dans les .ide de la carte (modèle ouvert depuis l'explorateur d'archives).")
                : L("Type introuvable dans les YTYP de la carte (modèle ouvert depuis l'explorateur d'archives)."), wrap: true);
            return;
        }
        var names = Names;
        string Name(uint hash) => hash == 0 ? "-" : names?.Describe(hash) ?? $"0x{hash:X8}";
        if (Widgets.Section(state.IsOtherGame ? L("Définition (.ide)") : L("Archétype (YTYP)"), "viewer_archetype") && Widgets.BeginProperties("viewer_archetype_props", 0.45f))
        {
            Widgets.PropertyText(L("Nom"), Name(a.NameHash), $"0x{a.NameHash:X8}");
            Widgets.PropertyText(L("Sorte"), KindName(a.Kind));
            Widgets.PropertyText(L("Distance d'affichage"), $"{a.LodDistance:0.#} m", "lodDist");
            Widgets.PropertyText(L("Dictionnaire de textures"), Name(a.TextureDictionaryHash), "textureDictionary");
            if (a.DrawableDictionaryHash != 0)
                Widgets.PropertyText(L("Dictionnaire de modèles"), Name(a.DrawableDictionaryHash), "drawableDictionary");
            Widgets.PropertyText(L("Collision"), Name(a.PhysicsDictionaryHash), "physicsDictionary");
            Widgets.PropertyText(L("Type de fichier"), a.AssetType.ToString(), "assetType");
            Widgets.PropertyText(L("Drapeaux"), $"0x{a.Flags:X8}", "flags");
            if (a.Kind == ArchetypeKind.Time)
            {
                var hours = Enumerable.Range(0, 24).Where(h => a.IsVisibleAt(h)).ToList();
                Widgets.PropertyText(L("Visible"), hours.Count == 24 ? L("toute la journée") : string.Join(" ", hours.Select(h => $"{h}h")), "timeFlags");
            }
            var size = a.BoundsMax - a.BoundsMin;
            Widgets.PropertyText(L("Boîte"), $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m", "bbMin / bbMax");
            Widgets.PropertyText(L("Sphère"), $"{a.SphereRadius:0.00} m", "bsRadius");
            if (a.Kind == ArchetypeKind.Interior)
            {
                Widgets.PropertyText(L("Objets de l'intérieur"), Widgets.Count(a.InteriorEntities.Count));
                Widgets.PropertyText(L("Salles / portails"), $"{a.InteriorRooms.Count} / {a.InteriorPortals.Count}");
                Widgets.PropertyText(L("Entity sets"), a.InteriorEntitySets.Count.ToString(CultureInfo.InvariantCulture));
            }
            Widgets.EndProperties();
        }
    }

    // ---- File ----

    private void DrawFileTab(ViewerItem item)
    {
        string path = item.Location.Path;
        if (IsGta4)
        {
            DrawGta4FileSection(path);
        }
        else if (Widgets.Section(L("Fichier"), "viewer_file") && Widgets.BeginProperties("viewer_file_props", 0.4f))
        {
            var info = state.World.Files?.GetInfo(path);
            Widgets.PropertyText(L("Nom"), Path.GetFileName(path));
            Widgets.PropertyText(L("Pack"), PackOf(path), L("Pack DLC qui apporte ce fichier (ou le jeu de base)"));
            Widgets.PropertyText(L("Archive"), Widgets.Fit(Path.GetDirectoryName(path) ?? "", ImGui.GetContentRegionAvail().X), Path.GetDirectoryName(path));
            if (info is { } i)
            {
                Widgets.PropertyText(L("Taille"), L($"{FormatSize(i.Size)} ({FormatSize(i.StoredSize)} dans l'archive)"));
                Widgets.PropertyText(L("Ressource"), i.IsResource ? L($"oui, version {i.ResourceVersion}") : L("non"));
                Widgets.PropertyText(L("Chiffré"), i.IsEncrypted ? L("oui") : L("non"));
            }
            Widgets.EndProperties();
        }
        if (Widgets.Button(L($"{Icons.Copy}  Copier le chemin")))
            ImGui.SetClipboardText(path);

        if (Viewer.Model.Data is { } data && Widgets.Section(L("Géométrie"), "viewer_geometry") && Widgets.BeginProperties("viewer_geometry_props", 0.4f))
        {
            var size = data.BoundsMax - data.BoundsMin;
            Widgets.PropertyText(L("Dimensions"), $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m");
            Widgets.PropertyText(L("Boîte min"), $"{data.BoundsMin.X:0.00}, {data.BoundsMin.Y:0.00}, {data.BoundsMin.Z:0.00}");
            Widgets.PropertyText(L("Boîte max"), $"{data.BoundsMax.X:0.00}, {data.BoundsMax.Y:0.00}, {data.BoundsMax.Z:0.00}");
            Widgets.PropertyText(L("Pièces"), data.Meshes.Count.ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Sommets"), Widgets.Count(data.Meshes.Sum(m => m.Positions.Length)));
            Widgets.PropertyText(L("Triangles"), Widgets.Count(data.TriangleCount));
            Widgets.PropertyText(L("Shaders différents"), data.Meshes.Select(m => m.ShaderHash).Distinct().Count().ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Textures intégrées"), data.EmbeddedTextures.Count.ToString(CultureInfo.InvariantCulture),
                L("Textures rangées dans le fichier du modèle lui-même"));
            Widgets.EndProperties();
        }
    }

    private static string PackOf(string path)
    {
        var parts = path.Split('\\');
        int dlc = Array.FindIndex(parts, p => p.Equals("dlcpacks", StringComparison.OrdinalIgnoreCase));
        if (dlc >= 0 && dlc + 1 < parts.Length)
            return parts[dlc + 1];
        int patch = Array.FindIndex(parts, p => p.Equals("dlc_patch", StringComparison.OrdinalIgnoreCase));
        if (patch >= 0 && patch + 1 < parts.Length)
            return L($"{parts[patch + 1]} (correctif)");
        return path.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? L("mise à jour du jeu") : L("jeu de base");
    }

    private static string FormatSize(long bytes) => bytes >= 1 << 20
        ? L($"{bytes / 1048576.0:0.0} Mo")
        : L($"{bytes / 1024.0:0} Ko");
}
