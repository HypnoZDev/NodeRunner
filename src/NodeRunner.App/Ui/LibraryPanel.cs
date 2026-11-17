using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;

namespace NodeRunner.App.Ui;

// Prop library (docked under the 3D view): pictures of the addon props shared by every project, filtered by pack or
// favourites; double-click or drag into the 3D view to place one.
internal sealed unsafe class LibraryPanel(AppState state, Action<uint> openInViewer)
{
    public const string Title = $"{Icons.LibraryBig}  Bibliothèque###Bibliothèque";

    private const float MinTile = 72f;
    private const float MaxTile = 160f;
    private const string FavoritesFilter = "\u0001favorites";

    private MapLibrary? _builtFor;
    private Task<List<ViewerItem>>? _building;
    private List<ViewerItem> _all = [];
    private List<ViewerItem> _rows = [];
    private (string Search, string? Pack, int Favorites, List<ViewerItem> All) _rowsBuiltFor;
    private List<(string Pack, int Count)> _packs = [];
    private string _search = "";
    private string? _pack;
    private ViewerItem? _selected;
    private ViewerItem? _dragging;
    private float _tile = 96f;

    public int Count => _all.Count;

    public void Draw(ref bool open)
    {
        DropInScene();
        bool visible = ImGui.Begin(L(Title), ref open);
        if (visible && !state.Features.Editing)
        {
            Widgets.Hint(L("La bibliothèque place des props dans un projet GTA V : indisponible dans ce jeu."), wrap: true);
        }
        else if (visible)
        {
            Update();
            DrawToolbar();
            if (_all.Count == 0)
                DrawEmpty();
            else
                DrawGrid();
        }
        ImGui.End();
    }

