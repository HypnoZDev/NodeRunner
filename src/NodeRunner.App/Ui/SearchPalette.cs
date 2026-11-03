using System.Globalization;
using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

/// <summary>
/// Global search (Ctrl+K): a small window at the top of the screen with a field and a list of results:
/// a position pasted from a FiveM script, a hash (0x3073333B, or decimal), the nearest objects whose type matches,
/// map files (.ymap) and types (archetypes). Arrows choose, Enter goes there (camera, selection), Escape or a click
/// elsewhere closes it. A typed name whose hash is that of a type without a known name becomes its name everywhere.
/// </summary>
internal sealed class SearchPalette(AppState state, Action<MapData> revealMap, Action<uint> openInViewer)
{
    private const float Width = 640f;
    private const float MaxListHeight = 420f;
    private const int MaxObjects = 8;
    private const int MaxMaps = 6;
    private const int MaxTypes = 6;

    private enum Kind { Position, Object, Map, Type }

    private sealed record Result(Kind Kind, string Icon, string Label, string Detail, int Score)
    {
        public MapEntity? Entity { get; init; }
        public MapData? Map { get; init; }
        public uint Type { get; init; }
        public Coordinates? Position { get; init; }
    }

    private sealed record Group(string Title, List<Result> Results);

    private bool _open;
    private bool _justOpened;
    private string _query = "";
    private string _searched = "\0";
    private MapLibrary? _searchedLibrary;
    private readonly List<Group> _groups = [];
    private readonly List<Result> _flat = [];
    private int _index;
    private bool _scrollToIndex;

    // Search index of the loaded map, built at the first search.
    private MapLibrary? _indexLibrary;
    private int _indexStructure; // MapLibrary.StructureVersion: objects deleted or brought back since the index was made.
    private readonly Dictionary<uint, List<MapEntity>> _objectsByType = [];
    private (uint Hash, string Name)[] _types = [];
    private (MapData Map, string Name)[] _maps = [];

    public bool IsOpen => _open;

    public void Open()
    {
        _open = true;
        _justOpened = true;
        _searched = "\0"; // Search again: the camera has moved, distances changed.
    }

