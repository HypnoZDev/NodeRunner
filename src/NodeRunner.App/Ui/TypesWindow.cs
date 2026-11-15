using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;

namespace NodeRunner.App.Ui;

// Window opened from the archive explorer on a .ytyp: the object types it defines (size, draw distance, model,
// textures), interiors with their rooms, portals and entity sets; a type goes to the viewer or to a copy on the map.
internal sealed class TypesWindow(AppState state, Action<Archetype> openInViewer)
{
    public const string Title = $"{Icons.Boxes}  Types###ArchiveTypes";

    private string? _path;
    private IReadOnlyList<Archetype> _types = [];
    private List<Archetype> _rows = [];
    private string _search = "";
    private string _rowsSearch = "";
    private Archetype? _selected;
    private bool _open;
    private bool _focus;

    // Copies placed on the loaded map of each type of the file.
    private Dictionary<uint, List<MapEntity>> _placed = [];

    public void Open(string path, byte[] data)
    {
        // A handful of old files of the game are in the PSO format, which the map does not use either.
        if (data.AsSpan().StartsWith("PSIN"u8))
        {
            state.Notify(L("Ce YTYP est dans l'ancien format PSO, pas encore lu par NodeRunner (le jeu en garde quelques-uns, inutilisés)."), warning: true);
            return;
        }
        try
        {
            _types = new YtypReader().Read(data).Archetypes;
        }
        catch (InvalidDataException ex)
        {
            state.Notify(L($"Lecture impossible : {ex.Message}"), warning: true);
            return;
        }
        _path = path;
        _selected = null;
        _search = "";
        _rows = SortedRows("");
        _placed = FindPlaced();
        _open = _focus = true;
    }

    private Dictionary<uint, List<MapEntity>> FindPlaced()
    {
        var placed = _types.Select(t => t.NameHash).Distinct().ToDictionary(h => h, _ => new List<MapEntity>());
        foreach (var map in state.World.Maps?.Maps ?? [])
        {
            foreach (var entity in map.KeptEntities)
            {
                if (placed.TryGetValue(entity.ArchetypeHash, out var list))
                    list.Add(entity);
                foreach (var child in entity.InteriorEntities)
                {
                    if (!child.IsDeleted && placed.TryGetValue(child.ArchetypeHash, out var inside))
                        inside.Add(child);
                }
            }
        }
        return placed;
    }

    private string Name(uint hash) => state.World.Names?.Describe(hash) ?? $"0x{hash:X8}";

