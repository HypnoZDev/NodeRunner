using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;

namespace NodeRunner.App.Ui;

// Dockable "Visionneuse" panel: list of the vehicles and object types on the left (search), the model in the middle
// (turn, pan, zoom, display options, captures), and an inspector on the right: paint (vehicles), parts, textures,
// vehicle sheet or object type, file.
internal sealed unsafe class ViewerPanel(AppState state)
{
    public const string Title = $"{Icons.Box}  Visionneuse###Visionneuse";

    private static string[] Filters => [L("Véhicules"), L("Objets")];
    private const float ListWidth = 230f;
    private const float InspectorWidth = 340f;

    private static VehiclePaintSlot[] PaintSlots => VehiclePaintColors.Slots;
    private static int[] DefaultPaint => VehiclePaintColors.Default;

    private ModelViewer Viewer => state.Viewer;

    private Task<ViewerCatalog>? _building;
    private ViewerCatalog? _catalog;
    private MapLibrary? _catalogLibrary;
    private int _filter;
    private string _search = "";
    private List<ViewerItem> _rows = [];
    private (int Filter, string Search, ViewerCatalog? Catalog) _rowsBuiltFor;
    private bool _scrollToItem;

    private readonly int[] _paint = (int[])DefaultPaint.Clone();
    private int _paintSet;
    private ViewerItem? _paintFor;
    private bool _paintOn = true;
    private int _pickingSlot = -1;
    private string _paletteSearch = "";
    private int _paletteFamily;
    private readonly Random _random = new();

    private int _selectedMesh = -1;
    private string _partSearch = "";
    private PreviewTexture? _openTexture;
    private int _captureScale = 2;
    private bool _captureTransparent;

    public void OpenArchetype(uint archetypeHash)
    {
        var world = state.World;
        if (world.Maps?.GetArchetype(archetypeHash) is not { } archetype || world.Models?.Locate(archetype) is not { } location)
            return;
        string name = world.Names?.Describe(archetypeHash) ?? $"0x{archetypeHash:X8}";
        Open(new ViewerItem(name, ViewerKind.Object, location));
        _filter = 1;
        _search = "";
        _scrollToItem = true;
    }

