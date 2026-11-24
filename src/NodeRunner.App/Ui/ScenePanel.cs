using System.Globalization;
using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Classic;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// Scene explorer (docked on the left): the map files (.ymap, or .wpl for GTA IV) and their objects as a tree table,
// near the camera or for the whole map, with a search by name.
internal sealed unsafe class ScenePanel(AppState state)
{
    public const string Title = $"{Icons.ListTree}  Scène###Scène";

    private const float NearRadius = 400f;
    private const float NearRefreshDistance = 25f;
    private const int MaxSearchMatches = 20000;

    private readonly List<Row> _rows = [];
    private int _builtStructure = -1; // MapLibrary.StructureVersion when the rows were made.
    private readonly HashSet<MapData> _expandedMaps = [];
    private readonly HashSet<MapEntity> _expandedInteriors = [];
    private readonly Dictionary<MapData, string> _mapNames = [];

    private string MapName(MapData map)
    {
        if (!_mapNames.TryGetValue(map, out var name))
            _mapNames[map] = name = map.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : $"0x{map.NameHash:X8}";
        return name;
    }

    private MapLibrary? _library;
    private bool _nearOnly = true;
    private string _search = "";
    private bool _dirty = true;
    private Vector3 _builtAt;

    private Dictionary<MapData, List<MapEntity>?>? _matches;
    private int _matchCount;

    private MapEntity? _revealed;
    private bool _scrollToSelection;

    private static string[] Filters => [L($"{Icons.Crosshair}  Proches"), L($"{Icons.Globe}  Toute la carte")];

    // Vice City / San Andreas: third filter listing the rooms.
    private static string[] FiltersWithRooms => [.. Filters, L($"{Icons.House}  Intérieurs")];

    private bool _roomsShown;
    private readonly List<(ClassicInterior Room, MapEntity? Entity)> _roomRows = [];
    private readonly HashSet<ClassicInterior> _expandedRooms = [];
    private ClassicScene? _roomScene;
    private bool _roomsDirty = true;

    private readonly record struct Row(int Depth, MapData Map, MapEntity? Entity, bool Open, bool HasChildren);

    // Drawn instead of the map's objects while the model workshop is shown (the parts of its model).
    public Action? WorkshopContent { get; set; }
    public Action? PedContent { get; set; }
    public Action? CutsceneContent { get; set; }
    public Action? RecordingContent { get; set; }

    public void Draw(ref bool visible)
    {
        if (ImGui.Begin(L(Title), ref visible))
        {
            if (state.WorkshopShown && WorkshopContent is { } workshop)
                workshop();
            else if (state.PedShown && PedContent is { } ped)
                ped();
            else if (state.CutsceneOpen && CutsceneContent is { } cutscenes)
                cutscenes();
            else if (state.RecordingOpen && RecordingContent is { } recordings)
                recordings();
            else
                DrawContent();
        }
        ImGui.End();
    }

    private void DrawContent()
    {
        if (state.ShownMaps is not { } library)
        {
            Widgets.Hint((state.Other?.IsBusy ?? state.World.IsBusy) ? L("Chargement de la carte...") : L("Aucune carte chargée."));
            return;
        }
        var names = state.ShownNames ?? new HashNames();
        if (!ReferenceEquals(library, _library))
            Reset(library);

        DrawFilters(library, names);
        if (_roomsShown && state.IsClassic && state.Classic.Scene is { Interiors.Count: > 0 } scene)
        {
            DrawRooms(scene, names);
            return;
        }
        _roomsShown = false;
        RevealSelection();
        if (_nearOnly && Vector2.Distance(Flat(_builtAt), Flat(state.Camera.Position)) > NearRefreshDistance)
            _dirty = true;
        if (library.StructureVersion != _builtStructure)
            _dirty = true; // Objects deleted or brought back.
        if (_dirty)
            Rebuild(library);

        DrawTable(names);
        DrawFooter(library);
    }