    private void Update()
    {
        var world = state.World;
        if (world.Maps != _builtFor)
        {
            _builtFor = world.Maps;
            _building = world.Maps is null ? null : Task.Run(() => ViewerCatalog.LibraryItems(world));
        }
        if (_building is { IsCompleted: true } done)
        {
            _building = null;
            _all = done.Result;
            _packs = _all.GroupBy(i => i.Pack ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count())).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (_pack is not null && _pack != FavoritesFilter && !_packs.Any(p => string.Equals(p.Pack, _pack, StringComparison.OrdinalIgnoreCase)))
                _pack = null;
            if (_selected is not null)
                _selected = _all.FirstOrDefault(i => i.Name == _selected.Name);
        }
    }

    private HashSet<string> Favorites => _favorites.Count == state.Settings.LibraryFavorites.Count
        ? _favorites
        : _favorites = state.Settings.LibraryFavorites.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);

    private bool IsFavorite(ViewerItem item) => Favorites.Contains(item.Name);

    private void ToggleFavorite(ViewerItem item)
    {
        var list = state.Settings.LibraryFavorites;
        if (list.RemoveAll(n => string.Equals(n, item.Name, StringComparison.OrdinalIgnoreCase)) == 0)
            list.Add(item.Name);
        _favorites = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rowsBuiltFor = default;
        state.SaveSettings();
    }

    private void DrawToolbar()
    {
        Widgets.SearchField("##library_search", L("Prop ou pack"), ref _search, width: Math.Min(240f, ImGui.GetContentRegionAvail().X * 0.3f));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(220f, ImGui.GetContentRegionAvail().X * 0.3f));
        string current = _pack switch
        {
            null => L($"Tous les packs ({_all.Count})"),
            FavoritesFilter => L($"{Icons.Star}  Favoris ({_all.Count(IsFavorite)})"),
            "" => L("(racine de la bibliothèque)"),
            _ => _pack,
        };
        if (ImGui.BeginCombo("##library_pack", current))
        {
            if (ImGui.Selectable(L($"Tous les packs ({_all.Count})"), _pack is null))
                _pack = null;
            if (ImGui.Selectable(L($"{Icons.Star}  Favoris ({_all.Count(IsFavorite)})"), _pack == FavoritesFilter))
                _pack = FavoritesFilter;
            ImGui.Separator();
            foreach (var (pack, count) in _packs)
            {
                if (ImGui.Selectable($"{(pack.Length == 0 ? L("(racine de la bibliothèque)") : pack)} ({count})##pack_{pack}", _pack == pack))
                    _pack = pack;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        bool canPlace = _selected is not null && state.Project is not null;
        if (Widgets.Button(L($"{Icons.MapPinned}  Placer##library_place"),
                _selected is null ? L("Choisis d'abord un prop dans la liste.")
                : state.Project is null ? L("Ouvre ou crée d'abord un projet.")
                : L("Pose le prop au centre de la vue 3D (aussi : double-clic, ou glisser la vignette dans la vue 3D)."), enabled: canPlace))
            Place(_selected!, null);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Box}  Visionneuse##library_view"), L("Ouvre le prop dans la visionneuse (pièces, textures, capture)."), enabled: _selected is not null))
            openInViewer(_selected!.ArchetypeHash);

        // Pack actions and picture size, on the right (on a second line when the panel is narrow).
        float last = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        float actions = 4 * 30f + 130f;
        if (last + 8f + actions <= right)
            ImGui.SameLine(right - actions);
        ImGui.SetNextItemWidth(110f);
        ImGui.SliderFloat("##library_size", ref _tile, MinTile, MaxTile, "");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("Taille des vignettes"));
        ImGui.SameLine(0, 6f);
        if (Widgets.IconButton(Icons.ArrowDownToLine, "library_add", L("Ajouter un pack (dossier d'une ressource FiveM ou dossier stream)...")))
            state.LibraryRequest = LibraryAction.AddFolder;
        ImGui.SameLine(0, 2f);
        if (Widgets.IconButton(Icons.Package, "library_zip", L("Ajouter un pack livré en .zip...")))
            state.LibraryRequest = LibraryAction.AddZip;
        ImGui.SameLine(0, 2f);
        if (Widgets.IconButton(Icons.FolderOpen, "library_open", L("Ouvrir le dossier de la bibliothèque")))
            ViewerPanel.OpenLibraryFolder(state);
        ImGui.SameLine(0, 2f);
        if (Widgets.IconButton(Icons.RefreshCw, "library_reload", L("Relire la bibliothèque (recharge la carte)")))
            state.LibraryRequest = LibraryAction.Reload;
    }

    private void DrawEmpty()
    {
        ImGui.Dummy(new Vector2(0, 8));
        if (state.World.Maps is null || _building is not null)
        {
            Widgets.Hint(L("En attente du chargement de la carte..."));
            return;
        }
        Widgets.Hint(L("Bibliothèque vide. Ajoute un pack de props addons (dossier d'une ressource FiveM ou .zip avec ses .ytyp, .ydr et .ytd) : ses props seront disponibles dans tous les projets."), wrap: true);
        Widgets.Hint(L("Astuce : on peut aussi glisser le dossier ou le .zip du pack sur la fenêtre de NodeRunner."), wrap: true);
        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.PrimaryButton(L($"{Icons.ArrowDownToLine}  Ajouter un pack...##library_empty")))
            state.LibraryRequest = LibraryAction.AddFolder;
        ImGui.SameLine();
        if (Widgets.Button(L(".zip...##library_empty_zip")))
            state.LibraryRequest = LibraryAction.AddZip;
    }

    private void UpdateRows()
    {
        int favorites = state.Settings.LibraryFavorites.Count;
        if (_rowsBuiltFor.All == _all && _rowsBuiltFor.Search == _search && _rowsBuiltFor.Pack == _pack && _rowsBuiltFor.Favorites == favorites)
            return;
        _rowsBuiltFor = (_search, _pack, favorites, _all);
        string search = _search.Trim();
        IEnumerable<ViewerItem> rows = _pack switch
        {
            null => _all,
            FavoritesFilter => _all.Where(IsFavorite),
            _ => _all.Where(i => string.Equals(i.Pack, _pack, StringComparison.OrdinalIgnoreCase)),
        };
        if (search.Length > 0)
            rows = rows.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                   || i.Pack?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
        // Favourites first.
        _rows = rows.OrderBy(i => IsFavorite(i) ? 0 : 1).ToList();
    }

    private void DrawGrid()
    {
        UpdateRows();
        if (_rows.Count == 0)
        {
            Widgets.Hint(_pack == FavoritesFilter ? L("Aucun favori : survole une vignette et clique sur l'étoile.") : L("Aucun résultat"));
            return;
        }
        if (!ImGui.BeginChild("##library_grid", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }
        float spacing = 6f;
        float tileHeight = _tile + ImGui.GetTextLineHeight() + 6f;
        int columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (_tile + spacing)));
        int rowCount = (_rows.Count + columns - 1) / columns;
        var drawList = ImGui.GetWindowDrawList();

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rowCount, tileHeight + spacing);
        while (clipper.Step())
        {
            for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    if (index >= _rows.Count)
                        break;
                    if (column > 0)
                        ImGui.SameLine(0, spacing);
                    DrawTile(drawList, _rows[index], index, tileHeight);
                }
                ImGui.Dummy(new Vector2(0, spacing - ImGui.GetStyle().ItemSpacing.Y));
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    private void DrawTile(ImDrawListPtr drawList, ViewerItem item, int index, float tileHeight)
    {
        ImGui.PushID(index);
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##tile", new Vector2(_tile, tileHeight));
        bool hovered = ImGui.IsItemHovered();
        var max = min + new Vector2(_tile, tileHeight);
        bool selected = _selected?.Name == item.Name;
        bool favorite = IsFavorite(item);

        // Star in the top-right corner: favourite (always shown) or to add (on hover).
        float starSize = ImGui.GetTextLineHeight() + 4f;
        var starMin = new Vector2(max.X - starSize - 2f, min.Y + 2f);
        bool overStar = hovered && ImGui.IsMouseHoveringRect(starMin, starMin + new Vector2(starSize));

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (overStar)
                ToggleFavorite(item);
            else
                _selected = item;
        }
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !overStar)
            Place(item, null);

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
        {
            ImGui.SetDragDropPayload("NR_LIBRARY_PROP", IntPtr.Zero, 0);
            _dragging = item;
            _selected = item;
            ImGui.TextUnformatted(item.Name);
            ImGui.TextDisabled(state.Project is null ? L("Ouvre d'abord un projet pour placer ce prop.") : L("Relâche dans la vue 3D pour le poser à cet endroit."));
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginPopupContextItem("##tile_menu"))
        {
            _selected = item;
            if (ImGui.MenuItem(L($"{Icons.MapPinned}  Placer au centre de la vue"), null, false, state.Project is not null))
                Place(item, null);
            if (ImGui.MenuItem(L($"{Icons.Box}  Voir dans la visionneuse")))
                openInViewer(item.ArchetypeHash);
            if (ImGui.MenuItem(L($"{Icons.Copy}  Copier le nom")))
                ImGui.SetClipboardText(item.Name);
            if (ImGui.MenuItem(favorite ? L($"{Icons.Star}  Retirer des favoris") : L($"{Icons.Star}  Ajouter aux favoris")))
                ToggleFavorite(item);
            if (ImGui.MenuItem(L($"{Icons.FolderOpen}  Ouvrir le dossier du pack")))
                OpenPackFolder(item);
            ImGui.EndPopup();
        }

        if (hovered && !overStar && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
        {
            string pack = string.IsNullOrEmpty(item.Pack) ? L("(racine de la bibliothèque)") : item.Pack;
            ImGui.SetTooltip(L($"{item.Name}\nPack : {pack}\nDouble-clic : placer · glisser dans la vue 3D : placer à cet endroit · clic droit : plus"));
        }

        // Background, picture, name.
        uint background = ImGui.GetColorU32(selected ? UiTheme.Accent with { W = 0.35f } : hovered ? UiTheme.FieldHovered : UiTheme.Field);
        drawList.AddRectFilled(min, max, background, 4f);
        if (selected)
            drawList.AddRect(min, max, ImGui.GetColorU32(UiTheme.Accent), 4f, ImDrawFlags.None, 1.5f);
        var pictureMin = min + new Vector2(4f, 4f);
        var pictureMax = new Vector2(max.X - 4f, min.Y + _tile - 4f);
        var picture = state.Thumbnails.Get(item, state.World);
        if (picture != IntPtr.Zero)
            drawList.AddImage(picture, pictureMin, pictureMax);
        else
        {
            var size = ImGui.CalcTextSize(Icons.Box);
            drawList.AddText((pictureMin + pictureMax - size) * 0.5f, ImGui.GetColorU32(UiTheme.TextMuted), Icons.Box);
        }
        string name = Widgets.Fit(item.Name, _tile - 8f);
        var nameSize = ImGui.CalcTextSize(name);
        drawList.AddText(new Vector2(min.X + (_tile - nameSize.X) * 0.5f, min.Y + _tile), ImGui.GetColorU32(selected ? UiTheme.Text : UiTheme.TextMuted), name);
        if (favorite || hovered)
        {
            var color = favorite ? new Vector4(1f, 0.78f, 0.25f, 1f) : overStar ? UiTheme.Text : UiTheme.TextMuted;
            drawList.AddText(starMin + new Vector2(2f, 2f), ImGui.GetColorU32(color), Icons.Star);
        }
        ImGui.PopID();
    }

    private void Place(ViewerItem item, Vector2? at)
    {
        _selected = item;
        if (state.Project is null)
        {
            state.Notify(L("Ouvre ou crée d'abord un projet pour placer un prop."), warning: true);
            return;
        }
        state.PlacePoint = at;
        state.PlaceRequest = item.ArchetypeHash;
    }

    // A picture dragged out of the panel and let go over the 3D view: the prop goes where the mouse points.
    private void DropInScene()
    {
        if (_dragging is not { } item)
            return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;
        _dragging = null;
        var (position, size) = DockLayout.SceneArea;
        var mouse = ImGui.GetMousePos();
        if (size.X < 1 || size.Y < 1 || mouse.X < position.X || mouse.Y < position.Y || mouse.X > position.X + size.X || mouse.Y > position.Y + size.Y)
            return;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow))
            return;
        Place(item, (mouse - position) / size);
    }

    private void OpenPackFolder(ViewerItem item)
    {
        string? disk = state.World.ProjectFiles?.LibraryFileOf(item.Location.Path)?.Disk;
        string folder = disk is null ? state.LibraryFolder : Path.GetDirectoryName(disk)!;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            state.Notify(ex.Message, warning: true);
        }
    }
}
