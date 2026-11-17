using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "Projet" panel: what the open project changes on the map (objects added, moved, deleted, by YMAP) and the files of
// its stream folder. Clicking an object goes to it.
internal sealed unsafe class ProjectPanel(AppState state)
{
    public const string Title = $"{Icons.FolderOpen}  Projet###Project";

    // Offered when no project is open.
    public Action? NewProject { get; set; }
    public Action? OpenProject { get; set; }
    public Action? ImportMapping { get; set; }

    private static string[] Filters => [L("Tout"), L("Ajoutés"), L("Déplacés"), L("Supprimés")];

    private List<ObjectChange> _changes = [];
    private (object? Library, int Structure, object? Top, int Count) _key;
    private readonly Dictionary<string, IReadOnlyList<MapEntity>?> _gameEntities = new(StringComparer.OrdinalIgnoreCase);
    private object? _cacheLibrary;
    private Task? _reading;
    private int _filter;
    private string _search = "";
    private ObjectChange? _selected;
    private List<(ObjectChange Change, string Name, string MapName)> _rows = [];
    private (object? Changes, int Filter, string Search, object? Names) _rowsKey;

    // The stream folder's files by extension, listed again once a second at most.
    private (string Folder, double Time, string? Error, List<(string Extension, int Count)> Groups) _streamFiles = ("", 0, null, []);

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(620f, 560f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(L(Title), ref open))
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        if (!state.Features.Editing)
        {
            Widgets.Hint(L("Les projets sont pour GTA V : dans ce jeu, la carte se consulte sans être modifiée."), wrap: true);
            return;
        }
        if (state.Project is not { } project)
        {
            Widgets.Hint(L("Aucun projet ouvert. Un projet garde vos modifications de la carte et les exporte en ressource FiveM."), wrap: true);
            ImGui.Dummy(new Vector2(0, 4));
            float width = ImGui.GetContentRegionAvail().X;
            if (Widgets.PrimaryButton(L($"{Icons.FolderPlus}  Nouveau projet"), width))
                NewProject?.Invoke();
            if (Widgets.Button(L($"{Icons.FolderOpen}  Ouvrir un projet..."), "Ctrl+O", width))
                OpenProject?.Invoke();
            if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Importer un mapping..."), L("Un mapping FiveM existant (dossier de ressource ou .zip) devient un projet."), width))
                ImportMapping?.Invoke();
            return;
        }
        if (state.World.Maps is not { } library)
        {
            Widgets.Hint(L("En attente du chargement de la carte..."));
            return;
        }
        Update(library);

        if (Widgets.BeginProperties("##project_info"))
        {
            Widgets.PropertyText(L("Nom"), project.Name);
            Widgets.PropertyText(L("Dossier"), project.Folder);
            int added = _changes.Count(c => c.Kind == ChangeKind.Added);
            int moved = _changes.Count(c => c.Kind == ChangeKind.Moved);
            int deleted = _changes.Count(c => c.Kind == ChangeKind.Deleted);
            Widgets.PropertyText(L("Objets"), L($"{added} ajouté(s), {moved} déplacé(s), {deleted} supprimé(s)"));
            Widgets.PropertyText(L("YMAP modifiés"), _changes.Select(c => c.Map).Distinct().Count().ToString());
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Changements sur la carte"), "project_changes"))
        {
            if (_reading is not null)
                Widgets.Hint(L("Comparaison avec les fichiers du jeu..."));
            Widgets.Segmented("##project_filter", Filters, ref _filter);
            ImGui.SetNextItemWidth(-float.Epsilon);
            Widgets.SearchField("##project_search", L("Filtrer par objet ou YMAP"), ref _search);
            DrawTable();
        }

        if (Widgets.Section(L("Fichiers du projet (stream)"), "project_files", defaultOpen: false))
            DrawFiles(project.StreamFolder, library);
    }

