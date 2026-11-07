using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;

namespace NodeRunner.App.Ui;

// Dockable "Visionneuse" panel: list of the vehicles and object types on the left (search), the model alone on the
// right, turned with the mouse (left drag: turn, wheel: zoom, double-click: default view).
internal sealed unsafe class ViewerPanel(AppState state)
{
    public const string Title = $"{Icons.Box}  Visionneuse###Visionneuse";

    private static string[] Filters => [L("Véhicules"), L("Objets")];
    private const float ListWidth = 250f;

    private ModelViewer Viewer => state.Viewer;

    private Task<ViewerCatalog>? _building;
    private ViewerCatalog? _catalog;
    private MapLibrary? _catalogLibrary;
    private int _filter;
    private string _search = "";
    private List<ViewerItem> _rows = [];
    private (int Filter, string Search, ViewerCatalog? Catalog) _rowsBuiltFor;
    private bool _scrollToItem;

    public void OpenArchetype(uint archetypeHash)
    {
        var world = state.World;
        if (world.Maps?.GetArchetype(archetypeHash) is not { } archetype || world.Models?.Locate(archetype) is not { } location)
            return;
        string name = world.Names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        Viewer.Open(new ViewerItem(name, ViewerKind.Object, location), world);
        _filter = 1;
        _search = "";
        _scrollToItem = true;
    }

