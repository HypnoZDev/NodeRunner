using NodeRunner.App.Configuration;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// What the viewer keeps from one session to the next: list shown, search, display options, models opened.
internal sealed partial class ViewerPanel
{
    private const int MaxRecents = 30;

    private bool _settingsApplied;
    private ViewerCatalog? _restoredFor;

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

    // The catalogue's item saved under that key (null if it is no longer in the game or the library).
    private ViewerItem? Find(string key)
    {
        if (_catalog is not { } catalog || key.Length < 3)
            return null;
        string name = key[2..];
        bool Same(ViewerItem i) => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        return key[0] == 'v'
            ? catalog.Vehicles.FirstOrDefault(Same)
            : catalog.Objects.FirstOrDefault(Same) ?? catalog.Library.FirstOrDefault(Same);
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
}
