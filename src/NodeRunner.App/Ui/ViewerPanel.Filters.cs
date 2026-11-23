using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;

namespace NodeRunner.App.Ui;

// Filters of the viewer's list: vehicle class, DLC pack, kind of object type, and the order (name, size, DLC).
internal sealed partial class ViewerPanel
{
    private enum ListOrder { Name, Size, Dlc }

    // What the filters look at, per item of the catalogue (read in the background: the object list is long).
    private sealed record Facets(string Dlc, string Class, ArchetypeKind? Kind, float Size);

    private Dictionary<ViewerItem, Facets>? _facets;
    private Task<Dictionary<ViewerItem, Facets>>? _facetsTask;
    private ViewerCatalog? _facetsFor;
    private bool _facetsWithClasses;

    private string? _dlcFilter, _classFilter;
    private ArchetypeKind? _kindFilter;
    private ListOrder _order;
    private int _filtersRevision;
    // Choices of each filter with their count, for the list shown.
    private (int Filter, Dictionary<ViewerItem, Facets> Facets, List<(string Value, int Count)> Dlcs, List<(string Value, int Count)> Classes, List<(ArchetypeKind Value, int Count)> Kinds)? _choices;

    private bool FiltersActive => _dlcFilter is not null || _filter == 0 && _classFilter is not null || _filter != 0 && _kindFilter is not null;

    private void UpdateFacets()
    {
        if (_catalog is not { } catalog)
            return;
        // The vehicle sheets may be read after the list: their classes then.
        bool classes = state.IsOtherGame || Vehicles is not null;
        if ((_facetsFor != catalog || classes && !_facetsWithClasses) && _facetsTask is null)
        {
            _facetsFor = catalog;
            _facetsWithClasses = classes;
            var library = Library;
            var info = state.IsOtherGame ? null : Vehicles?.Info;
            bool other = state.IsOtherGame;
            _facetsTask = Task.Run(() => BuildFacets(catalog, library, info, other));
        }
        if (_facetsTask is { IsCompleted: true } done)
        {
            _facetsTask = null;
            if (done.IsCompletedSuccessfully && _facetsFor == catalog)
            {
                _facets = done.Result;
                _filtersRevision++;
            }
            else if (!done.IsCompletedSuccessfully && _facetsRetries++ < 3)
            {
                // The map changed while it was read (a project opened): read again.
                _facetsFor = null;
            }
        }
    }

    private int _facetsRetries;

    private static Dictionary<ViewerItem, Facets> BuildFacets(ViewerCatalog catalog, MapLibrary? library, VehicleInfoCatalog? info, bool otherGame)
    {
        var facets = new Dictionary<ViewerItem, Facets>(catalog.Vehicles.Count + catalog.Objects.Count + catalog.Library.Count, ReferenceEqualityComparer.Instance);
        foreach (var item in catalog.Vehicles.Concat(catalog.Objects).Concat(catalog.Library))
        {
            string dlc = item.Pack is { } pack ? (pack.Length == 0 ? L("(racine de la bibliothèque)") : pack) : otherGame ? "" : PackOf(item.Location.Path);
            string vehicleClass = "";
            if (item.Kind == ViewerKind.Vehicle && info?.VehicleOf(item.Location.ModelHash) is { Count: > 0 } sheet)
                vehicleClass = ClassName(VehicleInfoCatalog.Value(sheet, "vehicleClass"));
            var archetype = library?.GetArchetype(item.ArchetypeHash);
            float size = archetype is null ? 0f : (archetype.BoundsMax - archetype.BoundsMin).Length();
            facets[item] = new Facets(dlc, vehicleClass, archetype?.Kind, size);
        }
        return facets;
    }

    private bool PassesFilters(ViewerItem item)
    {
        if (!FiltersActive || _facets is null || !_facets.TryGetValue(item, out var f))
            return true;
        if (_dlcFilter is not null && f.Dlc != _dlcFilter)
            return false;
        if (_filter == 0 && _classFilter is not null && f.Class != _classFilter)
            return false;
        if (_filter != 0 && _kindFilter is not null && f.Kind != _kindFilter)
            return false;
        return true;
    }

    private IEnumerable<ViewerItem> Ordered(IEnumerable<ViewerItem> rows)
    {
        if (_order == ListOrder.Name || _facets is not { } facets)
            return rows;
        Facets Of(ViewerItem i) => facets.GetValueOrDefault(i) ?? new Facets("", "", null, 0f);
        return _order == ListOrder.Size
            ? rows.OrderByDescending(i => Of(i).Size)
            : rows.OrderBy(i => Of(i).Dlc, StringComparer.OrdinalIgnoreCase);
    }

    private void ClearFilters()
    {
        _dlcFilter = _classFilter = null;
        _kindFilter = null;
        _filtersRevision++;
    }