    // Other games than GTA V: one object selected at a time, read only.
    private MapEntity? Selected => state.IsOtherGame ? state.Gta4Selection : state.Selection;

    private bool IsSelected(MapEntity entity) => state.IsOtherGame ? state.Gta4Selection == entity : state.IsSelected(entity);

    private void SelectOne(MapEntity entity)
    {
        if (state.IsOtherGame)
            state.Gta4Selection = entity;
        else
            state.Select(entity);
    }

    // Kind of map file: YMAP, WPL for GTA IV, IPL for the trilogy.
    private string FileKind => state.Other?.MapFileKind ?? "YMAP";

    private string ExampleObject => state.IsGta4 ? "ag_bigandbushy" : state.IsClassic ? "palm" : "prop_tree";

    private void Reset(MapLibrary library)
    {
        _library = library;
        _expandedMaps.Clear();
        _expandedInteriors.Clear();
        _mapNames.Clear();
        _matches = null;
        _revealed = null;
        _dirty = true;
    }

    private void DrawFilters(MapLibrary library, HashNames names)
    {
        bool rooms = state.IsClassic && state.Classic.Scene is { Interiors.Count: > 0 };
        int filter = _roomsShown && rooms ? 2 : _nearOnly ? 0 : 1;
        string[] tips = [L($"{FileKind} à moins de {NearRadius:0} m de la caméra"), L($"Tous les {FileKind} chargés")];
        if (rooms)
            tips = [.. tips, L("Pièces du jeu : objets d'un même numéro d'intérieur. San Andreas les range loin au-dessus de la ville.")];
        if (Widgets.Segmented("scene_filter", rooms ? FiltersWithRooms : Filters, ref filter, tips))
        {
            _roomsShown = filter == 2;
            _nearOnly = filter == 0;
            _dirty = true;
            _roomsDirty = true;
        }

        if (Widgets.SearchField("##scene_search", _roomsShown ? L("Pièce, numéro ou objet (ex. 3)") : L($"{FileKind} ou objet (ex. {ExampleObject})"), ref _search))
        {
            _roomsDirty = true;
            _matches = _search.Trim().Length >= 2 ? Search(library, names, _search.Trim()) : null;
            _dirty = true;
        }
    }

    private Dictionary<MapData, List<MapEntity>?> Search(MapLibrary library, HashNames names, string query)
    {
        var result = new Dictionary<MapData, List<MapEntity>?>();
        _matchCount = 0;
        foreach (var map in library.Maps)
        {
            if (MapName(map).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result[map] = null;
                continue;
            }
            if (_matchCount >= MaxSearchMatches)
                continue;
            List<MapEntity>? found = null;
            foreach (var entity in map.KeptEntities)
            {
                if (names.Describe(entity.ArchetypeHash).Contains(query, StringComparison.OrdinalIgnoreCase))
                    (found ??= []).Add(entity);
            }
            if (found is not null)
            {
                result[map] = found;
                _matchCount += found.Count;
            }
        }
        return result;
    }

    public void RevealMap(MapData map)
    {
        _expandedMaps.Add(map);
        _revealMap = map;
        _dirty = true;
    }

    private MapData? _revealMap;

    private void RevealSelection()
    {
        var selection = Selected;
        if (selection == _revealed)
            return;
        _revealed = selection;
        if (selection is null)
            return;
        var owner = selection.Interior ?? selection;
        if (owner.Map is { } map)
            _expandedMaps.Add(map);
        if (selection.Interior is { } interior)
            _expandedInteriors.Add(interior);
        _scrollToSelection = true;
        _dirty = true;
    }

