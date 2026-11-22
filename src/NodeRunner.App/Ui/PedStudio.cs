using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Models;

namespace NodeRunner.App.Ui;

// Ped viewer, in a tab beside the map: one ped or animal large in the tab, the game's peds and animations in Scene,
// its clothes and the animation played in Properties.
internal sealed partial class PedStudio(AppState state)
{
    private ModelViewer Viewer => state.PedViewer;

    private ModelCatalog? _for;
    private Task<IReadOnlyList<string>>? _names;
    private string _search = "";
    private int _filter; // 0 all, 1 humans, 2 animals.
    private string? _selected;
    private int _outfit; // 0: its first clothes, n: a mix picked by n.
    private bool _scrollToSelected;

    private static string[] Filters => [L("Tous"), L("Humains"), L("Animaux")];

    public void Open()
    {
        if (!state.Features.PedViewer)
        {
            state.Notify(L("La visionneuse de personnages est réservée à GTA V."), warning: true);
            return;
        }
        state.PedOpen = true;
        state.PedFront = true;
        // A first ped to look at.
        if (_selected is null && Names() is { } names && names.Contains("player_zero"))
            Show("player_zero", 0);
        _scrollToSelected = true;
    }

    public void Close()
    {
        Viewer.Model.Close();
        state.PedOpen = false;
        state.PedShown = false;
    }

    // Once per frame before the interface: the view asks for its image again while shown; a map reloaded with other
    // files lists its peds again.
    public void BeginFrame()
    {
        Viewer.RequestedSize = Vector2.Zero;
        if (state.PedOpen && !state.Features.PedViewer)
            Close();
        if (state.World.Models is { } models && !ReferenceEquals(models, _for))
        {
            _for = models;
            _names = null;
            if (_selected is not null && state.PedOpen)
                Show(_selected, _outfit);
        }
    }

    // The game's peds, listed in the background (null meanwhile).
    private IReadOnlyList<string>? Names()
    {
        if (state.World.Models is not { } models)
            return null;
        _for ??= models;
        _names ??= Task.Run(models.PedNames);
        return _names.IsCompletedSuccessfully ? _names.Result : null;
    }

    private void Show(string name, int outfit)
    {
        if (state.World.Models is not { } models)
            return;
        bool same = Viewer.Item?.Name == name;
        _selected = name;
        _outfit = outfit;
        var item = ViewerCatalog.Ped(models, name, outfit == 0 ? null : outfit);
        // Another outfit of the same ped: the camera stays where it is.
        if (same)
            Viewer.Reload(item, state.World.ModelReader, state.World.Textures, state.World.TextureReader);
        else
            Viewer.Open(item, state.World.ModelReader, state.World.Textures, state.World.TextureReader);
    }

    private static bool IsAnimal(string name) => name.StartsWith("a_c_", StringComparison.OrdinalIgnoreCase);

    // What the start of a ped's name tells about it.
    private static string Family(string name)
    {
        string prefix = name.Split('_')[0].ToLowerInvariant();
        if (IsAnimal(name))
            return L("Animal");
        string kind = prefix switch
        {
            "a" => L("Passant"),
            "s" => L("Métier (police, médecin, ouvrier...)"),
            "g" => L("Membre de gang"),
            "u" => L("Personnage unique"),
            "ig" => L("Personnage de l'histoire"),
            "cs" or "csb" => L("Personnage des cinématiques"),
            "hc" => L("Équipe de braquage"),
            "mp" => L("Personnage Online"),
            "player" => L("Personnage principal"),
            "slod" => L("Silhouette lointaine (détail réduit)"),
            _ => L("Personnage"),
        };
        string sex = name.Contains("_m_", StringComparison.OrdinalIgnoreCase) ? L(" · homme")
            : name.Contains("_f_", StringComparison.OrdinalIgnoreCase) ? L(" · femme") : "";
        return kind + sex;
    }

    // ---- The tab: the ped large ----