    // "Filtrer" under the search, and the popup with the filters.
    private void DrawFilterBar()
    {
        UpdateFacets();
        int count = (_dlcFilter is not null ? 1 : 0) + (_filter == 0 && _classFilter is not null ? 1 : 0) + (_filter != 0 && _kindFilter is not null ? 1 : 0);
        string label = count > 0 ? L($"{Icons.SlidersHorizontal}  Filtres ({count})") : L($"{Icons.SlidersHorizontal}  Filtrer et trier");
        if (_order != ListOrder.Name)
            label += _order == ListOrder.Size ? L(" · par taille") : L(" · par DLC");
        if (Widgets.Button(label + "##viewer_filters", _filter == 0 ? L("Classe, DLC, ordre de la liste") : L("DLC, sorte d'objet, ordre de la liste")))
            ImGui.OpenPopup("##viewer_filters_popup");
        if (count > 0 || _order != ListOrder.Name)
        {
            ImGui.SameLine(0, 4f);
            if (Widgets.IconButton(Icons.X, "viewer_filters_clear", L("Retirer les filtres et revenir à l'ordre par nom")))
            {
                ClearFilters();
                _order = ListOrder.Name;
            }
        }
        DrawFiltersPopup();
    }

    private void DrawFiltersPopup()
    {
        if (!ImGui.BeginPopup("##viewer_filters_popup"))
            return;
        if (_facets is not { } facets)
        {
            Widgets.Hint(L("Préparation des filtres..."));
            ImGui.EndPopup();
            return;
        }
        if (_choices is not { } choices || choices.Filter != _filter || choices.Facets != facets)
        {
            var source = _filter switch { 0 => _catalog!.Vehicles, 1 => _catalog!.Objects, _ => _catalog!.Library };
            var list = source.Select(i => facets.GetValueOrDefault(i)).OfType<Facets>().ToList();
            choices = (_filter, facets,
                list.Where(f => f.Dlc.Length > 0).GroupBy(f => f.Dlc).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                list.Where(f => f.Class.Length > 0).GroupBy(f => f.Class).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key, StringComparer.CurrentCulture).ToList(),
                list.Where(f => f.Kind is not null).GroupBy(f => f.Kind!.Value).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key).ToList());
            _choices = choices;
        }
        var (_, _, dlcs, classes, kinds) = choices;

        if (_filter == 0 && classes.Count > 0)
            Choice(L("Classe"), "class", classes, ref _classFilter, v => v);
        if (dlcs.Count > 1)
            Choice(_filter == 2 ? L("Pack") : L("DLC"), "dlc", dlcs, ref _dlcFilter, v => v);
        if (_filter != 0 && kinds.Count > 1)
        {
            string? kind = _kindFilter?.ToString();
            var named = kinds.Select(k => (k.Value.ToString(), k.Count)).ToList();
            Choice(L("Sorte"), "kind", named, ref kind, v => KindName(Enum.Parse<ArchetypeKind>(v)));
            _kindFilter = kind is null ? null : Enum.Parse<ArchetypeKind>(kind);
        }

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.TextMuted, L("Ordre"));
        int order = (int)_order;
        if (ImGui.RadioButton(L("Nom"), ref order, 0)) { }
        ImGui.SameLine();
        if (ImGui.RadioButton(L("Taille (grands d'abord)"), ref order, 1)) { }
        ImGui.SameLine();
        if (ImGui.RadioButton(L("DLC"), ref order, 2)) { }
        if (order != (int)_order)
        {
            _order = (ListOrder)order;
            _filtersRevision++;
        }
        ImGui.EndPopup();
    }

    // A filter as a list to choose from; "Tous" lets everything through.
    private void Choice(string title, string id, List<(string Value, int Count)> values, ref string? chosen, Func<string, string> name)
    {
        ImGui.TextColored(UiTheme.TextMuted, title);
        ImGui.SetNextItemWidth(280f);
        string preview = chosen is null ? L("Tous") : name(chosen);
        if (ImGui.BeginCombo($"##viewer_filter_{id}", preview, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(L("Tous"), chosen is null))
            {
                chosen = null;
                _filtersRevision++;
            }
            foreach (var (value, count) in values)
            {
                if (ImGui.Selectable($"{name(value)}  ({Widgets.Count(count)})##{value}", chosen == value))
                {
                    chosen = value;
                    _filtersRevision++;
                }
            }
            ImGui.EndCombo();
        }
    }

    // Same family: the name without its last part (prop_bench_01a -> prop_bench_).
    private void ShowFamily(ViewerItem item)
    {
        int cut = item.Name.LastIndexOf('_');
        if (cut <= 0)
            return;
        _search = item.Name[..(cut + 1)];
        _shortList = false;
    }
}