    private void Update(MapLibrary library)
    {
        if (!ReferenceEquals(_cacheLibrary, library))
        {
            _cacheLibrary = library;
            _gameEntities.Clear();
            _key = default;
        }
        if (_reading is { IsCompleted: true })
        {
            _reading = null;
            _key = default;
        }
        var key = (library, library.StructureVersion, (object?)state.History.NextUndo, state.History.Count);
        if (_reading is not null || key == _key)
            return;

        var maps = state.MapsToExport;
        var missing = maps.Where(m => !m.IsCustom && m.FilePath is { } p && !_gameEntities.ContainsKey(p)).ToList();
        if (missing.Count > 0 && state.World.Content is { } content)
        {
            var paths = missing.Select(m => m.FilePath!).ToList();
            _reading = Task.Run(() =>
            {
                var reader = new YmapReader();
                var read = new List<(string, IReadOnlyList<MapEntity>?)>();
                foreach (var path in paths)
                {
                    IReadOnlyList<MapEntity>? entities = null;
                    try
                    {
                        if (content.ReadFile(path) is { } bytes)
                            entities = reader.Read(bytes).Entities;
                    }
                    catch (InvalidDataException)
                    {
                    }
                    read.Add((path, entities));
                }
                return read;
            }).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    lock (_gameEntities)
                        foreach (var (path, entities) in t.Result)
                            _gameEntities[path] = entities;
            });
            return;
        }

        _key = key;
        var changes = new List<ObjectChange>();
        lock (_gameEntities)
        {
            foreach (var map in maps)
            {
                IReadOnlyList<MapEntity>? game = null;
                if (!map.IsCustom && (map.FilePath is null || !_gameEntities.TryGetValue(map.FilePath, out game) || game is null))
                    continue; // Game version unreadable: nothing to compare with.
                changes.AddRange(ProjectChanges.Compare(map, map.IsCustom ? null : game));
            }
        }
        _changes = changes;
    }

    private void DrawTable()
    {
        var names = state.World.Names;
        var camera = state.Camera.Position;
        string search = _search.Trim();
        // Filtered and sorted again only when the changes, the filter, the search or the names change.
        if (!ReferenceEquals(_changes, _rowsKey.Changes) || _filter != _rowsKey.Filter || search != _rowsKey.Search
            || !ReferenceEquals(names, _rowsKey.Names))
        {
            _rowsKey = (_changes, _filter, search, names);
            _rows = _changes
                .Where(c => _filter == 0 || (int)c.Kind == _filter - 1)
                .Select(c => (Change: c, Name: names?.Describe(c.Entity.ArchetypeHash) ?? $"0x{c.Entity.ArchetypeHash:X8}", MapName: MapName(c.Map)))
                .Where(r => search.Length < 2 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.MapName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Change.Kind).ThenBy(r => r.MapName).ThenBy(r => r.Name)
                .ToList();
        }
        var rows = _rows;

        if (rows.Count == 0)
        {
            Widgets.Hint(_changes.Count == 0 ? L("Ce projet ne change encore aucun objet de la carte.") : L("Aucun changement pour ce filtre."));
            return;
        }

        if (!ImGui.BeginTable("##project_rows", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
                new Vector2(0, Math.Min(ImGui.GetContentRegionAvail().Y, 420f))))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(L("Changement"), ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn(L("Objet"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("YMAP", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(L("Distance"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var (change, name, mapName) = rows[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                var (label, color) = change.Kind switch
                {
                    ChangeKind.Added => (L("Ajouté"), new Vector4(0.40f, 0.80f, 0.45f, 1f)),
                    ChangeKind.Moved => (L("Déplacé"), new Vector4(0.95f, 0.65f, 0.25f, 1f)),
                    _ => (L("Supprimé"), new Vector4(0.95f, 0.40f, 0.40f, 1f)),
                };
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable(label, ReferenceEquals(_selected, change), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                    GoTo(change);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(change.Kind == ChangeKind.Deleted
                        ? L("Clic : aller à l'emplacement de l'objet supprimé")
                        : L("Clic : sélectionner et aller à l'objet"));
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mapName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{Vector3.Distance(camera, change.Entity.Position):0} m");
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private void GoTo(ObjectChange change)
    {
        _selected = change;
        if (change.Kind == ChangeKind.Deleted)
        {
            state.Select(null);
            state.FocusOn(change.Entity.Position, 10f);
        }
        else
        {
            state.Select(change.Entity);
            state.FocusOn(change.Entity);
        }
    }

    private static string MapName(MapData map) => map.FilePath is { } p ? Path.GetFileName(p) : $"0x{map.NameHash:X8}";

    private void DrawFiles(string stream, MapLibrary library)
    {
        double now = ImGui.GetTime();
        if (_streamFiles.Folder != stream || now - _streamFiles.Time > 1.0 || now < _streamFiles.Time)
        {
            try
            {
                string[] files = Directory.Exists(stream) ? Directory.GetFiles(stream) : [];
                _streamFiles = (stream, now, null, files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).OrderBy(g => g.Key)
                    .Select(g => (g.Key, g.Count())).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _streamFiles = (stream, now, ex.Message, []);
            }
        }
        if (_streamFiles.Error is { } failure)
        {
            Widgets.Hint(failure, wrap: true);
            return;
        }
        if (_streamFiles.Groups.Count == 0)
        {
            Widgets.Hint(L("Le dossier stream est vide (rien d'enregistré pour l'instant)."));
            return;
        }
        foreach (var group in _streamFiles.Groups)
        {
            string what = group.Extension switch
            {
                ".ymap" => L("YMAP (objets)"),
                ".ybn" => "collisions",
                ".ynd" => "pathnodes",
                ".ytyp" => L("types (YTYP)"),
                ".ymf" => "manifeste",
                _ => "autres",
            };
            ImGui.TextUnformatted($"{group.Count} {group.Extension} : {what}");
        }
        if (library.SavedMapErrors.Count > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.65f, 0.25f, 1f));
            ImGui.TextUnformatted(L($"{Icons.TriangleAlert}  {library.SavedMapErrors.Count} fichier(s) non repris :"));
            ImGui.PopStyleColor();
            foreach (var error in library.SavedMapErrors)
                Widgets.Hint(error, wrap: true);
        }
    }
}
