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
    public const string Title = $"{Icons.Package}  Projet###Project";

    private static readonly string[] Filters = ["Tout", "Ajoutés", "Déplacés", "Supprimés"];

    private List<ObjectChange> _changes = [];
    private (object? Library, int Structure, object? Top, int Count) _key;
    private readonly Dictionary<string, IReadOnlyList<MapEntity>?> _gameEntities = new(StringComparer.OrdinalIgnoreCase);
    private object? _cacheLibrary;
    private Task? _reading;
    private int _filter;
    private string _search = "";
    private ObjectChange? _selected;

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(620f, 560f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title, ref open))
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        if (state.Project is not { } project)
        {
            Widgets.Hint("Aucun projet ouvert. Fichier > Nouveau projet, Ouvrir un projet ou Importer un mapping.", wrap: true);
            return;
        }
        if (state.World.Maps is not { } library)
        {
            Widgets.Hint("En attente du chargement de la carte...");
            return;
        }
        Update(library);

        if (Widgets.BeginProperties("##project_info"))
        {
            Widgets.PropertyText("Nom", project.Name);
            Widgets.PropertyText("Dossier", project.Folder);
            int added = _changes.Count(c => c.Kind == ChangeKind.Added);
            int moved = _changes.Count(c => c.Kind == ChangeKind.Moved);
            int deleted = _changes.Count(c => c.Kind == ChangeKind.Deleted);
            Widgets.PropertyText("Objets", $"{added} ajouté(s), {moved} déplacé(s), {deleted} supprimé(s)");
            Widgets.PropertyText("YMAP modifiés", _changes.Select(c => c.Map).Distinct().Count().ToString());
            Widgets.EndProperties();
        }

        if (Widgets.Section("Changements sur la carte", "project_changes"))
        {
            if (_reading is not null)
                Widgets.Hint("Comparaison avec les fichiers du jeu...");
            Widgets.Segmented("##project_filter", Filters, ref _filter);
            ImGui.SetNextItemWidth(-float.Epsilon);
            Widgets.SearchField("##project_search", "Filtrer par objet ou YMAP", ref _search);
            DrawTable();
        }

        if (Widgets.Section("Fichiers du projet (stream)", "project_files", defaultOpen: false))
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
        var rows = _changes
            .Where(c => _filter == 0 || (int)c.Kind == _filter - 1)
            .Select(c => (Change: c, Name: names?.Describe(c.Entity.ArchetypeHash) ?? $"0x{c.Entity.ArchetypeHash:X8}", MapName: MapName(c.Map)))
            .Where(r => search.Length < 2 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.MapName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Change.Kind).ThenBy(r => r.MapName).ThenBy(r => r.Name)
            .ToList();

        if (rows.Count == 0)
        {
            Widgets.Hint(_changes.Count == 0 ? "Ce projet ne change encore aucun objet de la carte." : "Aucun changement pour ce filtre.");
            return;
        }

        if (!ImGui.BeginTable("##project_rows", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
                new Vector2(0, Math.Min(ImGui.GetContentRegionAvail().Y, 420f))))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Changement", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Objet", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("YMAP", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed, 70f);
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
                    ChangeKind.Added => ("Ajouté", new Vector4(0.40f, 0.80f, 0.45f, 1f)),
                    ChangeKind.Moved => ("Déplacé", new Vector4(0.95f, 0.65f, 0.25f, 1f)),
                    _ => ("Supprimé", new Vector4(0.95f, 0.40f, 0.40f, 1f)),
                };
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable(label, ReferenceEquals(_selected, change), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                    GoTo(change);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(change.Kind == ChangeKind.Deleted
                        ? "Clic : aller à l'emplacement de l'objet supprimé"
                        : "Clic : sélectionner et aller à l'objet");
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

    private static void DrawFiles(string stream, MapLibrary library)
    {
        string[] files;
        try
        {
            files = Directory.Exists(stream) ? Directory.GetFiles(stream) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Widgets.Hint(ex.Message, wrap: true);
            return;
        }
        if (files.Length == 0)
        {
            Widgets.Hint("Le dossier stream est vide (rien d'enregistré pour l'instant).");
            return;
        }
        foreach (var group in files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).OrderBy(g => g.Key))
        {
            string what = group.Key switch
            {
                ".ymap" => "YMAP (objets)",
                ".ybn" => "collisions",
                ".ynd" => "pathnodes",
                ".ytyp" => "types (YTYP)",
                ".ymf" => "manifeste",
                _ => "autres",
            };
            ImGui.TextUnformatted($"{group.Count()} {group.Key} : {what}");
        }
        if (library.SavedMapErrors.Count > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.65f, 0.25f, 1f));
            ImGui.TextUnformatted($"{Icons.TriangleAlert}  {library.SavedMapErrors.Count} fichier(s) non repris :");
            ImGui.PopStyleColor();
            foreach (var error in library.SavedMapErrors)
                Widgets.Hint(error, wrap: true);
        }
    }
}