    private List<Archetype> SortedRows(string search)
    {
        _rowsSearch = search;
        search = search.Trim();
        return _types.Where(t => search.Length == 0 || Name(t.NameHash).Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => Name(t.NameHash), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Draw()
    {
        if (!_open)
            return;
        if (_focus)
        {
            ImGui.SetNextWindowFocus();
            _focus = false;
        }
        ImGui.SetNextWindowSize(new Vector2(820f, 560f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.NoDocking))
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        Widgets.Hint(_path ?? "");
        int interiors = _types.Count(t => t.Kind == ArchetypeKind.Interior);
        ImGui.TextUnformatted(interiors > 0
            ? L($"{Widgets.Count(_types.Count)} types, dont {interiors} intérieurs (MLO)")
            : L($"{Widgets.Count(_types.Count)} types"));
        ImGui.SetNextItemWidth(-1);
        if (Widgets.SearchField("##types_search", L("Chercher un type"), ref _search) || _search != _rowsSearch)
            _rows = SortedRows(_search);

        float detailsHeight = _selected is null ? 0f : Math.Min(ImGui.GetContentRegionAvail().Y * 0.45f, 230f * UiTheme.Scale);
        if (ImGui.BeginTable("##types_rows", 5, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
                new Vector2(0, -detailsHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L("Nom"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(L("Genre"), ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn(L("Taille"), ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn(L("Distance"), ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn(L("Sur la carte"), ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableHeadersRow();
            foreach (var type in _rows)
                DrawRow(type);
            ImGui.EndTable();
        }
        if (_selected is { } selected)
            DrawDetails(selected);
    }

    private void DrawRow(Archetype type)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID((int)type.NameHash);
        string icon = type.Kind == ArchetypeKind.Interior ? Icons.House : Icons.Box;
        if (ImGui.Selectable($"{icon}  {Name(type.NameHash)}", _selected == type, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            _selected = type;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                openInViewer(type);
        }
        if (ImGui.BeginPopupContextItem("##type_menu"))
        {
            _selected = type;
            DrawMenu(type);
            ImGui.EndPopup();
        }
        ImGui.TableNextColumn();
        ImGui.TextDisabled(KindName(type.Kind));
        ImGui.TableNextColumn();
        var size = type.BoundsMax - type.BoundsMin;
        ImGui.TextUnformatted($"{size.X:0.#} × {size.Y:0.#} × {size.Z:0.#} m");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{type.LodDistance:0} m");
        ImGui.TableNextColumn();
        int count = _placed.GetValueOrDefault(type.NameHash)?.Count ?? 0;
        if (count > 0)
            ImGui.TextUnformatted(Widgets.Count(count));
        else
            ImGui.TextDisabled("0");
        ImGui.PopID();
    }

    private void DrawMenu(Archetype type)
    {
        if (ImGui.MenuItem(L($"{Icons.Box}  Voir dans la visionneuse")))
            openInViewer(type);
        int count = _placed.GetValueOrDefault(type.NameHash)?.Count ?? 0;
        if (ImGui.MenuItem(L($"{Icons.MapPinned}  Aller au plus proche sur la carte"), null, false, count > 0))
            GoToNearest(type);
        if (ImGui.MenuItem(L($"{Icons.Copy}  Copier le nom")))
        {
            ImGui.SetClipboardText(Name(type.NameHash));
            state.Notify(L("Nom copié"));
        }
    }

    private void GoToNearest(Archetype type)
    {
        if (_placed.GetValueOrDefault(type.NameHash) is not { Count: > 0 } copies)
            return;
        var camera = state.Camera.Position;
        var nearest = copies.MinBy(e => Vector3.DistanceSquared(e.Position, camera))!;
        state.Marker = null;
        state.Select(nearest);
        state.FocusOn(nearest);
    }

    private static string KindName(ArchetypeKind kind) => kind switch
    {
        ArchetypeKind.Time => L("Horaire"),
        ArchetypeKind.Interior => L("Intérieur"),
        _ => L("Objet"),
    };

    private void DrawDetails(Archetype type)
    {
        ImGui.Separator();
        if (!ImGui.BeginChild("##type_details", new Vector2(0, 0)))
        {
            ImGui.EndChild();
            return;
        }
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(Name(type.NameHash));
        ImGui.PopFont();
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Box}  Visionneuse")))
            openInViewer(type);
        ImGui.SameLine();
        int count = _placed.GetValueOrDefault(type.NameHash)?.Count ?? 0;
        if (Widgets.Button(L($"{Icons.MapPinned}  Aller sur la carte"), count > 0
                ? L($"Sélectionne l'exemplaire le plus proche de la caméra ({Widgets.Count(count)} sur la carte chargée).")
                : L("Aucun exemplaire sur la carte chargée."), enabled: count > 0))
            GoToNearest(type);

        if (Widgets.BeginProperties("##type_props"))
        {
            string model = type.AssetType switch
            {
                ArchetypeAssetType.Drawable => L($"{Name(type.AssetNameHash)} (.ydr)"),
                ArchetypeAssetType.Fragment => L($"{Name(type.AssetNameHash)} (.yft, cassable)"),
                ArchetypeAssetType.DrawableDictionary => L($"{Name(type.AssetNameHash)} dans {Name(type.DrawableDictionaryHash)}.ydd"),
                ArchetypeAssetType.Assetless => L("aucun (intérieur ou repère)"),
                _ => L("non indiqué"),
            };
            Widgets.PropertyText(L("Modèle"), model, L("Fichier 3D que le jeu affiche pour ce type."));
            Widgets.PropertyText(L("Textures"), type.TextureDictionaryHash == 0 ? L("aucune") : $"{Name(type.TextureDictionaryHash)}.ytd");
            if (type.PhysicsDictionaryHash != 0)
                Widgets.PropertyText(L("Collision"), $"{Name(type.PhysicsDictionaryHash)}.ybn",
                    L("Dictionnaire de collisions (ybn) qui contient la collision propre au type."));
            Widgets.PropertyText(L("Distance d'affichage"), $"{type.LodDistance:0} m");
            var size = type.BoundsMax - type.BoundsMin;
            Widgets.PropertyText(L("Taille"), $"{size.X:0.##} × {size.Y:0.##} × {size.Z:0.##} m");
            if (type.Kind == ArchetypeKind.Time)
                Widgets.PropertyText(L("Heures"), Hours(type.TimeFlags), L("Heures où le jeu affiche l'objet (ex. enseignes allumées la nuit)."));
            Widgets.PropertyText(L("Drapeaux"), $"0x{type.Flags:X8}");
            if (type.Kind == ArchetypeKind.Interior)
            {
                Widgets.PropertyText(L("Objets"), Widgets.Count(type.InteriorEntities.Count));
                Widgets.PropertyText(L("Pièces"), type.InteriorRooms.Count == 0 ? "0"
                    : $"{type.InteriorRooms.Count} : {string.Join(", ", type.InteriorRooms.Select(r => r.Name.Length > 0 ? r.Name : "?"))}");
                Widgets.PropertyText(L("Portails"), Widgets.Count(type.InteriorPortals.Count));
                Widgets.PropertyText(L("Ensembles"), type.InteriorEntitySets.Count == 0 ? "0"
                    : string.Join(", ", type.InteriorEntitySets.Select(s => $"{Name(s.NameHash)} ({s.Entities.Count})")),
                    L("Entity sets : groupes d'objets qu'un script affiche ou cache."));
            }
            Widgets.EndProperties();
        }
        ImGui.EndChild();
    }

    // Hours shown as ranges (e.g. 20 h - 6 h), a range over midnight kept whole.
    private static string Hours(uint flags)
    {
        bool On(int hour) => (flags & (1u << (hour % 24))) != 0;
        int first = Enumerable.Range(0, 24).FirstOrDefault(h => !On(h), -1);
        if (first < 0)
            return L("toujours");
        var ranges = new List<string>();
        int start = -1;
        for (int h = first; h <= first + 24; h++)
        {
            bool on = h < first + 24 && On(h);
            if (on && start < 0)
                start = h;
            else if (!on && start >= 0)
            {
                ranges.Add($"{start % 24} h - {h % 24} h");
                start = -1;
            }
        }
        return ranges.Count == 0 ? L("jamais") : string.Join(", ", ranges);
    }
}