    public void OpenFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var kind = ViewerCatalog.IsVehicleFile(path) ? ViewerKind.Vehicle : ViewerKind.Object;
        Viewer.Open(new ViewerItem(name, kind, new ModelLocation(path, Joaat.Hash(name))), state.World);
        _filter = kind == ViewerKind.Vehicle ? 0 : 1;
        _search = "";
        _scrollToItem = true;
    }

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(960f, 580f), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool visible = ImGui.Begin(L(Title), ref open);
        ImGui.PopStyleVar();
        Viewer.RequestedSize = Vector2.Zero; // Set again below while the view is visible.
        if (visible)
        {
            UpdateCatalog();
            if (ImGui.BeginChild("##viewer_list", new Vector2(ListWidth, 0), ImGuiChildFlags.None))
                DrawList();
            ImGui.EndChild();
            ImGui.SameLine();
            if (ImGui.BeginChild("##viewer_view", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                DrawView();
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void UpdateCatalog()
    {
        var world = state.World;
        if (world.Maps != _catalogLibrary)
        {
            _catalogLibrary = world.Maps;
            _catalog = null;
            _building = world.Maps is null ? null : Task.Run(() => ViewerCatalog.Build(world));
        }
        if (_building is { IsCompleted: true } finished)
        {
            _building = null;
            _catalog = finished.Result;
        }
    }

    private void DrawList()
    {
        string[] labels = _catalog is { } c
            ? [$"{Filters[0]} ({Widgets.Count(c.Vehicles.Count)})", $"{Filters[1]} ({Widgets.Count(c.Objects.Count)})"]
            : Filters;
        Widgets.Segmented("viewer_filter", labels, ref _filter,
            [L("Tous les véhicules du jeu, DLC compris (nom de spawn)"), L("Tous les types d'objets de la carte (archétypes)")]);
        ImGui.SetNextItemWidth(-float.Epsilon);
        Widgets.SearchField("##viewer_search", _filter == 0 ? L("Véhicule (ex. adder)") : L("Type (ex. prop_bench)"), ref _search);

        if (_catalog is null)
        {
            Widgets.Hint(state.World.Maps is null ? L("En attente du chargement de la carte...") : L("Préparation de la liste..."));
            return;
        }

        UpdateRows();
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

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(_rows.Count, rowHeight);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var item = _rows[i];
                ImGui.PushID(i);
                string icon = item.Kind == ViewerKind.Vehicle ? Icons.Car : Icons.Box;
                if (ImGui.Selectable($"{icon}  {item.Name}", item.Location == Viewer.Item?.Location))
                    Viewer.Open(item, state.World);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(item.Location.Path);
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    private void UpdateRows()
    {
        if (_rowsBuiltFor == (_filter, _search, _catalog))
            return;
        _rowsBuiltFor = (_filter, _search, _catalog);
        var source = _filter == 0 ? _catalog!.Vehicles : _catalog!.Objects;
        string search = _search.Trim().ToLowerInvariant();
        _rows = search.Length == 0
            ? source.ToList()
            : source.Where(r => r.Name.Contains(search, StringComparison.Ordinal))
                .OrderBy(r => r.Name.StartsWith(search, StringComparison.Ordinal) ? 0 : 1)
                .ToList();
    }

    private void DrawView()
    {
        float infoHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var size = ImGui.GetContentRegionAvail() - new Vector2(0, infoHeight);
        size = new Vector2(MathF.Floor(Math.Max(size.X, 2)), MathF.Floor(Math.Max(size.Y, 2)));
        Viewer.RequestedSize = size * UiTheme.Scale; // In pixels, for a sharp image.

        ImGui.InvisibleButton("##viewer_image", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        if (Viewer.ImageId != IntPtr.Zero)
            drawList.AddImage(Viewer.ImageId, min, max);
        else
            drawList.AddRectFilled(min, max, Widgets.Col(UiTheme.Field));
        HandleMouse();

        // Name, then loading / error, over the top-left corner of the image.
        var pos = min + new Vector2(12, 10);
        if (Viewer.Item is { } item)
        {
            ImGui.PushFont(UiTheme.TitleFont);
            drawList.AddText(pos, Widgets.Col(UiTheme.Text), item.Name);
            pos.Y += ImGui.GetTextLineHeight() + 2;
            ImGui.PopFont();
        }
        string? status = Viewer.IsLoading ? L($"{Icons.LoaderCircle}  Chargement...")
            : Viewer.Error is { } error ? L($"{Icons.TriangleAlert}  Impossible d'afficher ce modèle : {error}")
            : Viewer.Item is null ? L("Choisis un véhicule ou un objet dans la liste.") : null;
        if (status is not null)
            drawList.AddText(pos, Widgets.Col(Viewer.Error is not null ? UiTheme.Error : UiTheme.TextMuted), status);
        drawList.AddText(new Vector2(min.X + 12, max.Y - ImGui.GetTextLineHeight() - 8), Widgets.Col(UiTheme.TextFaint),
            L("Clic gauche glissé : tourner · molette : zoom · double-clic : vue par défaut"));

        DrawInfo();
    }

    private void HandleMouse()
    {
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            Viewer.Yaw -= io.MouseDelta.X * 0.008f;
            Viewer.Pitch -= io.MouseDelta.Y * 0.008f;
        }
        if (!ImGui.IsItemHovered())
            return;
        if (io.MouseWheel != 0)
            Viewer.Distance = Math.Clamp(Viewer.Distance * MathF.Pow(0.88f, io.MouseWheel), Viewer.MinDistance, Viewer.MaxDistance);
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Viewer.ResetView();
    }

    private void DrawInfo()
    {
        if (Viewer.Item is not { } item || Viewer.TriangleCount == 0)
            return;
        var s = Viewer.Size;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.TextMuted, L($"{s.X:0.0} x {s.Y:0.0} x {s.Z:0.0} m · {Widgets.Count(Viewer.TriangleCount)} triangles · ")
            + L($"{Viewer.MeshCount} maillage(s) · {Viewer.TextureCount} texture(s)")
            + (Viewer.MissingTextureCount > 0 ? L($", {Viewer.MissingTextureCount} introuvable(s)") : ""));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L($"Fichier : {item.Location.Path}")
                + (Viewer.MissingTextureCount > 0 ? L($"\nTextures introuvables : {string.Join(", ", Viewer.MissingTextures)}") : ""));

        bool canPlace = item.Kind == ViewerKind.Object && state.World.Maps is not null;
        float buttons = ImGui.CalcTextSize(L("Copier le nom")).X + ImGui.CalcTextSize(L("Vue par défaut")).X + 80f
                        + (canPlace ? ImGui.CalcTextSize(L("Placer")).X + 50f : 0f);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - buttons));
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
        if (Widgets.Button(L($"{Icons.Camera}  Vue par défaut"), L("Revenir à la vue de départ (double-clic sur l'image)")))
            Viewer.ResetView();
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom"), item.Kind == ViewerKind.Vehicle ? L("Nom de spawn du véhicule (ex. pour un script FiveM)") : L("Nom du type d'objet")))
            ImGui.SetClipboardText(item.Name);
    }
}