    public void DrawView()
    {
        TimelineKeys();
        Animate();
        DrawToolbar();
        var clip = _clip;
        var size = ImGui.GetContentRegionAvail() - new Vector2(0, clip is null ? 0f : TimelineHeight);
        size = new Vector2(MathF.Floor(Math.Max(size.X, 2)), MathF.Floor(Math.Max(size.Y, 2)));
        Viewer.RequestedSize = size * UiTheme.Scale;

        ImGui.InvisibleButton("##ped_image", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        if (Viewer.ImageId != IntPtr.Zero)
            drawList.AddImage(Viewer.ImageId, min, max);
        else
            drawList.AddRectFilled(min, max, Widgets.Col(UiTheme.Field));
        HandleMouse(size);

        bool light = Viewer.Background == 2;
        var text = light ? new Vector4(0.08f, 0.08f, 0.1f, 1f) : UiTheme.Text;
        var muted = light ? new Vector4(0.25f, 0.26f, 0.3f, 1f) : UiTheme.TextMuted;
        var pos = min + new Vector2(12, 10);
        if (Viewer.Item is { } item)
        {
            ImGui.PushFont(UiTheme.TitleFont);
            drawList.AddText(pos, Widgets.Col(text), item.Name);
            pos.Y += ImGui.GetTextLineHeight() + 2;
            ImGui.PopFont();
            drawList.AddText(pos, Widgets.Col(muted), Family(item.Name));
            pos.Y += ImGui.GetTextLineHeight() + 2;
        }
        string? status = Viewer.IsLoading ? L($"{Icons.LoaderCircle}  Chargement...")
            : Viewer.Error is { } error ? L($"{Icons.TriangleAlert}  Impossible d'afficher ce personnage : {error}")
            : Viewer.Item is null ? L("Choisis un personnage dans le panneau Scène.") : null;
        if (status is not null)
            drawList.AddText(pos, Widgets.Col(Viewer.Error is not null ? UiTheme.Error : muted), status);
        drawList.AddText(new Vector2(min.X + 12, max.Y - ImGui.GetTextLineHeight() - 8), Widgets.Col(light ? muted : UiTheme.TextFaint),
            L("Clic gauche : tourner · clic droit ou molette enfoncée : déplacer · molette : zoom · double-clic ou F : vue par défaut"));
        if (clip is not null)
            DrawTimeline(clip);
    }

    private void DrawToolbar()
    {
        int shading = (int)Viewer.Shading;
        string[] shadings = [L($"{Icons.Box}  Textures"), L($"{Icons.Mountain}  Argile"), L($"{Icons.Grid}  Fil de fer")];
        if (ImGui.BeginChild("##ped_shading", new Vector2(300f, ImGui.GetFrameHeight()), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar))
        {
            if (Widgets.Segmented("ped_shading_mode", shadings, ref shading,
                    [L("Le personnage avec ses textures"), L("Sans texture : seulement les formes et la lumière"),
                     L("Seulement les arêtes des triangles")]))
                Viewer.Shading = (ViewerShading)shading;
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.Grid, "ped_ground", L("Sol quadrillé sous le personnage (carreaux de 1 m)"), active: Viewer.ShowGround))
            Viewer.ShowGround = !Viewer.ShowGround;
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Rotate, "ped_spin", L("Rotation automatique (vitrine)"), active: Viewer.AutoRotate))
            Viewer.AutoRotate = !Viewer.AutoRotate;
        ImGui.SameLine();
        if (Widgets.ToolbarButton(Icons.Layers, L("Fond"), L("Couleur du fond (vert : pour l'incrustation dans une vidéo)")))
            ImGui.OpenPopup("##ped_background");
        if (ImGui.BeginPopup("##ped_background"))
        {
            for (int i = 0; i < ModelViewer.Backgrounds.Length; i++)
            {
                var (name, color) = ModelViewer.Backgrounds[i];
                ImGui.ColorButton($"##ped_bg{i}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine();
                if (ImGui.Selectable(L(name), Viewer.Background == i))
                    Viewer.Background = i;
            }
            ImGui.EndPopup();
        }
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
                Viewer.PanBy(io.MouseDelta, size.Y);
        }
        if (!ImGui.IsItemHovered())
            return;
        if (io.MouseWheel != 0)
            Viewer.Distance = Math.Clamp(Viewer.Distance * MathF.Pow(0.88f, io.MouseWheel), Viewer.MinDistance, Viewer.MaxDistance);
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) || !io.WantTextInput && state.Keys.Pressed(Input.KeyAction.FrameModel))
            Viewer.ResetView();
    }

    // ---- Scene panel: the peds and the animations of the game ----

    public void DrawList()
    {
        if (!ImGui.BeginTabBar("##ped_scene_tabs"))
            return;
        if (ImGui.BeginTabItem(L($"{Icons.PersonStanding}  Personnages")))
        {
            DrawPeds();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(L($"{Icons.Film}  Animations")))
        {
            DrawAnimations();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private unsafe void DrawPeds()
    {
        if (Names() is not { } names)
        {
            Widgets.Hint(state.World.Models is null ? L("La carte se charge...") : L($"{Icons.LoaderCircle}  Liste des personnages..."));
            return;
        }
        int filter = _filter;
        if (Widgets.Segmented("ped_filter", Filters, ref filter))
            _filter = filter;
        Widgets.SearchField("##ped_search", L("Rechercher (ex. player_zero, a_c_husky, cop)"), ref _search);
        var rows = names.Where(n => (_filter == 0 || _filter == 2 == IsAnimal(n))
                                    && (_search.Length == 0 || n.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        Widgets.Hint(L($"{rows.Count} personnages"));

        if (!ImGui.BeginChild("##ped_list", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }
        float height = ImGui.GetTextLineHeightWithSpacing();
        if (_scrollToSelected && _selected is not null && rows.IndexOf(_selected) is >= 0 and var at)
            ImGui.SetScrollY(Math.Max(0, at * height - ImGui.GetWindowHeight() * 0.4f));
        _scrollToSelected = false;
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(rows.Count, height);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                string name = rows[i];
                string icon = IsAnimal(name) ? Icons.PawPrint : Icons.PersonStanding;
                if (ImGui.Selectable($"{icon}  {name}##ped_{name}", name == _selected))
                    Show(name, 0);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(Family(name));
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    // ---- Properties panel: the shown ped ----

    public void DrawProperties()
    {
        if (Viewer.Item is not { } item || state.World.Models is not { } models)
        {
            Widgets.Hint(L("Choisis un personnage dans le panneau Scène."), wrap: true);
            return;
        }
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(item.Name);
        ImGui.PopFont();
        Widgets.Hint(Family(item.Name));
        ImGui.Spacing();
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom"), L("Le nom du modèle, pour un script FiveM (CreatePed, SetPlayerModel...)")))
        {
            ImGui.SetClipboardText(item.Name);
            state.Notify(L($"Copié : {item.Name}"));
        }

        if (Widgets.Section(L($"{Icons.Shirt}  Tenue"), "ped_outfit"))
        {
            if (ViewerCatalog.HasOutfits(models, item.Name))
            {
                if (Widgets.IconButton(Icons.ChevronLeft, "ped_outfit_previous", L("Tenue précédente"), enabled: _outfit > 0))
                    Show(item.Name, _outfit - 1);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(_outfit == 0 ? L("Tenue de base") : L($"Variante {_outfit}"));
                ImGui.SameLine();
                if (Widgets.IconButton(Icons.ChevronRight, "ped_outfit_next", L("Tenue suivante (tête, cheveux, haut et bas mélangés)")))
                    Show(item.Name, _outfit + 1);
                Widgets.Hint(L("Les variantes mélangent les têtes, cheveux, hauts et bas du personnage, comme le jeu pour les passants."), wrap: true);
            }
            else
                Widgets.Hint(L("Ce personnage a une seule tenue : ses pièces sont des fichiers séparés (personnages de l'histoire, certains animaux)."), wrap: true);
        }

        DrawAnimationProperties();

        if (Widgets.Section(L($"{Icons.Info}  Modèle"), "ped_model"))
        {
            if (Viewer.IsLoading)
                Widgets.Hint(L("Chargement..."));
            else
            {
                ImGui.TextUnformatted(L($"{Viewer.TriangleCount:N0} triangles · {Viewer.MeshCount} morceaux · {Viewer.TextureCount} textures"));
                var s = Viewer.Size;
                ImGui.TextUnformatted(L($"Taille : {s.Z:0.00} m de haut"));
                if (Viewer.MissingTextureCount > 0)
                    Widgets.Hint(L($"{Viewer.MissingTextureCount} textures introuvables"));
            }
        }
    }

    public IReadOnlyList<(string Keys, string Action)> Hints() => _clip is null
        ? [(L("Clic gauche"), L("tourner")), (L("Clic droit"), L("déplacer")), (L("Molette"), L("zoom")), ("F", L("vue par défaut"))]
        : [(L("Espace"), L("lecture / pause")), (L("Flèches"), L("image par image")), (L("Origine / Fin"), L("début / fin")),
           (L("Clic gauche"), L("tourner")), (L("Molette"), L("zoom")), ("F", L("vue par défaut"))];
}