    public void Draw()
    {
        if (!_open)
            return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f, viewport.WorkPos.Y + viewport.WorkSize.Y * 0.14f),
            ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(Width, 0f), new Vector2(Width, float.MaxValue));
        if (_justOpened)
            ImGui.SetNextWindowFocus();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]);
        ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.Border);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.AlwaysAutoResize;
        bool visible = ImGui.Begin("##search_palette", flags);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        if (visible)
            DrawContent();
        // A click elsewhere (another panel or the 3D view) closes it, like a menu.
        bool clickedOutside = (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            && !ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (!_justOpened && (clickedOutside || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)))
            _open = false;
        _justOpened = false;
        ImGui.End();
    }

    private void DrawContent()
    {
        if (_justOpened || !ImGui.IsAnyItemActive())
            ImGui.SetKeyboardFocusHere();
        Widgets.SearchField("##search_query", "Objet, YMAP, type, ou position FiveM : vector3(x, y, z)", ref _query,
            flags: ImGuiInputTextFlags.AutoSelectAll);

        if (_query != _searched || state.World.Maps != _searchedLibrary)
            Search();

        if (_flat.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                Move(1);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                Move(-1);
            if (ImGui.IsKeyPressed(ImGuiKey.PageDown))
                Move(8);
            if (ImGui.IsKeyPressed(ImGuiKey.PageUp))
                Move(-8);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            _open = false;
        else if ((ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) && _flat.Count > 0)
        {
            if (ImGui.GetIO().KeyCtrl && HasModel(_flat[_index]))
                OpenInViewer(_flat[_index]);
            else
                Activate(_flat[_index]);
        }
        if (!_open)
        {
            ImGui.Dummy(Vector2.Zero); // The search field moved the cursor back: an item must follow.
            return;
        }

        ImGui.Dummy(new Vector2(0, 2));
        if (_flat.Count == 0)
            DrawEmpty();
        else
            DrawResults();
        DrawFooter();
    }

    private void Move(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, _flat.Count - 1);
        _scrollToIndex = true;
    }

    private void DrawEmpty()
    {
        string query = _query.Trim();
        if (query.Length == 0)
        {
            Widgets.Hint("Exemples : prop_bench_01a · dt1_02 · 0x3073333B · vector3(215.76, -810.12, 30.73) · -1037.5, -2737.9", wrap: true);
            if (state.World.Maps is null)
                Widgets.Hint(state.World.IsBusy ? "Carte en cours de chargement : seules les positions sont disponibles." : "Aucune carte chargée.", wrap: true);
        }
        else if (query.Length < 2)
            Widgets.Hint("Tape au moins 2 caractères.");
        else
            Widgets.Hint(state.World.Maps is null ? "Carte en cours de chargement." : $"Aucun résultat pour « {query} ».");
    }

    private void DrawResults()
    {
        float rowHeight = ImGui.GetTextLineHeight() + 8f;
        float headerHeight = UiTheme.SmallFontSize + 10f;
        float height = _flat.Count * rowHeight + _groups.Count * headerHeight + 4f;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild("##search_results", new Vector2(0, Math.Min(height, MaxListHeight)), ImGuiChildFlags.None, ImGuiWindowFlags.NoNavInputs);
        ImGui.PopStyleColor();

        var drawList = ImGui.GetWindowDrawList();
        int i = 0;
        foreach (var group in _groups)
        {
            ImGui.PushFont(UiTheme.SmallFont);
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
            ImGui.TextColored(UiTheme.TextFaint, group.Title);
            ImGui.PopFont();
            foreach (var result in group.Results)
            {
                DrawRow(drawList, result, i, rowHeight);
                i++;
            }
        }
        ImGui.EndChild();
    }

    private void DrawRow(ImDrawListPtr drawList, Result result, int index, float height)
    {
        ImGui.PushID(index);
        bool choice = index == _index && HasModel(result);
        if (ImGui.Selectable("##row", index == _index, choice ? ImGuiSelectableFlags.AllowOverlap : ImGuiSelectableFlags.None, new Vector2(0, height)))
        {
            ImGui.PopID();
            Activate(result);
            return;
        }
        // The row's rectangle first: the buttons drawn over it become the last item.
        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (choice && DrawChoice(result, min, max))
        {
            ImGui.PopID();
            return;
        }
        ImGui.PopID();
        if (hovered && ImGui.GetIO().MouseDelta != Vector2.Zero)
            _index = index;
        if (index == _index && _scrollToIndex)
        {
            _scrollToIndex = false;
            if (min.Y < ImGui.GetWindowPos().Y || max.Y > ImGui.GetWindowPos().Y + ImGui.GetWindowHeight())
                ImGui.SetScrollHereY(0.5f);
        }

        float textY = (min.Y + max.Y - ImGui.GetTextLineHeight()) * 0.5f;
        string detail = choice ? "" : result.Detail;
        var detailSize = ImGui.CalcTextSize(detail);
        float detailX = max.X - 8f - (choice ? _choiceWidth : detailSize.X);
        drawList.AddText(Widgets.Snap(new Vector2(min.X + 8f, textY)), Widgets.Col(index == _index ? UiTheme.AccentText : UiTheme.TextMuted), result.Icon);
        float nameX = min.X + 32f;
        string label = Widgets.Fit(result.Label, detailX - nameX - 16f);
        drawList.AddText(Widgets.Snap(new Vector2(nameX, textY)), Widgets.Col(UiTheme.Text), label);
        drawList.AddText(Widgets.Snap(new Vector2(detailX, textY)), Widgets.Col(UiTheme.TextFaint), detail);
    }

    private float _choiceWidth;

    /// <summary>
    /// On the chosen object or type: "Monde" (go to it in the 3D view) and "Visionneuse" buttons, at the right of the row.
    /// Returns true if one was clicked (the palette is then closed).
    /// </summary>
    private bool DrawChoice(Result result, Vector2 min, Vector2 max)
    {
        bool placed = result.Kind == Kind.Object || IsPlaced(result.Type);
        string world = $"{Icons.Crosshair}  Monde";
        string viewer = $"{Icons.Box}  Visionneuse";
        var padding = ImGui.GetStyle().FramePadding;
        float buttonHeight = ImGui.GetFrameHeight();
        float worldWidth = ImGui.CalcTextSize(world).X + padding.X * 2;
        float viewerWidth = ImGui.CalcTextSize(viewer).X + padding.X * 2;
        _choiceWidth = worldWidth + viewerWidth + 6f;
        var cursor = ImGui.GetCursorScreenPos();
        float y = (min.Y + max.Y - buttonHeight) * 0.5f;

        bool clicked = false;
        ImGui.SetCursorScreenPos(new Vector2(max.X - 6f - _choiceWidth, y));
        ImGui.BeginDisabled(!placed);
        if (Widgets.Button(world, placed ? "Aller à l'objet dans la vue 3D (Entrée)" : "Ce type n'est placé nulle part sur la carte"))
        {
            Activate(result);
            clicked = true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine(0, 6f);
        if (!clicked && Widgets.Button(viewer, "Voir le modèle seul dans la visionneuse (Ctrl+Entrée), puis « Placer » pour l'ajouter à la carte"))
        {
            OpenInViewer(result);
            clicked = true;
        }
        ImGui.SetCursorScreenPos(cursor);
        ImGui.Dummy(Vector2.Zero);
        return clicked;
    }

    /// <summary>Objects and types have a model that the viewer can show.</summary>
    private static bool HasModel(Result result) => result.Kind is Kind.Object or Kind.Type;

    private bool IsPlaced(uint type) => _objectsByType.TryGetValue(type, out var list) && list.Count > 0;

    private void OpenInViewer(Result result)
    {
        _open = false;
        openInViewer(result.Kind == Kind.Object && result.Entity is { } entity ? entity.ArchetypeHash : result.Type);
    }

    private static void DrawFooter()
    {
        ImGui.Dummy(new Vector2(0, 2));
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, p.Y), new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowWidth(), p.Y), Widgets.Col(UiTheme.Divider));
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SmallFont);
        ImGui.TextColored(UiTheme.TextFaint, "↑ ↓  choisir     Entrée  y aller     Ctrl+Entrée  visionneuse     Échap  fermer");
        ImGui.PopFont();
    }

    // --- Search ---

    private void Search()
    {
        _searched = _query;
        _searchedLibrary = state.World.Maps;
        _groups.Clear();
        _flat.Clear();
        _index = 0;
        _scrollToIndex = true;

        string query = _query.Trim();
        if (Coordinates.TryParse(query) is { } coordinates)
            AddGroup("Position", [PositionResult(coordinates)]);

        if (query.Length >= 2 && state.World.Maps is { } library)
        {
            var names = state.World.Names ?? new HashNames();
            BuildIndex(library, names);
            LearnTypedName(query, library, names);
            if (HashText.TryParse(query) is { } hash && FindHash(hash, library, names) is { Count: > 0 } byHash)
                AddGroup("Hash", byHash);
            var groups = new List<Group>
            {
                new("Objets les plus proches", FindObjects(query, names)),
                new("YMAP", FindMaps(query)),
                new("Types (archétypes)", FindTypes(query, library)),
            };
            // The kind with the best match first (e.g. a YMAP whose exact name was typed), otherwise in this order.
            foreach (var group in groups.Where(g => g.Results.Count > 0).OrderBy(g => g.Results.Min(r => r.Score)))
                AddGroup(group.Title, group.Results);
        }
    }

    private void AddGroup(string title, List<Result> results)
    {
        _groups.Add(new Group(title, results));
        _flat.AddRange(results);
    }

    private Result PositionResult(Coordinates c)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{c.X:0.00}, {c.Y:0.00}, {(c.Z is { } z ? z.ToString("0.00", CultureInfo.InvariantCulture) : "?")}");
        string detail = c.Heading is { } h
            ? string.Create(CultureInfo.InvariantCulture, $"cap {h:0.#}° · {Distance(c.ToVector3(state.Camera.Position.Z)):0} m")
            : c.Z is null ? "hauteur estimée" : $"{Distance(c.ToVector3(0)):0} m";
        return new Result(Kind.Position, Icons.MapPinned, $"Aller à {text}", detail, 0) { Position = c };
    }

    /// <summary>Lists every object by type, all type names and map names, once per loaded map.</summary>
    private void BuildIndex(MapLibrary library, HashNames names)
    {
        if (_indexLibrary == library && _indexStructure == library.StructureVersion)
            return;
        _indexLibrary = library;
        _indexStructure = library.StructureVersion;
        _objectsByType.Clear();
        foreach (var map in library.Maps)
        {
            foreach (var entity in map.KeptEntities)
            {
                Add(entity);
                foreach (var child in entity.InteriorEntities)
                    Add(child);
            }
        }
        var hashes = library.GetArchetypeHashes();
        hashes.UnionWith(_objectsByType.Keys);
        _types = hashes.Select(h => (h, names.Describe(h))).ToArray();
        _maps = library.Maps.Select(m => (m, m.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : $"0x{m.NameHash:X8}")).ToArray();

        void Add(MapEntity entity)
        {
            if (!_objectsByType.TryGetValue(entity.ArchetypeHash, out var list))
                _objectsByType[entity.ArchetypeHash] = list = [];
            list.Add(entity);
        }
    }

    /// <summary>Objects whose type matches: the best matches first, then the nearest.</summary>
    private List<Result> FindObjects(string query, HashNames names)
    {
        var best = new List<(int Score, float Distance, MapEntity Entity)>();
        foreach (var (hash, name) in _types)
        {
            int score = Score(name, query);
            if (score < 0 || !_objectsByType.TryGetValue(hash, out var entities))
                continue;
            foreach (var entity in entities)
            {
                var candidate = (score, Distance(entity.Position), entity);
                if (best.Count == MaxObjects && Compare(candidate, best[^1]) >= 0)
                    continue;
                int at = best.FindIndex(b => Compare(candidate, b) < 0);
                best.Insert(at < 0 ? best.Count : at, candidate);
                if (best.Count > MaxObjects)
                    best.RemoveAt(best.Count - 1);
            }
        }
        return best.Select(b => new Result(Kind.Object, b.Entity.IsInterior ? Icons.House : Icons.Box, names.Describe(b.Entity.ArchetypeHash),
            $"{MapName(b.Entity)} · {b.Distance:0} m", b.Score) { Entity = b.Entity }).ToList();

        static int Compare((int Score, float Distance, MapEntity) a, (int Score, float Distance, MapEntity) b) =>
            a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Distance.CompareTo(b.Distance);
    }

    private List<Result> FindMaps(string query) =>
        _maps.Select(m => (m.Map, m.Name, Score: Score(m.Name, query)))
            .Where(m => m.Score >= 0)
            .OrderBy(m => m.Score).ThenBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(MaxMaps)
            .Select(m => new Result(Kind.Map, Icons.File, m.Name,
                $"{m.Map.Entities.Count} objets · {Distance(Center(m.Map)):0} m", m.Score) { Map = m.Map })
            .ToList();

    private List<Result> FindTypes(string query, MapLibrary library) =>
        _types.Select(t => (t.Hash, t.Name, Score: Score(t.Name, query)))
            .Where(t => t.Score >= 0)
            .OrderBy(t => t.Score).ThenBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(MaxTypes)
            .Select(t => TypeResult(t.Hash, t.Name, t.Score, library))
            .ToList();

    private Result TypeResult(uint hash, string name, int score, MapLibrary library, string prefix = "")
    {
        int count = _objectsByType.TryGetValue(hash, out var list) ? list.Count : 0;
        string file = library.GetArchetypeFile(hash) is { } path ? Path.GetFileName(path) : "?";
        string placed = count == 0 ? "non placé" : count == 1 ? "1 sur la carte" : $"{count} sur la carte";
        return new Result(Kind.Type, Icons.Package, name, $"{prefix}{file} · {placed}", score) { Type = hash };
    }

    private bool IsType(uint hash, MapLibrary library) => library.GetArchetype(hash) is not null || _objectsByType.ContainsKey(hash);

    /// <summary>The type and the map files whose name has this hash (shown first: the hash was typed on purpose).</summary>
    private List<Result> FindHash(uint hash, MapLibrary library, HashNames names)
    {
        var results = new List<Result>();
        if (IsType(hash, library))
            results.Add(TypeResult(hash, names.Describe(hash), -1, library, $"0x{hash:X8} · "));
        foreach (var (map, name) in _maps.Where(m => m.Map.NameHash == hash).Take(MaxMaps))
            results.Add(new Result(Kind.Map, Icons.File, name, $"0x{hash:X8} · YMAP · {map.Entities.Count} objets", -1) { Map = map });
        return results;
    }

    /// <summary>
    /// A typed name whose hash is that of a type without a known name (often a simplified model, SLOD...): it becomes
    /// its name, here and everywhere else (Properties, Scene...), until the map is loaded again.
    /// </summary>
    private void LearnTypedName(string query, MapLibrary library, HashNames names)
    {
        if (!HashText.IsName(query))
            return;
        uint hash = Joaat.Hash(query);
        if (names.TryGet(hash) is not null || !IsType(hash, library))
            return;
        names.Add(query);
        int at = Array.FindIndex(_types, t => t.Hash == hash);
        if (at >= 0)
            _types[at] = (hash, names.Describe(hash));
    }

    /// <summary>0 = same name, 1 = starts with the query, 2 = a part of the name (after _) does, 3 = contains it, -1 = no match.</summary>
    private static int Score(string name, string query)
    {
        int at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return -1;
        if (at == 0)
            return name.Length == query.Length ? 0 : 1;
        if (name[at - 1] == '_')
            return 2;
        return name.IndexOf("_" + query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 3;
    }

    // --- Going there ---

    private void Activate(Result result)
    {
        _open = false;
        switch (result.Kind)
        {
            case Kind.Position when result.Position is { } c:
                var target = c.ToVector3(GroundEstimate(c));
                state.LookFromAbove(target, c.HeadingDirection);
                state.Marker = (target, c.HeadingDirection);
                break;
            case Kind.Object when result.Entity is { } entity:
                GoTo(entity);
                break;
            case Kind.Map when result.Map is { } map:
                var size = map.EntitiesExtentsMax - map.EntitiesExtentsMin;
                state.FocusOn(Center(map), Math.Max(size.Length() * 0.5f, 10f));
                revealMap(map);
                break;
            case Kind.Type:
                if (_objectsByType.TryGetValue(result.Type, out var entities) && entities.Count > 0)
                    GoTo(entities.MinBy(e => Distance(e.Position))!);
                else
                    openInViewer(result.Type); // Placed nowhere: the viewer shows it (and can place it).
                break;
        }
    }

    private void GoTo(MapEntity entity)
    {
        state.Marker = null;
        state.Select(entity);
        state.FocusOn(entity);
    }

    /// <summary>Height for a position given without Z: that of the nearest object outside interiors, else the camera's.</summary>
    private float GroundEstimate(Coordinates c)
    {
        if (c.Z is { } z)
            return z;
        var point = new Vector2(c.X, c.Y);
        var nearest = state.World.Maps?.EntitiesAround(point, 80f)
            .Where(e => e.Interior is null && !e.IsInterior && e.LodLevel is LodLevel.Hd or LodLevel.OrphanHd)
            .MinBy(e => Vector2.DistanceSquared(new Vector2(e.Position.X, e.Position.Y), point));
        return nearest?.Position.Z ?? state.Camera.Position.Z;
    }

    private float Distance(Vector3 p) => Vector3.Distance(state.Camera.Position, p);

    private static Vector3 Center(MapData map) => (map.EntitiesExtentsMin + map.EntitiesExtentsMax) * 0.5f;

    private static string MapName(MapEntity entity) =>
        (entity.Interior ?? entity).Map?.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : "?";
}