    public void OpenFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var kind = ViewerCatalog.IsVehicleFile(path) ? ViewerKind.Vehicle : ViewerKind.Object;
        Open(new ViewerItem(name, kind, new ModelLocation(path, Joaat.Hash(name))));
        _filter = kind == ViewerKind.Vehicle ? 0 : 1;
        _search = "";
        _scrollToItem = true;
    }

    private void Open(ViewerItem item)
    {
        if (item != Viewer.Item)
        {
            _selectedMesh = -1;
            _openTexture = null;
        }
        Viewer.Open(item, state.World);
    }

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(1240f, 700f), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool visible = ImGui.Begin(L(Title), ref open);
        ImGui.PopStyleVar();
        Viewer.RequestedSize = Vector2.Zero; // Set again below while the view is visible.
        if (visible)
        {
            UpdateCatalog();
            UpdatePaint();
            ShowCaptureResult();
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float available = ImGui.GetContentRegionAvail().X;
            bool inspector = available > ListWidth + InspectorWidth + 300f;
            if (ImGui.BeginChild("##viewer_list", new Vector2(ListWidth, 0), ImGuiChildFlags.None))
                DrawList();
            ImGui.EndChild();
            ImGui.SameLine();
            float viewWidth = inspector ? available - ListWidth - InspectorWidth - spacing * 2 : 0f;
            if (ImGui.BeginChild("##viewer_view", new Vector2(viewWidth, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                DrawView();
            ImGui.EndChild();
            if (inspector)
            {
                ImGui.SameLine();
                if (ImGui.BeginChild("##viewer_inspector", Vector2.Zero, ImGuiChildFlags.None))
                    DrawInspector();
                ImGui.EndChild();
            }
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

    // ---- Paint ----

    private VehicleData? Vehicles => state.World.Vehicles;

    private VehicleVariation? Variation =>
        Viewer.Item is { Kind: ViewerKind.Vehicle } item ? Vehicles?.Paints.VariationOf(item.Location.ModelHash) : null;

    // A vehicle opened: its first planned combination, once the palette is read.
    private void UpdatePaint()
    {
        var item = Viewer.Item;
        if (item?.Kind != ViewerKind.Vehicle)
        {
            Viewer.PaintColors = null;
            return;
        }
        if (Vehicles is not { } vehicles)
        {
            Viewer.PaintColors = null;
            return;
        }
        if (_paintFor != item)
        {
            _paintFor = item;
            _paintSet = -1;
            if (vehicles.Paints.VariationOf(item.Location.ModelHash) is { ColorSets.Count: > 0 })
                ApplySet(0);
            else
                DefaultPaint.CopyTo(_paint, 0);
        }
        Viewer.PaintColors = _paintOn ? VehiclePaintColors.From(vehicles.Paints, _paint) : null;
    }

    private void ApplySet(int index)
    {
        if (Variation is not { } variation || index < 0 || index >= variation.ColorSets.Count)
            return;
        var set = variation.ColorSets[index];
        for (int i = 0; i < _paint.Length; i++)
            _paint[i] = set[i] >= 0 ? set[i] : DefaultPaint[i];
        _paintSet = index;
    }

    private static Vector3 Display(VehicleColor color) => VehiclePaintColors.Display(color);

    // ---- List ----

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

        // Up / down arrows: previous / next model of the list.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput && _rows.Count > 0)
        {
            int current = _rows.FindIndex(r => r.Location == Viewer.Item?.Location);
            int next = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? current + 1 : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? current - 1 : current;
            if (next != current && next >= 0 && next < _rows.Count)
            {
                Open(_rows[next]);
                _scrollToItem = true;
            }
        }

        var vehicles = Vehicles?.Info;
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
                    Open(item);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                {
                    string tip = item.Location.Path;
                    if (item.Kind == ViewerKind.Vehicle && vehicles?.VehicleOf(item.Location.ModelHash) is { Count: > 0 } sheet)
                        tip = $"{VehicleInfoCatalog.Value(sheet, "vehicleMakeName")} {VehicleInfoCatalog.Value(sheet, "gameName")} · {ClassName(VehicleInfoCatalog.Value(sheet, "vehicleClass"))}\n{tip}";
                    ImGui.SetTooltip(tip);
                }
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

    // ---- View ----

    private void DrawView()
    {
        DrawViewToolbar();
        float infoHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var size = ImGui.GetContentRegionAvail() - new Vector2(0, infoHeight);
        size = new Vector2(MathF.Floor(Math.Max(size.X, 2)), MathF.Floor(Math.Max(size.Y, 2)));
        Viewer.RequestedSize = size * UiTheme.Scale; // In pixels, for a sharp image.

        ImGui.InvisibleButton("##viewer_image", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        if (Viewer.ImageId != IntPtr.Zero)
            drawList.AddImage(Viewer.ImageId, min, max);
        else
            drawList.AddRectFilled(min, max, Widgets.Col(UiTheme.Field));
        HandleMouse(size);

        // Name, make and class, then loading / error, over the top-left corner of the image.
        var pos = min + new Vector2(12, 10);
        bool light = Viewer.Background == 2;
        var text = light ? new Vector4(0.08f, 0.08f, 0.1f, 1f) : UiTheme.Text;
        var muted = light ? new Vector4(0.25f, 0.26f, 0.3f, 1f) : UiTheme.TextMuted;
        if (Viewer.Item is { } item)
        {
            ImGui.PushFont(UiTheme.TitleFont);
            drawList.AddText(pos, Widgets.Col(text), item.Name);
            pos.Y += ImGui.GetTextLineHeight() + 2;
            ImGui.PopFont();
            if (Subtitle(item) is { } subtitle)
            {
                drawList.AddText(pos, Widgets.Col(muted), subtitle);
                pos.Y += ImGui.GetTextLineHeight() + 2;
            }
        }
        string? status = Viewer.IsLoading ? L($"{Icons.LoaderCircle}  Chargement...")
            : Viewer.Error is { } error ? L($"{Icons.TriangleAlert}  Impossible d'afficher ce modèle : {error}")
            : Viewer.Item is null ? L("Choisis un véhicule ou un objet dans la liste.") : null;
        if (status is not null)
            drawList.AddText(pos, Widgets.Col(Viewer.Error is not null ? UiTheme.Error : muted), status);
        if (Viewer.TriangleCount > 0)
        {
            var s = Viewer.Size;
            string dims = L($"L {s.X:0.00} m · l {s.Y:0.00} m · h {s.Z:0.00} m");
            drawList.AddText(new Vector2(max.X - ImGui.CalcTextSize(dims).X - 12, min.Y + 10), Widgets.Col(muted), dims);
        }
        drawList.AddText(new Vector2(min.X + 12, max.Y - ImGui.GetTextLineHeight() - 8), Widgets.Col(light ? muted : UiTheme.TextFaint),
            L("Clic gauche : tourner · clic droit ou molette enfoncée : déplacer · molette : zoom · double-clic : vue par défaut"));

        DrawInfo();
    }

    private string? Subtitle(ViewerItem item)
    {
        if (item.Kind == ViewerKind.Vehicle)
        {
            if (Vehicles?.Info.VehicleOf(item.Location.ModelHash) is not { Count: > 0 } sheet)
                return null;
            string? make = VehicleInfoCatalog.Value(sheet, "vehicleMakeName");
            string? game = VehicleInfoCatalog.Value(sheet, "gameName");
            return string.Join(" · ", new[] { $"{make} {game}".Trim(), ClassName(VehicleInfoCatalog.Value(sheet, "vehicleClass")),
                TypeName(VehicleInfoCatalog.Value(sheet, "type")) }.Where(t => t.Length > 0));
        }
        if (state.World.Maps?.GetArchetype(item.ArchetypeHash) is { } archetype)
            return L($"{KindName(archetype.Kind)} · distance d'affichage {archetype.LodDistance:0} m");
        return null;
    }

    private void DrawViewToolbar()
    {
        bool enabled = Viewer.Item is not null;
        int shading = (int)Viewer.Shading;
        string[] shadings = [L($"{Icons.Box}  Textures"), L($"{Icons.Mountain}  Argile"), L($"{Icons.Grid}  Fil de fer")];
        if (ImGui.BeginChild("##viewer_shading", new Vector2(300f, ImGui.GetFrameHeight()), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar))
        {
            if (Widgets.Segmented("viewer_shading_mode", shadings, ref shading,
                    [L("Le modèle avec ses textures (et sa peinture)"), L("Sans texture : seulement les formes et la lumière"),
                     L("Seulement les arêtes des triangles")]))
                Viewer.Shading = (ViewerShading)shading;
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Waypoints, "viewer_edges", L("Arêtes des triangles sur le modèle"), active: Viewer.Edges))
            Viewer.Edges = !Viewer.Edges;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Grid, "viewer_ground", L("Sol quadrillé sous le modèle (carreaux de 1 m, 5 m pour les grands modèles)"), active: Viewer.ShowGround))
            Viewer.ShowGround = !Viewer.ShowGround;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Ruler, "viewer_bounds", L("Boîte englobante du modèle (ses dimensions)"), active: Viewer.ShowBounds))
            Viewer.ShowBounds = !Viewer.ShowBounds;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Rotate, "viewer_spin", L("Rotation automatique (vitrine)"), active: Viewer.AutoRotate))
            Viewer.AutoRotate = !Viewer.AutoRotate;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Sun, "viewer_light", L("Lumière : réglages"), active: !Viewer.LightFollowsCamera))
            ImGui.OpenPopup("##viewer_light_popup");
        DrawLightPopup();
        Widgets.ToolbarSeparator();

        if (Widgets.ToolbarButton(Icons.Camera, L("Vue"), L("Vues prédéfinies et champ de vision"), enabled))
            ImGui.OpenPopup("##viewer_views");
        DrawViewsPopup();
        ImGui.SameLine();
        if (Widgets.ToolbarButton(Icons.Layers, L("Fond"), L("Couleur du fond (vert : pour l'incrustation dans une vidéo)")))
            ImGui.OpenPopup("##viewer_background");
        if (ImGui.BeginPopup("##viewer_background"))
        {
            for (int i = 0; i < ModelViewer.Backgrounds.Length; i++)
            {
                var (name, color) = ModelViewer.Backgrounds[i];
                ImGui.ColorButton($"##bg{i}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine();
                if (ImGui.Selectable(L(name), Viewer.Background == i))
                    Viewer.Background = i;
            }
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (Widgets.ToolbarButton(Icons.Save, L("Capture"), L("Enregistrer l'image de la vue en PNG"), enabled && Viewer.TriangleCount > 0))
            ImGui.OpenPopup("##viewer_capture");
        DrawCapturePopup();
    }

    private void DrawLightPopup()
    {
        if (!ImGui.BeginPopup("##viewer_light_popup"))
            return;
        bool follow = Viewer.LightFollowsCamera;
        if (ImGui.Checkbox(L("Lumière qui suit la caméra"), ref follow))
            Viewer.LightFollowsCamera = follow;
        ImGui.BeginDisabled(follow);
        float angle = Viewer.LightAngle * 180f / MathF.PI;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat(L("Direction##light_angle"), ref angle, -180f, 180f, "%.0f°"))
            Viewer.LightAngle = angle * MathF.PI / 180f;
        ImGui.EndDisabled();
        float height = Viewer.LightHeight;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat(L("Hauteur##light_height"), ref height, 0.05f, 3f, "%.2f"))
            Viewer.LightHeight = height;
        ImGui.EndPopup();
    }

    private void DrawViewsPopup()
    {
        if (!ImGui.BeginPopup("##viewer_views"))
            return;
        // A vehicle faces +Y: yaw 0 looks along +Y (from behind).
        (string Name, float Yaw, float Pitch)[] views =
        [
            (L("Trois quarts avant"), -0.93f, -0.32f), (L("Trois quarts arrière"), 0.93f + MathF.PI, -0.32f),
            (L("Avant"), MathF.PI, -0.05f), (L("Arrière"), 0f, -0.05f), (L("Gauche"), -MathF.PI / 2f, -0.05f),
            (L("Droite"), MathF.PI / 2f, -0.05f), (L("Dessus"), MathF.PI, -1.5f), (L("Dessous"), MathF.PI, 1.5f),
        ];
        foreach (var (name, yaw, pitch) in views)
        {
            if (ImGui.Selectable(name))
                Viewer.SetView(yaw, pitch);
        }
        ImGui.Separator();
        float fov = Viewer.FieldOfViewDegrees;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat(L("Champ de vision"), ref fov, 10f, 90f, "%.0f°"))
            Viewer.FieldOfViewDegrees = fov;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Petit angle : effet téléobjectif, sans déformation (bien pour une vignette)"));
        ImGui.EndPopup();
    }

    private void DrawCapturePopup()
    {
        if (!ImGui.BeginPopup("##viewer_capture"))
            return;
        ImGui.TextColored(UiTheme.TextMuted, L("Taille de l'image"));
        var size = Viewer.RequestedSize;
        for (int scale = 1; scale <= 4; scale *= 2)
        {
            if (scale > 1)
                ImGui.SameLine();
            if (ImGui.RadioButton(L($"x{scale} ({(int)size.X * scale} x {(int)size.Y * scale})"), _captureScale == scale))
                _captureScale = scale;
        }
        ImGui.Checkbox(L("Fond transparent (sans le sol)"), ref _captureTransparent);
        ImGui.Spacing();
        string folder = CaptureFolder;
        if (Widgets.PrimaryButton(L($"{Icons.Save}  Enregistrer")) && Viewer.Item is { } item)
        {
            string file = $"{item.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            Viewer.RequestCapture(Path.Combine(folder, file), _captureScale, _captureTransparent);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.FolderOpen}  Ouvrir le dossier"), folder))
            OpenFolder(folder);
        Widgets.Hint(L($"Dans {folder}"));
        ImGui.EndPopup();
    }

    private static string CaptureFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NodeRunner Studio");

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Diagnostics.ErrorLog.Add(L("Visionneuse"), ex);
        }
    }

    private void ShowCaptureResult()
    {
        if (Viewer.CaptureResult is not { } result)
            return;
        Viewer.CaptureResult = null;
        state.Notify(result.Error ? L($"Capture impossible : {result.Text}") : L($"Image enregistrée : {result.Text}"), result.Error);
    }

    private void HandleMouse(Vector2 size)
    {
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive())
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            {
                Viewer.Yaw -= io.MouseDelta.X * 0.008f;
                Viewer.Pitch -= io.MouseDelta.Y * 0.008f;
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f))
            {
                Viewer.PanBy(io.MouseDelta, size.Y);
            }
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
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.TextMuted, L($"{Widgets.Count(Viewer.TriangleCount)} triangles · {Viewer.MeshCount} pièce(s) · {Viewer.TextureCount} texture(s)")
            + (Viewer.MissingTextureCount > 0 ? L($", {Viewer.MissingTextureCount} introuvable(s)") : ""));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L($"Fichier : {item.Location.Path}")
                + (Viewer.MissingTextureCount > 0 ? L($"\nTextures introuvables : {string.Join(", ", Viewer.MissingTextures)}") : ""));

        bool canPlace = item.Kind == ViewerKind.Object && state.World.Maps is not null;
        float buttons = ImGui.CalcTextSize(L("Copier le nom")).X + 50f + (canPlace ? ImGui.CalcTextSize(L("Placer")).X + 50f : 0f);
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
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom"), item.Kind == ViewerKind.Vehicle ? L("Nom de spawn du véhicule (ex. pour un script FiveM)") : L("Nom du type d'objet")))
            ImGui.SetClipboardText(item.Name);
    }

    // ---- Inspector ----

    private void DrawInspector()
    {
        if (Viewer.Item is not { } item)
        {
            Widgets.Hint(L("Choisis un modèle : ses couleurs, ses pièces, ses textures et sa fiche s'affichent ici."), wrap: true);
            return;
        }
        if (!ImGui.BeginTabBar("##viewer_tabs"))
            return;
        if (item.Kind == ViewerKind.Vehicle && ImGui.BeginTabItem(L("Peinture")))
        {
            Scroll("paint", DrawPaintTab);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Pièces")))
        {
            Scroll("parts", DrawPartsTab);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Textures")))
        {
            Scroll("textures", DrawTexturesTab);
            ImGui.EndTabItem();
        }
        if (item.Kind == ViewerKind.Vehicle ? ImGui.BeginTabItem(L("Fiche")) : ImGui.BeginTabItem(L("Type d'objet")))
        {
            Scroll("sheet", () =>
            {
                if (item.Kind == ViewerKind.Vehicle)
                    DrawVehicleSheet(item);
                else
                    DrawArchetypeSheet(item);
            });
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L("Fichier")))
        {
            Scroll("file", () => DrawFileTab(item));
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private static void Scroll(string id, Action draw)
    {
        if (ImGui.BeginChild($"##viewer_tab_{id}", Vector2.Zero, ImGuiChildFlags.None))
            draw();
        ImGui.EndChild();
    }

    // ---- Paint tab ----

    private void DrawPaintTab()
    {
        if (Vehicles is not { } vehicles)
        {
            Widgets.Hint(L("Lecture des couleurs du jeu (carcols, carvariations)..."));
            return;
        }
        var paints = vehicles.Paints;
        if (paints.Colors.Count == 0)
        {
            Widgets.Hint(L("Palette des véhicules introuvable dans les fichiers du jeu."), wrap: true);
            return;
        }
        ImGui.Checkbox(L("Afficher la peinture"), ref _paintOn);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Décoché : la carrosserie garde la couleur de sa texture (blanc), comme avant"));

        var variation = Variation;
        var used = UsedSlots();
        if (Widgets.Section(L("Couleurs prévues par le jeu"), "viewer_paint_sets"))
        {
            if (variation is not { ColorSets.Count: > 0 })
            {
                Widgets.Hint(L("Aucune combinaison dans carvariations pour ce modèle : couleurs par défaut."), wrap: true);
            }
            else
            {
                Widgets.Hint(L($"{variation.ColorSets.Count} combinaison(s) : le jeu en tire une au hasard à l'apparition du véhicule."), wrap: true);
                float swatch = 30f;
                float width = ImGui.GetContentRegionAvail().X;
                int perRow = Math.Max(1, (int)((width + 4f) / (swatch + 4f)));
                for (int i = 0; i < variation.ColorSets.Count; i++)
                {
                    if (i % perRow != 0)
                        ImGui.SameLine(0, 4);
                    if (SetSwatch(i, variation.ColorSets[i], paints, swatch))
                        ApplySet(i);
                }
                if (Widgets.Button(L($"{Icons.RefreshCw}  Au hasard"), L("Une combinaison du jeu tirée au hasard")))
                    ApplySet(_random.Next(variation.ColorSets.Count));
            }
        }

        if (Widgets.Section(L("Couleurs"), "viewer_paint_slots"))
        {
            for (int i = 0; i < PaintSlots.Length; i++)
                SlotRow(i, paints, used.Contains(PaintSlots[i]));
            if (Widgets.Button(L($"{Icons.Undo}  Couleurs du jeu"), L("Revenir à la première combinaison prévue par le jeu")))
            {
                if (variation is { ColorSets.Count: > 0 })
                    ApplySet(0);
                else
                    DefaultPaint.CopyTo(_paint, 0);
            }
        }

        if (Widgets.Section(L("Script FiveM (Lua)"), "viewer_paint_script"))
        {
            string script = PaintScript();
            ImGui.PushFont(UiTheme.SmallFont);
            ImGui.TextColored(UiTheme.TextMuted, script);
            ImGui.PopFont();
            if (Widgets.Button(L($"{Icons.Copy}  Copier le script"), L("Peint le véhicule du joueur avec ces couleurs (côté client)")))
                ImGui.SetClipboardText(script);
        }

        if (variation is not null && Widgets.Section(L("Autres réglages de carvariations"), "viewer_paint_more", defaultOpen: false))
        {
            if (Widgets.BeginProperties("viewer_variation"))
            {
                Widgets.PropertyText(L("Kits de tuning"), variation.Kits.Count == 0 ? "-" : string.Join(", ", variation.Kits.Select(HashText)));
                Widgets.PropertyText(L("Plaques"), variation.Plates.Count == 0 ? "-"
                    : string.Join(", ", variation.Plates.Select(p => $"{p.Plate} ({p.Weight} %)")));
                Widgets.PropertyText(L("Réglage des feux"), variation.LightSettings.ToString(CultureInfo.InvariantCulture),
                    L("Numéro du réglage des phares dans carcols (lightSettings)"));
                Widgets.PropertyText(L("Réglage des sirènes"), variation.SirenSettings.ToString(CultureInfo.InvariantCulture));
                Widgets.PropertyText(L("Fichier"), Path.GetFileName(variation.Source ?? ""), variation.Source);
                Widgets.EndProperties();
            }
        }
    }

    private static string HashText(string text) => text.StartsWith("hash_", StringComparison.Ordinal) ? "0x" + text[5..] : text;

    // Slots that a part of the model takes its colour from.
    private HashSet<VehiclePaintSlot> UsedSlots() =>
        Viewer.Model.Data?.Meshes.Select(m => VehiclePaints.SlotOf(m.DiffuseColor)).ToHashSet() ?? [];

    // A combination: primary on the left half, secondary on the right, wheels as a strip at the bottom.
    private bool SetSwatch(int index, VehicleColorSet set, VehiclePaints paints, float size)
    {
        ImGui.PushID(index);
        bool clicked = ImGui.InvisibleButton("##set", new Vector2(size));
        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        uint Color(int i) => Widgets.Col(new Vector4(paints.Color(set[i]) is { } c ? Display(c) : Vector3.One, 1f));
        float middle = (min.X + max.X) * 0.5f;
        float strip = max.Y - size * 0.22f;
        drawList.AddRectFilled(min, new Vector2(middle, strip), Color(0));
        drawList.AddRectFilled(new Vector2(middle, min.Y), new Vector2(max.X, strip), Color(1));
        drawList.AddRectFilled(new Vector2(min.X, strip), max, Color(3));
        bool selected = _paintSet == index;
        drawList.AddRect(min - new Vector2(1), max + new Vector2(1),
            Widgets.Col(selected ? UiTheme.Accent : hovered ? UiTheme.Text : UiTheme.Border), 2f, ImDrawFlags.None, selected ? 2f : 1f);
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(L($"Combinaison {index + 1}"));
            for (int i = 0; i < PaintSlots.Length && i < set.Indices.Count; i++)
                ImGui.TextColored(UiTheme.TextMuted, $"{ShaderNames.Slot(PaintSlots[i])} : {ColorName(paints, set[i])}");
            ImGui.EndTooltip();
        }
        ImGui.PopID();
        return clicked;
    }

    private static string ColorName(VehiclePaints paints, int index) =>
        paints.Color(index) is { } c ? $"{index} · {c.Name}" : $"{index}";

    private void SlotRow(int slot, VehiclePaints paints, bool used)
    {
        ImGui.PushID(slot);
        var color = paints.Color(_paint[slot]);
        float height = ImGui.GetFrameHeight();
        if (ImGui.ColorButton("##slot", new Vector4(color is not null ? Display(color) : Vector3.One, 1f),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(height * 1.6f, height)))
        {
            _pickingSlot = slot;
            ImGui.OpenPopup("##viewer_palette");
        }
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(used ? UiTheme.Text : UiTheme.TextFaint, ShaderNames.Slot(PaintSlots[slot]));
        ImGui.PushFont(UiTheme.SmallFont);
        ImGui.TextColored(UiTheme.TextMuted, color is null ? $"{_paint[slot]}" : $"{_paint[slot]} · {color.Name}" + (used ? "" : L(" (aucune pièce de ce modèle)")));
        ImGui.PopFont();
        ImGui.EndGroup();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Clic sur la couleur : choisir dans la palette du jeu"));
        // The popup is opened from this row's id stack: drawn here.
        if (_pickingSlot == slot)
            DrawPalettePopup(paints);
        ImGui.PopID();
    }

    private static readonly (VehicleFinish? Finish, string Name)[] Families =
    [
        (null, "Tout"), (VehicleFinish.Metallic, "Métallisé"), (VehicleFinish.Matte, "Mat"), (VehicleFinish.Util, "Utilitaire"),
        (VehicleFinish.Worn, "Usé"), (VehicleFinish.Metal, "Métal"), (VehicleFinish.Chrome, "Chrome"),
        (VehicleFinish.Chameleon, "Caméléon"), (VehicleFinish.Other, "Autres"),
    ];

    private void DrawPalettePopup(VehiclePaints paints)
    {
        if (_pickingSlot < 0)
            return;
        ImGui.SetNextWindowSize(new Vector2(420f, 0f));
        if (!ImGui.BeginPopup("##viewer_palette"))
            return;
        ImGui.TextUnformatted(L($"{ShaderNames.Slot(PaintSlots[_pickingSlot])} : palette du jeu ({paints.Colors.Count} couleurs)"));
        ImGui.SetNextItemWidth(-float.Epsilon);
        Widgets.SearchField("##palette_search", L("Nom ou numéro (ex. matte, 27)"), ref _paletteSearch);
        ImGui.SetNextItemWidth(-float.Epsilon);
        string[] families = Families.Select(f => L(f.Name)).ToArray();
        ImGui.Combo("##palette_family", ref _paletteFamily, families, families.Length);

        string search = _paletteSearch.Trim();
        var list = paints.Colors.Where(c =>
                (Families[_paletteFamily].Finish is not { } f || c.Finish == f)
                && (search.Length == 0 || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || c.Index.ToString(CultureInfo.InvariantCulture) == search))
            .ToList();
        float swatch = 22f;
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + 3f) / (swatch + 3f)));
        if (ImGui.BeginChild("##palette_grid", new Vector2(0, Math.Min(320f, ((list.Count + perRow - 1) / perRow) * (swatch + 3f) + 8f))))
        {
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (i % perRow != 0)
                    ImGui.SameLine(0, 3);
                bool selected = _paint[_pickingSlot] == c.Index;
                if (selected)
                    ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.Accent);
                if (ImGui.ColorButton($"##p{c.Index}", new Vector4(Display(c), 1f),
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | (selected ? ImGuiColorEditFlags.None : ImGuiColorEditFlags.NoBorder),
                        new Vector2(swatch)))
                {
                    _paint[_pickingSlot] = c.Index;
                    _paintSet = -1;
                }
                if (selected)
                    ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{c.Index} · {c.Name}" + (c.RampTexture is not null ? "\n" + L($"Peinture qui change de couleur ({c.RampTexture})") : ""));
            }
        }
        ImGui.EndChild();
        if (list.Count == 0)
            Widgets.Hint(L("Aucune couleur"));
        ImGui.EndPopup();
    }

    private string PaintScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("local vehicle = GetVehiclePedIsIn(PlayerPedId(), false)");
        sb.AppendLine($"SetVehicleColours(vehicle, {_paint[0]}, {_paint[1]})");
        sb.AppendLine($"SetVehicleExtraColours(vehicle, {_paint[2]}, {_paint[3]})");
        sb.AppendLine($"SetVehicleInteriorColor(vehicle, {_paint[4]})");
        sb.Append($"SetVehicleDashboardColor(vehicle, {_paint[5]})");
        return sb.ToString();
    }

    // ---- Parts tab ----

    private void DrawPartsTab()
    {
        if (Viewer.Model.Data is not { } data)
        {
            Widgets.Hint(Viewer.IsLoading ? L("Chargement...") : L("Aucun modèle."));
            return;
        }
        Widgets.Hint(L("Survol : la pièce s'allume dans la vue. Œil : la cacher. Clic : ses détails."), wrap: true);
        ImGui.SetNextItemWidth(-float.Epsilon);
        Widgets.SearchField("##parts_search", L("Shader ou texture (ex. paint, glass)"), ref _partSearch);
        if (Widgets.Button(L($"{Icons.Eye}  Tout afficher")))
            Viewer.HiddenMeshes.Clear();
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.EyeOff}  Isoler"), L("Cacher toutes les pièces sauf celle choisie"), enabled: _selectedMesh >= 0))
        {
            Viewer.HiddenMeshes.Clear();
            for (int i = 0; i < data.Meshes.Count; i++)
                if (i != _selectedMesh)
                    Viewer.HiddenMeshes.Add(i);
        }

        string search = _partSearch.Trim();
        int hovered = -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        float tableHeight = Math.Max(160f, ImGui.GetContentRegionAvail().Y * (_selectedMesh >= 0 ? 0.5f : 1f));
        if (ImGui.BeginTable("##parts", 4, flags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##eye", ImGuiTableColumnFlags.WidthFixed, 22f);
            ImGui.TableSetupColumn(L("Shader"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(L("Peinture"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(L("Triangles"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            for (int i = 0; i < data.Meshes.Count; i++)
            {
                var mesh = data.Meshes[i];
                string shader = ShaderNames.Shader(mesh.ShaderHash);
                if (search.Length > 0 && !shader.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !(mesh.DiffuseTextureName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool visible = !Viewer.HiddenMeshes.Contains(i);
                if (Widgets.IconButton(visible ? Icons.Eye : Icons.EyeOff, "eye", null, size: ImGui.GetTextLineHeight() + 2f))
                {
                    if (visible)
                        Viewer.HiddenMeshes.Add(i);
                    else
                        Viewer.HiddenMeshes.Remove(i);
                }
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i + 1}. {shader}", _selectedMesh == i, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedMesh = _selectedMesh == i ? -1 : i;
                if (ImGui.IsItemHovered())
                    hovered = i;
                ImGui.TableNextColumn();
                var slot = VehiclePaints.SlotOf(mesh.DiffuseColor);
                ImGui.TextColored(slot is VehiclePaintSlot.None or VehiclePaintSlot.Unpainted ? UiTheme.TextFaint : UiTheme.Text,
                    slot == VehiclePaintSlot.None ? "-" : ShaderNames.Slot(slot));
                ImGui.TableNextColumn();
                ImGui.TextColored(UiTheme.TextMuted, Widgets.Count(mesh.Indices.Length / 3));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        Viewer.HighlightedMesh = hovered >= 0 ? hovered : _selectedMesh;

        if (_selectedMesh >= 0 && _selectedMesh < data.Meshes.Count)
            DrawMeshDetails(data.Meshes[_selectedMesh], _selectedMesh);
    }

    private void DrawMeshDetails(ModelMesh mesh, int index)
    {
        if (!Widgets.Section(L($"Pièce {index + 1}"), "viewer_part_details"))
            return;
        if (Widgets.BeginProperties("viewer_part"))
        {
            Widgets.PropertyText(L("Shader"), ShaderNames.Shader(mesh.ShaderHash));
            Widgets.PropertyText(L("Rendu"), ShaderNames.BlendMode(mesh.BlendMode));
            var slot = VehiclePaints.SlotOf(mesh.DiffuseColor);
            if (mesh.DiffuseColor is { } d)
                Widgets.PropertyText(L("matDiffuseColor"), $"{d.X:0.###}, {d.Y:0.###}, {d.Z:0.###}",
                    slot is VehiclePaintSlot.None ? null : L($"2, n, n = prend la couleur n° n : {ShaderNames.Slot(slot)}"));
            if (slot != VehiclePaintSlot.None)
                Widgets.PropertyText(L("Couleur"), ShaderNames.Slot(slot));
            Widgets.PropertyText(L("Sommets"), Widgets.Count(mesh.Positions.Length));
            Widgets.PropertyText(L("Triangles"), Widgets.Count(mesh.Indices.Length / 3));
            if (mesh.Emission != MeshEmission.None)
                Widgets.PropertyText(L("Lumière propre"), mesh.Emission == MeshEmission.Night ? L("la nuit") : L("toujours"));
            if (mesh.VertexColored)
                Widgets.PropertyText(L("Couleur"), L("couleurs des sommets"));
            Widgets.EndProperties();
        }
        if (mesh.ShaderTextures.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.TextMuted, L("Textures du shader"));
            var texture = index < Viewer.Model.MeshTextures.Count ? Viewer.Model.MeshTextures[index] : null;
            foreach (var (sampler, name) in mesh.ShaderTextures.OrderBy(t => ShaderNames.SamplerOrder(t.Key)))
            {
                bool isColor = name == mesh.DiffuseTextureName;
                if (isColor && texture is not null && Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
                {
                    ImGui.Image(id, new Vector2(32f));
                    if (ImGui.IsItemClicked())
                        _openTexture = texture;
                    ImGui.SameLine();
                }
                ImGui.BeginGroup();
                ImGui.TextUnformatted(name);
                ImGui.PushFont(UiTheme.SmallFont);
                ImGui.TextColored(UiTheme.TextFaint, $"{ShaderNames.SamplerRole(sampler)} · {ShaderNames.SamplerName(sampler)}");
                ImGui.PopFont();
                ImGui.EndGroup();
            }
            Widgets.Hint(L("Seule la texture de couleur est affichée dans NodeRunner pour l'instant."), wrap: true);
        }
    }

    // ---- Textures tab ----

    private void DrawTexturesTab()
    {
        var textures = Viewer.Model.Textures;
        if (textures.Count == 0 && Viewer.MissingTextureCount == 0)
        {
            Widgets.Hint(Viewer.IsLoading ? L("Chargement...") : L("Aucune texture."));
            return;
        }
        if (_openTexture is { } open && textures.Contains(open))
        {
            DrawTexturePreview(open);
            return;
        }
        Widgets.Hint(L($"{textures.Count} texture(s) de couleur chargée(s) · clic : voir en grand"), wrap: true);
        float thumb = 48f;
        foreach (var texture in textures)
        {
            ImGui.PushID(texture.Name + texture.Source);
            var start = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton("##texture", new Vector2(ImGui.GetContentRegionAvail().X, thumb + 4f));
            bool hovered = ImGui.IsItemHovered();
            var after = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();
            if (hovered)
                drawList.AddRectFilled(start, ImGui.GetItemRectMax(), Widgets.Col(new Vector4(1f, 1f, 1f, 0.05f)), UiTheme.FrameRounding);
            DrawChecker(drawList, start + new Vector2(2f), new Vector2(thumb));
            if (Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
                drawList.AddImage(id, start + new Vector2(2f), start + new Vector2(2f + thumb));
            float x = start.X + thumb + 12f;
            drawList.AddText(new Vector2(x, start.Y + 4f), Widgets.Col(UiTheme.Text), texture.Name);
            drawList.AddText(new Vector2(x, start.Y + 4f + ImGui.GetTextLineHeight()), Widgets.Col(UiTheme.TextMuted),
                $"{texture.Width} x {texture.Height} · {texture.Format} · {L($"{texture.MipLevels} niveau(x)")}");
            drawList.AddText(new Vector2(x, start.Y + 4f + ImGui.GetTextLineHeight() * 2), Widgets.Col(UiTheme.TextFaint),
                Widgets.Fit(Path.GetFileName(texture.Source), ImGui.GetContentRegionAvail().X - thumb - 16f));
            if (hovered)
                ImGui.SetTooltip(texture.Source);
            if (clicked)
                _openTexture = texture;
            ImGui.SetCursorScreenPos(after);
            ImGui.PopID();
        }
        if (Viewer.MissingTextureCount > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Warning, L($"{Icons.TriangleAlert}  Introuvables ({Viewer.MissingTextureCount})"));
            foreach (var name in Viewer.MissingTextures)
                ImGui.TextColored(UiTheme.TextMuted, name);
            Widgets.Hint(L("Textures cherchées dans le modèle, son dictionnaire (.ytd) et ses parents, puis vehshare."), wrap: true);
        }
    }

    private void DrawTexturePreview(PreviewTexture texture)
    {
        if (Widgets.Button(L($"{Icons.ChevronLeft}  Toutes les textures")))
        {
            _openTexture = null;
            return;
        }
        ImGui.TextUnformatted(texture.Name);
        float width = ImGui.GetContentRegionAvail().X;
        float height = width * texture.Height / Math.Max(1, texture.Width);
        var start = ImGui.GetCursorScreenPos();
        DrawChecker(ImGui.GetWindowDrawList(), start, new Vector2(width, height));
        if (Viewer.Thumbnail(texture.Texture) is var id && id != IntPtr.Zero)
            ImGui.Image(id, new Vector2(width, height));
        else
            ImGui.Dummy(new Vector2(width, height));
        if (Widgets.BeginProperties("viewer_texture"))
        {
            Widgets.PropertyText(L("Taille"), $"{texture.Width} x {texture.Height}");
            Widgets.PropertyText(L("Format"), texture.Format.ToString());
            Widgets.PropertyText(L("Niveaux de détail"), texture.MipLevels.ToString(CultureInfo.InvariantCulture), L("Versions réduites gardées dans le fichier (mipmaps)"));
            Widgets.PropertyText(L("Fichier"), Path.GetFileName(texture.Source), texture.Source);
            Widgets.EndProperties();
        }
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom")))
            ImGui.SetClipboardText(texture.Name);
    }

    // Grey squares behind a texture, to see its transparent parts.
    private static void DrawChecker(ImDrawListPtr drawList, Vector2 min, Vector2 size)
    {
        drawList.AddRectFilled(min, min + size, Widgets.Col(new Vector4(0.32f, 0.32f, 0.34f, 1f)));
        float cell = 8f;
        uint dark = Widgets.Col(new Vector4(0.22f, 0.22f, 0.24f, 1f));
        for (float y = 0; y < size.Y; y += cell)
            for (float x = ((int)(y / cell) % 2) * cell; x < size.X; x += cell * 2)
                drawList.AddRectFilled(min + new Vector2(x, y), min + Vector2.Min(new Vector2(x + cell, y + cell), size), dark);
    }

    // ---- Vehicle sheet ----

    private static readonly (string Field, string Label)[] IdentityFields =
    [
        ("gameName", "Nom dans le jeu"), ("vehicleMakeName", "Marque"), ("vehicleClass", "Classe"), ("type", "Type"),
        ("wheelType", "Type de jantes"), ("plateType", "Plaques"), ("dashboardType", "Tableau de bord"),
        ("layout", "Disposition des places"), ("txdName", "Dictionnaire de textures"), ("handlingId", "Conduite (handlingId)"),
        ("audioNameHash", "Son"), ("swankness", "Standing"), ("frequency", "Fréquence d'apparition"), ("maxNum", "Nombre maximum"),
        ("defaultBodyHealth", "Santé de la carrosserie"), ("wheelScale", "Taille des roues avant"), ("wheelScaleRear", "Taille des roues arrière"),
    ];

    private void DrawVehicleSheet(ViewerItem item)
    {
        if (Vehicles is not { } vehicles)
        {
            Widgets.Hint(L("Lecture des fiches du jeu (vehicles.meta, handling.meta)..."));
            return;
        }
        var sheet = vehicles.Info.VehicleOf(item.Location.ModelHash);
        var handling = vehicles.Info.HandlingOf(item.Location.ModelHash);
        if (sheet.Count == 0)
        {
            Widgets.Hint(L("Ce modèle n'est pas dans les vehicles.meta du jeu."), wrap: true);
            return;
        }
        if (Widgets.Section(L("Identité"), "viewer_sheet_identity") && Widgets.BeginProperties("viewer_identity", 0.45f))
        {
            foreach (var (field, label) in IdentityFields)
            {
                if (VehicleInfoCatalog.Value(sheet, field) is { Length: > 0 } value)
                    Widgets.PropertyText(L(label), field switch
                    {
                        "vehicleClass" => $"{ClassName(value)} ({value})",
                        "type" => $"{TypeName(value)} ({value})",
                        _ => value,
                    }, field);
            }
            Widgets.EndProperties();
        }
        if (VehicleInfoCatalog.Value(sheet, "flags") is { Length: > 0 } flags && Widgets.Section(L("Particularités (flags)"), "viewer_sheet_flags", defaultOpen: false))
        {
            ImGui.PushFont(UiTheme.SmallFont);
            foreach (var flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                ImGui.TextColored(UiTheme.TextMuted, flag);
            ImGui.PopFont();
        }
        if (handling.Count > 0 && Widgets.Section(L("Conduite (handling.meta)"), "viewer_sheet_handling") && Widgets.BeginProperties("viewer_handling", 0.5f))
        {
            string? Get(string name) => VehicleInfoCatalog.Value(handling, name);
            float Number(string name) => float.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
            void Row(string label, string? value, string? tooltip = null)
            {
                if (!string.IsNullOrEmpty(value) && !value.Contains("NaN", StringComparison.Ordinal))
                    Widgets.PropertyText(label, value, tooltip);
            }
            Row(L("Masse"), $"{Number("fMass"):0} kg", "fMass");
            Row(L("Vitesse de pointe"), L($"{Number("fInitialDriveMaxFlatVel"):0} km/h (valeur du fichier)"),
                L("fInitialDriveMaxFlatVel : la vitesse réelle en jeu est un peu différente (frottements, rapports)"));
            Row(L("Rapports"), Get("nInitialDriveGears"), "nInitialDriveGears");
            float bias = Number("fDriveBiasFront");
            Row(L("Transmission"), float.IsNaN(bias) ? null
                : bias <= 0.01f ? L("propulsion (roues arrière)") : bias >= 0.99f ? L("traction (roues avant)")
                : L($"4 roues motrices ({bias * 100:0} % à l'avant)"), "fDriveBiasFront");
            Row(L("Force du moteur"), Get("fInitialDriveForce"), "fInitialDriveForce");
            Row(L("Freinage"), Get("fBrakeForce"), "fBrakeForce");
            Row(L("Angle de braquage"), $"{Number("fSteeringLock"):0}°", "fSteeringLock");
            Row(L("Adhérence"), $"{Number("fTractionCurveMax"):0.00} / {Number("fTractionCurveMin"):0.00}", "fTractionCurveMax / fTractionCurveMin");
            Row(L("Réservoir"), $"{Number("fPetrolTankVolume"):0} L", "fPetrolTankVolume");
            Row(L("Valeur"), $"{Number("nMonetaryValue"):0} $", "nMonetaryValue");
            Widgets.EndProperties();
        }
        DrawAllFields(L("Toutes les valeurs de vehicles.meta"), "viewer_sheet_all", sheet);
        if (handling.Count > 0)
            DrawAllFields(L("Toutes les valeurs de handling.meta"), "viewer_handling_all", handling);
    }

    private static void DrawAllFields(string title, string id, IReadOnlyList<VehicleField> fields)
    {
        if (!Widgets.Section(title, id, defaultOpen: false))
            return;
        if (Widgets.Button(L($"{Icons.Copy}  Copier"), L("Toutes les valeurs, une par ligne")))
            ImGui.SetClipboardText(string.Join("\n", fields.Select(f => $"{f.Name} = {f.Value}")));
        ImGui.PushFont(UiTheme.SmallFont);
        if (ImGui.BeginTable($"##{id}", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            foreach (var field in fields)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(UiTheme.TextMuted, field.Name);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(field.Value);
            }
            ImGui.EndTable();
        }
        ImGui.PopFont();
    }

    private static string ClassName(string? value) => value switch
    {
        "VC_COMPACT" => L("Compacte"), "VC_SEDAN" => L("Berline"), "VC_SUV" => L("SUV"), "VC_COUPE" => L("Coupé"),
        "VC_MUSCLE" => L("Muscle car"), "VC_SPORT_CLASSIC" => L("Sportive classique"), "VC_SPORT" => L("Sportive"),
        "VC_SUPER" => L("Supercar"), "VC_MOTORCYCLE" => L("Moto"), "VC_OFF_ROAD" => L("Tout-terrain"), "VC_INDUSTRIAL" => L("Industriel"),
        "VC_UTILITY" => L("Utilitaire"), "VC_VAN" => L("Van"), "VC_CYCLE" => L("Vélo"), "VC_BOAT" => L("Bateau"),
        "VC_HELICOPTER" => L("Hélicoptère"), "VC_PLANE" => L("Avion"), "VC_SERVICE" => L("Service"), "VC_EMERGENCY" => L("Urgences"),
        "VC_MILITARY" => L("Militaire"), "VC_COMMERCIAL" => L("Commercial"), "VC_RAIL" => L("Train"), "VC_OPEN_WHEEL" => L("Monoplace"),
        null => "",
        _ => value,
    };

    private static string TypeName(string? value) => value switch
    {
        "VEHICLE_TYPE_CAR" => L("Voiture"), "VEHICLE_TYPE_BIKE" => L("Moto"), "VEHICLE_TYPE_BICYCLE" => L("Vélo"),
        "VEHICLE_TYPE_QUADBIKE" => L("Quad"), "VEHICLE_TYPE_BOAT" => L("Bateau"), "VEHICLE_TYPE_HELI" => L("Hélicoptère"),
        "VEHICLE_TYPE_PLANE" => L("Avion"), "VEHICLE_TYPE_TRAILER" => L("Remorque"), "VEHICLE_TYPE_TRAIN" => L("Train"),
        "VEHICLE_TYPE_SUBMARINE" => L("Sous-marin"), "VEHICLE_TYPE_AMPHIBIOUS_AUTOMOBILE" => L("Voiture amphibie"),
        "VEHICLE_TYPE_AMPHIBIOUS_QUADBIKE" => L("Quad amphibie"), "VEHICLE_TYPE_BLIMP" => L("Dirigeable"),
        "VEHICLE_TYPE_SUBMARINECAR" => L("Voiture sous-marine"), null => "",
        _ => value,
    };

    // ---- Object type ----

    private static string KindName(ArchetypeKind kind) => kind switch
    {
        ArchetypeKind.Time => L("Objet à horaires"),
        ArchetypeKind.Interior => L("Intérieur (MLO)"),
        _ => L("Objet"),
    };

    private void DrawArchetypeSheet(ViewerItem item)
    {
        var world = state.World;
        if (world.Maps?.GetArchetype(item.ArchetypeHash) is not { } a)
        {
            Widgets.Hint(L("Type introuvable dans les YTYP de la carte (modèle ouvert depuis l'explorateur d'archives)."), wrap: true);
            return;
        }
        string Name(uint hash) => hash == 0 ? "-" : world.Names?.Describe(hash) ?? $"0x{hash:X8}";
        if (Widgets.Section(L("Archétype (YTYP)"), "viewer_archetype") && Widgets.BeginProperties("viewer_archetype_props", 0.45f))
        {
            Widgets.PropertyText(L("Nom"), Name(a.NameHash), $"0x{a.NameHash:X8}");
            Widgets.PropertyText(L("Sorte"), KindName(a.Kind));
            Widgets.PropertyText(L("Distance d'affichage"), $"{a.LodDistance:0.#} m", "lodDist");
            Widgets.PropertyText(L("Dictionnaire de textures"), Name(a.TextureDictionaryHash), "textureDictionary");
            if (a.DrawableDictionaryHash != 0)
                Widgets.PropertyText(L("Dictionnaire de modèles"), Name(a.DrawableDictionaryHash), "drawableDictionary");
            Widgets.PropertyText(L("Collision"), Name(a.PhysicsDictionaryHash), "physicsDictionary");
            Widgets.PropertyText(L("Type de fichier"), a.AssetType.ToString(), "assetType");
            Widgets.PropertyText(L("Drapeaux"), $"0x{a.Flags:X8}", "flags");
            if (a.Kind == ArchetypeKind.Time)
            {
                var hours = Enumerable.Range(0, 24).Where(h => a.IsVisibleAt(h)).ToList();
                Widgets.PropertyText(L("Visible"), hours.Count == 24 ? L("toute la journée") : string.Join(" ", hours.Select(h => $"{h}h")), "timeFlags");
            }
            var size = a.BoundsMax - a.BoundsMin;
            Widgets.PropertyText(L("Boîte"), $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m", "bbMin / bbMax");
            Widgets.PropertyText(L("Sphère"), $"{a.SphereRadius:0.00} m", "bsRadius");
            if (a.Kind == ArchetypeKind.Interior)
            {
                Widgets.PropertyText(L("Objets de l'intérieur"), Widgets.Count(a.InteriorEntities.Count));
                Widgets.PropertyText(L("Salles / portails"), $"{a.InteriorRooms.Count} / {a.InteriorPortals.Count}");
                Widgets.PropertyText(L("Entity sets"), a.InteriorEntitySets.Count.ToString(CultureInfo.InvariantCulture));
            }
            Widgets.EndProperties();
        }
    }

    // ---- File ----

    private void DrawFileTab(ViewerItem item)
    {
        string path = item.Location.Path;
        var info = state.World.Files?.GetInfo(path);
        if (Widgets.Section(L("Fichier"), "viewer_file") && Widgets.BeginProperties("viewer_file_props", 0.4f))
        {
            Widgets.PropertyText(L("Nom"), Path.GetFileName(path));
            Widgets.PropertyText(L("Pack"), PackOf(path), L("Pack DLC qui apporte ce fichier (ou le jeu de base)"));
            Widgets.PropertyText(L("Archive"), Widgets.Fit(Path.GetDirectoryName(path) ?? "", ImGui.GetContentRegionAvail().X), Path.GetDirectoryName(path));
            if (info is { } i)
            {
                Widgets.PropertyText(L("Taille"), L($"{FormatSize(i.Size)} ({FormatSize(i.StoredSize)} dans l'archive)"));
                Widgets.PropertyText(L("Ressource"), i.IsResource ? L($"oui, version {i.ResourceVersion}") : L("non"));
                Widgets.PropertyText(L("Chiffré"), i.IsEncrypted ? L("oui") : L("non"));
            }
            Widgets.EndProperties();
        }
        if (Widgets.Button(L($"{Icons.Copy}  Copier le chemin")))
            ImGui.SetClipboardText(path);

        if (Viewer.Model.Data is { } data && Widgets.Section(L("Géométrie"), "viewer_geometry") && Widgets.BeginProperties("viewer_geometry_props", 0.4f))
        {
            var size = data.BoundsMax - data.BoundsMin;
            Widgets.PropertyText(L("Dimensions"), $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m");
            Widgets.PropertyText(L("Boîte min"), $"{data.BoundsMin.X:0.00}, {data.BoundsMin.Y:0.00}, {data.BoundsMin.Z:0.00}");
            Widgets.PropertyText(L("Boîte max"), $"{data.BoundsMax.X:0.00}, {data.BoundsMax.Y:0.00}, {data.BoundsMax.Z:0.00}");
            Widgets.PropertyText(L("Pièces"), data.Meshes.Count.ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Sommets"), Widgets.Count(data.Meshes.Sum(m => m.Positions.Length)));
            Widgets.PropertyText(L("Triangles"), Widgets.Count(data.TriangleCount));
            Widgets.PropertyText(L("Shaders différents"), data.Meshes.Select(m => m.ShaderHash).Distinct().Count().ToString(CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Textures intégrées"), data.EmbeddedTextures.Count.ToString(CultureInfo.InvariantCulture),
                L("Textures rangées dans le fichier du modèle lui-même"));
            Widgets.EndProperties();
        }
    }

    private static string PackOf(string path)
    {
        var parts = path.Split('\\');
        int dlc = Array.FindIndex(parts, p => p.Equals("dlcpacks", StringComparison.OrdinalIgnoreCase));
        if (dlc >= 0 && dlc + 1 < parts.Length)
            return parts[dlc + 1];
        int patch = Array.FindIndex(parts, p => p.Equals("dlc_patch", StringComparison.OrdinalIgnoreCase));
        if (patch >= 0 && patch + 1 < parts.Length)
            return L($"{parts[patch + 1]} (correctif)");
        return path.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? L("mise à jour du jeu") : L("jeu de base");
    }

    private static string FormatSize(long bytes) => bytes >= 1 << 20
        ? L($"{bytes / 1048576.0:0.0} Mo")
        : L($"{bytes / 1024.0:0} Ko");
}
