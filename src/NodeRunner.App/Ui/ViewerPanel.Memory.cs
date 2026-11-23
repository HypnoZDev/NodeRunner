using ImGuiNET;
using NodeRunner.App.Configuration;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// What the viewer keeps from one session to the next: list shown, search, display options, models opened.
internal sealed partial class ViewerPanel
{
    private const int MaxRecents = 30;

    private bool _settingsApplied;
    private ViewerCatalog? _restoredFor;

    // The list shows the favourites and recent models instead of everything.
    private bool _shortList;
    private int _favoritesRevision;
    private readonly Dictionary<string, ViewerItem?> _found = new(StringComparer.OrdinalIgnoreCase);
    private ViewerCatalog? _foundFor;

    private ViewerSettings Saved => state.Settings.Viewer;

    private void ApplySavedSettings()
    {
        if (_settingsApplied)
            return;
        _settingsApplied = true;
        var saved = Saved;
        _filter = Math.Clamp(saved.Filter, 0, SoundsFilter);
        _search = saved.Search ?? "";
        var viewer = state.Viewer;
        viewer.Shading = Enum.IsDefined((ViewerShading)saved.Shading) ? (ViewerShading)saved.Shading : ViewerShading.Textured;
        viewer.Edges = saved.Edges;
        viewer.ShowBounds = saved.ShowBounds;
        viewer.ShowGround = saved.ShowGround;
        viewer.AutoRotate = saved.AutoRotate;
        viewer.Background = Math.Clamp(saved.Background, 0, ModelViewer.Backgrounds.Length - 1);
        viewer.LightFollowsCamera = saved.LightFollowsCamera;
        viewer.LightAngle = saved.LightAngle;
        viewer.LightHeight = Math.Clamp(saved.LightHeight, 0.05f, 3f);
        viewer.FieldOfViewDegrees = Math.Clamp(saved.FieldOfView, 10f, 90f);
        _listWidth = Math.Clamp(saved.ListWidth, MinListWidth, MaxListWidth);
        _inspectorWidth = Math.Clamp(saved.InspectorWidth, MinInspectorWidth, MaxInspectorWidth);
        _inspectorOpen = saved.InspectorOpen;
        _order = Enum.IsDefined((ListOrder)saved.Order) ? (ListOrder)saved.Order : ListOrder.Name;
    }

    // Copied each frame; written to settings.json when NodeRunner closes.
    private void RememberSettings()
    {
        var saved = Saved;
        var viewer = state.Viewer;
        saved.Filter = _filter;
        saved.Search = _search;
        saved.Shading = (int)viewer.Shading;
        saved.Edges = viewer.Edges;
        saved.ShowBounds = viewer.ShowBounds;
        saved.ShowGround = viewer.ShowGround;
        saved.AutoRotate = viewer.AutoRotate;
        saved.Background = viewer.Background;
        saved.LightFollowsCamera = viewer.LightFollowsCamera;
        saved.LightAngle = viewer.LightAngle;
        saved.LightHeight = viewer.LightHeight;
        saved.FieldOfView = viewer.FieldOfViewDegrees;
        saved.ListWidth = _listWidth;
        saved.InspectorWidth = _inspectorWidth;
        saved.InspectorOpen = _inspectorOpen;
        saved.Order = (int)_order;
    }

    private static string? KeyOf(ViewerItem item) => item.Kind switch
    {
        ViewerKind.Vehicle => "v:" + item.Name,
        ViewerKind.Object => "o:" + item.Name,
        _ => null,
    };

    private List<string> SavedList(Dictionary<GameKind, List<string>> lists)
    {
        if (!lists.TryGetValue(state.ActiveGame, out var list))
            lists[state.ActiveGame] = list = [];
        return list;
    }

    private void AddRecent(ViewerItem item)
    {
        if (KeyOf(item) is not { } key)
            return;
        var recents = SavedList(Saved.Recents);
        recents.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        recents.Insert(0, key);
        if (recents.Count > MaxRecents)
            recents.RemoveRange(MaxRecents, recents.Count - MaxRecents);
    }

    // The catalogue's item saved under that key, in one list (0 vehicles, 1 objects, 2 library), null if it is no
    // longer there. Kept: the object list is long.
    private ViewerItem? Find(string key, int filter)
    {
        if (_catalog is not { } catalog || key.Length < 3 || (key[0] == 'v') != (filter == 0))
            return null;
        if (_foundFor != catalog)
        {
            _foundFor = catalog;
            _found.Clear();
        }
        string cacheKey = $"{filter}|{key}";
        if (_found.TryGetValue(cacheKey, out var found))
            return found;
        string name = key[2..];
        var source = filter switch { 0 => catalog.Vehicles, 1 => catalog.Objects, _ => catalog.Library };
        return _found[cacheKey] = source.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private ViewerItem? Find(string key) => Find(key, 0) ?? Find(key, 1) ?? Find(key, 2);

    private bool IsFavorite(ViewerItem item) =>
        KeyOf(item) is { } key && Saved.Favorites.TryGetValue(state.ActiveGame, out var list)
        && list.Exists(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));