    private void Rebuild(MapLibrary library)
    {
        _dirty = false;
        _builtStructure = library.StructureVersion;
        _builtAt = state.Camera.Position;
        _rows.Clear();
        var camera = Flat(state.Camera.Position);
        var selectedMap = (Selected?.Interior ?? Selected)?.Map;

        IEnumerable<MapData> maps = _matches is not null ? _matches.Keys : library.Maps;
        if (_nearOnly)
            maps = maps.Where(m => m == selectedMap || m == _revealMap || DistanceToExtents(m, camera) <= NearRadius);

        foreach (var map in maps.OrderBy(m => MapName(m), StringComparer.OrdinalIgnoreCase))
        {
            var entities = (_matches?.GetValueOrDefault(map) ?? map.Entities).Where(e => !e.IsDeleted).ToList();
            bool open = _expandedMaps.Contains(map) || (_matches is not null && _matches[map] is not null);
            _rows.Add(new Row(0, map, null, open, entities.Count > 0));
            if (!open)
                continue;
            foreach (var entity in entities)
            {
                bool hasChildren = entity.IsInterior && entity.InteriorEntities.Any(c => !c.IsDeleted);
                bool interiorOpen = hasChildren && _expandedInteriors.Contains(entity);
                _rows.Add(new Row(1, map, entity, interiorOpen, hasChildren));
                if (interiorOpen)
                    foreach (var child in entity.InteriorEntities.Where(c => !c.IsDeleted))
                        _rows.Add(new Row(2, map, child, false, false));
            }
        }
    }