    private void ToggleFavorite(ViewerItem item)
    {
        if (KeyOf(item) is not { } key)
            return;
        var favorites = SavedList(Saved.Favorites);
        if (favorites.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) == 0)
            favorites.Add(key);
        _favoritesRevision++;
        state.SaveSettings();
    }

    // The short list: favourites of the list shown, then the models opened lately (newest first).
    private List<ViewerItem> ShortList(string search)
    {
        var rows = new List<ViewerItem>();
        var seen = new HashSet<ViewerItem>();
        var words = SearchWords(search);
        foreach (var list in new[] { SavedList(Saved.Favorites), SavedList(Saved.Recents) })
        {
            foreach (string key in list)
            {
                if (Find(key, _filter) is { } item && seen.Add(item) && Matches(item, words))
                    rows.Add(item);
            }
        }
        return rows;
    }

    private void DrawFavoriteMenu(ViewerItem item)
    {
        if (!ImGui.BeginPopupContextItem("##row_menu"))
            return;
        bool favorite = IsFavorite(item);
        if (ImGui.MenuItem(favorite ? L($"{Icons.Star}  Retirer des favoris") : L($"{Icons.Star}  Ajouter aux favoris")))
            ToggleFavorite(item);
        if (ImGui.MenuItem(L($"{Icons.Copy}  Copier le nom")))
            ImGui.SetClipboardText(item.Name);
        if (item.Kind == ViewerKind.Object && item.Name.LastIndexOf('_') > 0
            && ImGui.MenuItem(L($"{Icons.Search}  Voir la même famille ({item.Name[..(item.Name.LastIndexOf('_') + 1)]}...)")))
            ShowFamily(item);
        ImGui.EndPopup();
    }

    // Once the list of a game is ready: its last model opened again.
    private void RestoreLastModel()
    {
        if (_catalog is not { } catalog || _restoredFor == catalog)
            return;
        _restoredFor = catalog;
        if (state.Viewer.Item is not null)
            return;
        foreach (string key in SavedList(Saved.Recents))
        {
            if (Find(key) is { } item)
            {
                Open(item);
                _scrollToItem = true;
                return;
            }
        }
    }

    // ---- History (back / forward, as in a web browser) ----

    private const int MaxHistory = 50;
    private readonly List<ViewerItem> _back = [], _forward = [];
    private bool _goingThroughHistory;

    private void AddToHistory(ViewerItem item)
    {
        if (_goingThroughHistory || state.Viewer.Item is not { } current || current == item)
            return;
        _back.Add(current);
        if (_back.Count > MaxHistory)
            _back.RemoveAt(0);
        _forward.Clear();
    }

    private void GoBack() => GoThroughHistory(_back, _forward);

    private void GoForward() => GoThroughHistory(_forward, _back);

    private void GoThroughHistory(List<ViewerItem> from, List<ViewerItem> to)
    {
        if (from.Count == 0)
            return;
        var item = from[^1];
        from.RemoveAt(from.Count - 1);
        if (state.Viewer.Item is { } current)
            to.Add(current);
        _goingThroughHistory = true;
        Open(item);
        _goingThroughHistory = false;
        // The list where it is, so that it shows selected.
        _filter = item.Kind == ViewerKind.Vehicle ? 0 : item.Pack is not null ? 2 : 1;
        _scrollToItem = true;
    }

    private void HandleCatalogKeys()
    {
        var keys = state.Keys;
        if (keys.Pressed(Input.KeyAction.ViewerBack))
            GoBack();
        else if (keys.Pressed(Input.KeyAction.ViewerForward))
            GoForward();
        if (_filter == SoundsFilter || Viewer.Item is null)
            return;
        if (keys.Pressed(Input.KeyAction.FrameModel))
            Viewer.Frame();
        if (keys.Pressed(Input.KeyAction.ViewerSpin))
            Viewer.AutoRotate = !Viewer.AutoRotate;
        if (keys.Pressed(Input.KeyAction.ViewerGround))
            Viewer.ShowGround = !Viewer.ShowGround;
        if (keys.Pressed(Input.KeyAction.ViewerBounds))
            Viewer.ShowBounds = !Viewer.ShowBounds;
        var io = ImGui.GetIO();
        if (io.KeyCtrl || io.KeyAlt)
            return;
        var views = Views;
        for (int i = 0; i < views.Length; i++)
        {
            if (ImGui.IsKeyPressed(ImGuiKey._1 + i, false) || ImGui.IsKeyPressed(ImGuiKey.Keypad1 + i, false))
                Viewer.SetView(views[i].Yaw, views[i].Pitch);
        }
    }
}