    private void DrawTable(HashNames names)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.NoBordersInBody;
        float footer = UiTheme.SmallFontSize + ImGui.GetStyle().ItemSpacing.Y * 2f + 6f;
        if (!ImGui.BeginTable("##scene_table", 3, flags, new Vector2(0, -footer)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(L("Nom"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(L("Type"), ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn(L("Dist."), ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.TextFaint);
        ImGui.TableHeadersRow();
        ImGui.PopStyleColor();

        float rowHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        if (_scrollToSelection || _revealMap is not null)
        {
            int index = _revealMap is { } map
                ? _rows.FindIndex(r => r.Entity is null && r.Map == map)
                : _rows.FindIndex(r => r.Entity is not null && r.Entity == Selected);
            _scrollToSelection = false;
            _revealMap = null;
            if (index >= 0)
                ImGui.SetScrollY(Math.Max(0, index * rowHeight - ImGui.GetWindowHeight() * 0.4f));
        }

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(_rows.Count, rowHeight);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRow(i, _rows[i], names);
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private void DrawRow(int index, Row row, HashNames names)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 0);
        ImGui.TableNextColumn();
        ImGui.PushID(index);

        var entity = row.Entity;
        var flags = ImGuiTreeNodeFlags.SpanAllColumns | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.NoTreePushOnOpen
            | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (!row.HasChildren)
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (entity is not null && IsSelected(entity))
            flags |= ImGuiTreeNodeFlags.Selected;

        float indent = row.Depth * ImGui.GetStyle().IndentSpacing;
        if (indent > 0) ImGui.Indent(indent);
        string label = entity is null
            ? $"{(row.Map.IsSaved ? Icons.Save : Icons.File)}  {MapName(row.Map)}"
            : $"{(entity.IsInterior ? Icons.House : Icons.Box)}  {names.Describe(entity.ArchetypeHash)}";
        bool dim = entity is { IsDrawnInMainView: false };
        if (dim) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        // The row spans all columns (whole-row highlight), so the name must be cut to its own column.
        label = Widgets.Fit(label, ImGui.GetContentRegionAvail().X - ImGui.GetTreeNodeToLabelSpacing());
        ImGui.SetNextItemOpen(row.Open);
        bool open = ImGui.TreeNodeEx("##node", flags, label);
        if (dim) ImGui.PopStyleColor();
        if (indent > 0) ImGui.Unindent(indent);

        // A leaf always reports itself open: only rows with children can be toggled.
        if (row.HasChildren && open != row.Open)
            Toggle(row, open);
        else if (entity is not null && ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            // Ctrl+click adds the object to the selection or removes it.
            bool control = ImGui.GetIO().KeyCtrl && state.Features.Editing;
            if (control)
                state.ToggleSelect(entity);
            else
                SelectOne(entity);
            _revealed = Selected; // Already visible: no scrolling.
            if (!control && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                state.FocusOn(entity);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal) && row.Map.FilePath is { } path && entity is null)
            ImGui.SetTooltip(row.Map.IsCustom ? L($"YMAP du projet (objets ajoutés), absent des fichiers du jeu\n{(row.Map.IsSaved ? L($"Enregistré dans {state.ExtraProjectOf(row.Map)?.StreamFolder ?? state.SaveFolder}") : L("Pas encore enregistré (Ctrl+S)"))}")
                : row.Map.IsSaved ? L($"Version enregistrée, dans {state.ExtraProjectOf(row.Map)?.StreamFolder ?? state.SaveFolder}\nFichier du jeu : {path}") : path);
        DrawContextMenu(row, names);

        ImGui.TableNextColumn();
        ImGui.TextDisabled(entity is null ? (row.Map.IsScripted ? L("YMAP script") : FileKind) : TypeName(entity));
        ImGui.TableNextColumn();
        if (entity is null)
            RightAligned($"{row.Map.KeptEntities.Count()}");
        else
            RightAligned($"{Vector3.Distance(state.Camera.Position, entity.Position):0} m");
        ImGui.PopID();
    }

    private void Toggle(Row row, bool open)
    {
        if (row.Entity is { } interior)
        {
            if (open) _expandedInteriors.Add(interior); else _expandedInteriors.Remove(interior);
        }
        else if (open)
            _expandedMaps.Add(row.Map);
        else
            _expandedMaps.Remove(row.Map);
        _dirty = true;
    }

    private void DrawContextMenu(Row row, HashNames names)
    {
        if (!ImGui.BeginPopupContextItem("##row_menu"))
            return;
        if (row.Entity is { } entity)
        {
            if (ImGui.MenuItem(L($"{Icons.Crosshair}  Aller à l'objet")))
            {
                SelectOne(entity);
                state.FocusOn(entity);
            }
            ImGui.Separator();
            if (ImGui.MenuItem(L($"{Icons.File}  Copier le nom : {names.Describe(entity.ArchetypeHash)}")))
                CopyText(names.Describe(entity.ArchetypeHash));
            string position = state.FormatPosition(entity.Position);
            string withHeading = state.FormatPosition(entity.Position, FiveMFormat.Heading(entity));
            if (ImGui.MenuItem(L($"{Icons.MapPinned}  Copier la position : {position}")))
                CopyText(position);
            if (ImGui.MenuItem(L($"{Icons.MapPinned}  Copier position et cap : {withHeading}")))
                CopyText(withHeading);
            // Other games are read only: nothing more.
            if (state.IsOtherGame)
            {
                ImGui.EndPopup();
                return;
            }
            if (state.IsSelected(entity) && state.SelectedEntities.Count is var selected and > 1
                && ImGui.MenuItem(L($"{Icons.MapPinned}  Copier les {selected} objets sélectionnés (liste {PositionText.FormatName(state.CopyFormat)})")))
                CopyText(state.FormatSelection());
            ImGui.Separator();
            int group = state.IsSelected(entity) ? state.DeletableSelection.Count : 1;
            if (ImGui.MenuItem(group > 1 ? L($"{Icons.Trash}  Supprimer les {group} objets sélectionnés...") : L($"{Icons.Trash}  Supprimer..."), L("Suppr"), false, entity.CanDelete))
            {
                // On a selected object: the whole selection goes; otherwise only this object.
                if (!state.IsSelected(entity))
                    state.Select(entity);
                state.RequestDelete();
            }
        }
        else
        {
            if (ImGui.MenuItem(L($"{Icons.File}  Copier le nom : {MapName(row.Map)}")))
                CopyText(MapName(row.Map));
            if (row.Map.FilePath is { } path && ImGui.MenuItem(L($"{Icons.FolderOpen}  Copier le chemin dans les archives")))
                CopyText(path);
        }
        ImGui.EndPopup();
    }

    private void DrawFooter(MapLibrary library)
    {
        int maps = _rows.Count(r => r.Entity is null);
        string text = _matches is not null
            ? L($"{maps} {FileKind} trouvé(s), {_matchCount} objet(s){(_matchCount >= MaxSearchMatches ? L(" (limite atteinte)") : "")}")
            : _nearOnly
                ? L($"{maps} {FileKind} proches sur {library.Maps.Count}")
                : L($"{library.Maps.Count} {FileKind}, {library.EntityCount} objets");
        if (state.SelectedEntities.Count is var selected and > 0)
            text += L($" · {selected} sélectionné(s)");
        // Thin line under the list, then the count.
        var p = ImGui.GetCursorScreenPos();
        float left = ImGui.GetWindowPos().X;
        ImGui.GetWindowDrawList().AddLine(new Vector2(left, p.Y - 3f), new Vector2(left + ImGui.GetWindowWidth(), p.Y - 3f), Widgets.Col(UiTheme.Divider));
        ImGui.Dummy(new Vector2(0, 1));
        Widgets.Hint(text);
    }

    // Rooms of Vice City / San Andreas: one line per room, open to see its objects. Clicking a room selects its
    // largest object and frames the whole room.
    private void DrawRooms(ClassicScene scene, HashNames names)
    {
        if (!ReferenceEquals(scene, _roomScene))
        {
            _roomScene = scene;
            _expandedRooms.Clear();
            _roomsDirty = true;
        }
        var selected = state.Gta4Selection;
        if (selected != _revealed)
        {
            _revealed = selected;
            if (selected is not null && scene.InteriorOf.TryGetValue(selected, out var owner) && _expandedRooms.Add(owner))
                _roomsDirty = true;
            _scrollToSelection = selected is not null;
        }
        if (_roomsDirty)
            RebuildRooms(scene, names);

        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.NoBordersInBody;
        float footer = UiTheme.SmallFontSize + ImGui.GetStyle().ItemSpacing.Y * 2f + 6f;
        if (ImGui.BeginTable("##rooms_table", 3, flags, new Vector2(0, -footer)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L("Nom"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(L("N°"), ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn(L("Objets"), ImGuiTableColumnFlags.WidthFixed, 54);
            ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.TextFaint);
            ImGui.TableHeadersRow();
            ImGui.PopStyleColor();

            float rowHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
            if (_scrollToSelection)
            {
                _scrollToSelection = false;
                int index = _roomRows.FindIndex(r => r.Entity is not null && r.Entity == selected);
                if (index >= 0)
                    ImGui.SetScrollY(Math.Max(0, index * rowHeight - ImGui.GetWindowHeight() * 0.4f));
            }
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(_roomRows.Count, rowHeight);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawRoomRow(i, _roomRows[i], names);
            }
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }

        int shown = _roomRows.Count(r => r.Entity is null);
        var p = ImGui.GetCursorScreenPos();
        float left = ImGui.GetWindowPos().X;
        ImGui.GetWindowDrawList().AddLine(new Vector2(left, p.Y - 3f), new Vector2(left + ImGui.GetWindowWidth(), p.Y - 3f), Widgets.Col(UiTheme.Divider));
        ImGui.Dummy(new Vector2(0, 1));
        Widgets.Hint(shown == scene.Interiors.Count
            ? L($"{scene.Interiors.Count} pièces, {scene.InteriorOf.Count} objets")
            : L($"{shown} pièce(s) trouvée(s) sur {scene.Interiors.Count}"));
    }

    private void RebuildRooms(ClassicScene scene, HashNames names)
    {
        _roomsDirty = false;
        _roomRows.Clear();
        string query = _search.Trim();
        foreach (var room in scene.Interiors)
        {
            bool found = query.Length == 0 || room.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || room.Number.ToString(CultureInfo.InvariantCulture) == query
                || (query.Length >= 2 && room.Entities.Any(e => names.Describe(e.ArchetypeHash).Contains(query, StringComparison.OrdinalIgnoreCase)));
            if (!found)
                continue;
            _roomRows.Add((room, null));
            if (_expandedRooms.Contains(room))
                foreach (var entity in room.Entities)
                    _roomRows.Add((room, entity));
        }
    }

    private void DrawRoomRow(int index, (ClassicInterior Room, MapEntity? Entity) row, HashNames names)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 0);
        ImGui.TableNextColumn();
        ImGui.PushID(index);
        var (room, entity) = row;
        var flags = ImGuiTreeNodeFlags.SpanAllColumns | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        bool expanded = _expandedRooms.Contains(room);
        if (entity is not null)
            flags |= ImGuiTreeNodeFlags.Leaf;
        if (entity is not null ? state.Gta4Selection == entity : state.Gta4Selection == room.Main && !expanded)
            flags |= ImGuiTreeNodeFlags.Selected;

        float indent = entity is null ? 0f : ImGui.GetStyle().IndentSpacing;
        if (indent > 0) ImGui.Indent(indent);
        string label = entity is null ? $"{Icons.House}  {room.Name}" : $"{Icons.Box}  {names.Describe(entity.ArchetypeHash)}";
        label = Widgets.Fit(label, ImGui.GetContentRegionAvail().X - ImGui.GetTreeNodeToLabelSpacing());
        ImGui.SetNextItemOpen(entity is null && expanded);
        bool open = ImGui.TreeNodeEx("##room", flags, label);
        if (indent > 0) ImGui.Unindent(indent);

        if (entity is null && open != expanded)
        {
            if (open) _expandedRooms.Add(room); else _expandedRooms.Remove(room);
            _roomsDirty = true;
        }
        else if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (entity is null)
            {
                state.Gta4Selection = room.Main;
                state.FocusOn(room.Center, room.Radius);
            }
            else
            {
                state.Gta4Selection = entity;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    state.FocusOn(entity);
            }
            _revealed = state.Gta4Selection;
        }
        if (entity is null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip(L($"Intérieur n° {room.Number}, {room.Entities.Count} objets\nPosition : {state.FormatPosition(room.Center)}\nClic : voir la pièce"));
        if (entity?.Map is { } map)
            DrawContextMenu(new Row(1, map, entity, false, false), names);

        ImGui.TableNextColumn();
        ImGui.TextDisabled(room.Number.ToString(CultureInfo.InvariantCulture));
        ImGui.TableNextColumn();
        if (entity is null)
            RightAligned(room.Entities.Count.ToString(CultureInfo.InvariantCulture));
        ImGui.PopID();
    }

    private static string TypeName(MapEntity entity) =>
        entity.IsInterior ? L("Intérieur") : entity.LodLevel switch
        {
            LodLevel.Hd => "HD",
            LodLevel.OrphanHd => L("HD seul"),
            LodLevel.Lod => "LOD",
            var level => level.ToString().ToUpperInvariant(),
        };

    private static void RightAligned(string text)
    {
        float x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (x > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(x);
        ImGui.TextDisabled(text);
    }

    private static void CopyText(string text) => ImGui.SetClipboardText(text);

    private static float DistanceToExtents(MapData map, Vector2 point)
    {
        var min = Flat(map.EntitiesExtentsMin);
        var max = Flat(map.EntitiesExtentsMax);
        return Vector2.Distance(point, Vector2.Clamp(point, min, max));
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
}

// Heading of an object as FiveM scripts use it (the positions themselves: PositionText).
internal static class FiveMFormat
{
    public static float Heading(MapEntity entity)
    {
        float z = EulerAngles.FromQuaternion(entity.Orientation).Z % 360f;
        return z < 0f ? z + 360f : z;
    }
}
