using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// Elements floating over the 3D view: a bar centred at the top (tools, grid, layers menu, camera menu) and an
// orientation indicator in the bottom left corner (world axes as seen from the camera).
internal sealed class ViewportOverlay(AppState state, CameraController cameraController)
{
    private const float Margin = 12f;
    private const float AxesRadius = 34f;
    private const float BarPadding = 5f;
    private const ImGuiWindowFlags OverlayFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;

    private static readonly (string Label, string Icon, string[] Layers)[] LayerGroups =
    [
        ("Modèles 3D", Icons.Box, [ModelLayer.LayerName]),
        ("Eau", Icons.Waves, [WaterLayer.LayerName]),
        ("Boîtes des objets", Icons.Boxes, EntityBoxLayer.Categories.Select(EntityBoxLayer.LayerName).ToArray()),
        ("Pathnodes (routes)", Icons.Route, PathNodeLayer.Categories.Select(PathNodeLayer.LayerName).ToArray()),
        ("Points de scénario", Icons.Users, ScenarioLayer.Categories.Select(ScenarioLayer.LayerName).ToArray()),
        ("Navmesh", Icons.Footprints, NavMeshLayer.Surfaces.Select(NavMeshLayer.LayerName).Append(NavMeshLayer.OutlinesLayer).ToArray()),
        ("Collisions", Icons.Mountain, CollisionLayer.Surfaces.Select(CollisionLayer.LayerName).ToArray()),
        ("Salles et portails", Icons.House, RoomLayer.Parts.Select(RoomLayer.LayerName).ToArray()),
    ];

    private static readonly (Vector3 Direction, string Label, Vector4 Color)[] Axes =
    [
        (Vector3.UnitX, "X", UiTheme.AxisX),
        (Vector3.UnitY, "Y", UiTheme.AxisY),
        (Vector3.UnitZ, "Z", UiTheme.AxisZ),
    ];

    public void Draw()
    {
        var (position, size) = DockLayout.SceneArea;
        if (size.X < 200f || size.Y < 120f)
            return; // 3D view too small for floating buttons.

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(BarPadding, BarPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 4));

        ImGui.SetNextWindowPos(new Vector2(position.X + size.X * 0.5f, position.Y + Margin), ImGuiCond.Always, new Vector2(0.5f, 0f));
        if (ImGui.Begin("##viewport_bar", OverlayFlags))
        {
            // Pill shape: fully rounded ends.
            OverlayStyle.WindowBackground(OverlayStyle.ButtonSize * 0.5f + BarPadding);
            DrawBar();
        }
        ImGui.End();

        ImGui.SetNextWindowPos(new Vector2(position.X + Margin, position.Y + size.Y - Margin), ImGuiCond.Always, new Vector2(0f, 1f));
        if (ImGui.Begin("##viewport_axes", OverlayFlags | ImGuiWindowFlags.NoInputs))
            DrawAxes();
        ImGui.End();

        ImGui.PopStyleVar(2);
    }

    private void DrawBar()
    {
        ToolButton("##tool_select", Icons.MousePointer, TransformTool.Select, "Sélection (1) : clic gauche sur un objet");
        ImGui.SameLine();
        ToolButton("##tool_move", Icons.Move, TransformTool.Move,
            "Déplacer (2) : flèches le long d'un axe, carrés dans un plan\nCtrl : par pas de 0,5 m · Échap : annuler");
        ImGui.SameLine();
        ToolButton("##tool_rotate", Icons.Rotate, TransformTool.Rotate,
            "Tourner (3) : anneaux autour de X, Y et Z\nCtrl : par pas de 15° · Échap : annuler");
        ImGui.SameLine();
        ToolButton("##tool_scale", Icons.Scale, TransformTool.Scale,
            "Mettre à l'échelle (4) : X ou Y = horizontale, Z = verticale, centre = les deux\nCtrl : par pas de 0,1 · Échap : annuler");
        ImGui.SameLine();
        ToolButton("##tool_collision", Icons.Mountain, TransformTool.Collision,
            "Collisions (5) : clic sur une surface de collision, Ctrl+clic pour en ajouter ou en retirer,\n"
            + "glisser = toutes les surfaces visibles dans le rectangle (Ctrl : ajouter)\nSuppr : supprimer · matériau : panneau Propriétés");

        OverlayStyle.Divider();
        bool followGround = state.Settings.FollowGround;
        if (OverlayStyle.IconButton("##follow_ground", Icons.Footprints,
            "Suivre le sol : en déplaçant avec les flèches X / Y ou le carré au sol, l'objet reste posé sur la collision", active: followGround))
        {
            state.Settings.FollowGround = !followGround;
            state.SaveSettings();
        }
        ImGui.SameLine();
        bool grid = state.Settings.ShowGrid;
        if (OverlayStyle.IconButton("##grid", Icons.Grid, "Grille au sol", active: grid))
        {
            state.Settings.ShowGrid = !grid;
            state.SaveSettings();
        }

        OverlayStyle.Divider();
        if (OverlayStyle.MenuButton("##layers", Icons.Layers, "Calques", "Afficher ou masquer les familles de calques"))
            ImGui.OpenPopup("layers_menu");
        var layersAnchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y);
        ImGui.SameLine();
        if (OverlayStyle.MenuButton("##camera", Icons.Video, $"{cameraController.Speed:0} m/s", "Caméra : vitesse, champ de vision, commandes"))
            ImGui.OpenPopup("camera_menu");
        var cameraAnchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y);
        ImGui.SameLine();
        float hour = state.Settings.TimeOfDay;
        if (OverlayStyle.MenuButton("##time", IsNight(hour) ? Icons.Moon : Icons.Sun, FormatHour(hour), "Heure du jour : soleil, lumière et ciel"))
            ImGui.OpenPopup("time_menu");
        var timeAnchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y);

        if (OverlayStyle.BeginPopup("time_menu", timeAnchor))
        {
            DrawTimeMenu();
            OverlayStyle.EndPopup();
        }
        if (OverlayStyle.BeginPopup("layers_menu", layersAnchor))
        {
            DrawLayersMenu();
            OverlayStyle.EndPopup();
        }
        if (OverlayStyle.BeginPopup("camera_menu", cameraAnchor))
        {
            DrawCameraMenu();
            OverlayStyle.EndPopup();
        }
    }

    private void ToolButton(string id, string icon, TransformTool tool, string tooltip)
    {
        if (OverlayStyle.IconButton(id, icon, tooltip, active: state.Tool == tool))
            state.Tool = tool;
    }

    private void DrawLayersMenu()
    {
        foreach (var (label, icon, layers) in LayerGroups)
        {
            bool any = layers.Any(state.IsLayerVisible);
            if (ImGui.MenuItem($"{icon}  {label}", null, any))
                foreach (var layer in layers)
                    state.SetLayerVisible(layer, !any);
        }
        ImGui.Separator();
        bool grid = state.Settings.ShowGrid;
        if (ImGui.MenuItem($"{Icons.Grid}  Grille au sol", null, grid))
        {
            state.Settings.ShowGrid = !grid;
            state.SaveSettings();
        }
        ImGui.TextDisabled("Détail par catégorie : panneau Calques.");
    }

    private static bool IsNight(float hour) => hour < 6f || hour >= 20f;

    private static string FormatHour(float hour)
    {
        int minutes = (int)MathF.Round(hour * 60f) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private void DrawTimeMenu()
    {
        var settings = state.Settings;
        float hour = settings.TimeOfDay;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("##hour", ref hour, 0f, 24f, FormatHour(hour)))
            settings.TimeOfDay = hour;
        if (ImGui.IsItemDeactivatedAfterEdit())
            state.SaveSettings();

        (string Icon, string Label, float Hour)[] moments =
            [(Icons.Sunrise, "Aube", 6.5f), (Icons.Sun, "Midi", 13f), (Icons.Sunset, "Soir", 19f), (Icons.Moon, "Nuit", 23f)];
        foreach (var (icon, label, momentHour) in moments)
        {
            if (ImGui.MenuItem($"{icon}  {label}", FormatHour(momentHour), MathF.Abs(settings.TimeOfDay - momentHour) < 0.01f))
            {
                settings.TimeOfDay = momentHour;
                state.SaveSettings();
            }
        }

        ImGui.Separator();
        bool sky = settings.ShowSky;
        if (ImGui.MenuItem("Ciel", null, sky))
        {
            settings.ShowSky = !sky;
            state.SaveSettings();
        }
        bool fog = settings.Fog;
        if (ImGui.MenuItem("Brouillard", null, fog))
        {
            settings.Fog = !fog;
            state.SaveSettings();
        }
        ImGui.TextDisabled("Autres réglages : Affichage > Paramètres d'affichage.");
    }

    private void DrawCameraMenu()
    {
        var camera = state.Camera;
        float speed = cameraController.Speed;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Vitesse (m/s)", ref speed, CameraController.MinSpeed, CameraController.MaxSpeed, "%.0f", ImGuiSliderFlags.Logarithmic))
            cameraController.Speed = speed;
        float fov = camera.FieldOfView * 180f / MathF.PI;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Champ de vision (°)", ref fov, 30f, 110f, "%.0f"))
            camera.FieldOfView = fov * MathF.PI / 180f;
        if (ImGui.MenuItem($"{Icons.House}  Revenir à l'origine"))
        {
            camera.Position = new Vector3(-150f, -150f, 100f);
            camera.LookAt(Vector3.Zero);
        }
        if (state.Selection is { } entity && ImGui.MenuItem($"{Icons.Crosshair}  Aller à l'objet sélectionné"))
            state.FocusOn(entity);

        ImGui.Separator();
        ImGui.TextDisabled("Clic droit maintenu + souris : regarder autour");
        ImGui.TextDisabled("Z Q S D / W A S D / flèches : se déplacer");
        ImGui.TextDisabled("Espace ou E : monter      C : descendre");
        ImGui.TextDisabled("Shift : 5 fois plus rapide      Molette : vitesse");
        ImGui.TextDisabled("Clic gauche : sélectionner      Échap : désélectionner");
        ImGui.TextDisabled("1 / 2 / 3 / 4 : sélection, déplacer, tourner, échelle");
        ImGui.TextDisabled("Ctrl en glissant : par pas      Suppr : supprimer");
        ImGui.TextDisabled("Fin : poser les objets sélectionnés au sol");
        ImGui.TextDisabled("Ctrl+C / Ctrl+V : copier, coller au centre de la vue");
        ImGui.TextDisabled("Bouton empreintes : suivre le sol en déplaçant");
    }

    // The three world axes as the camera sees them: thin lines with a colored letter at the tip, no background.
    private void DrawAxes()
    {
        float diameter = AxesRadius * 2f;
        ImGui.Dummy(new Vector2(diameter, diameter));
        var center = ImGui.GetItemRectMin() + new Vector2(AxesRadius);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRectFullScreen();

        uint shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f));
        var view = state.Camera.View;
        var projected = Axes
            .Select(a => (Axis: a, Direction: Vector3.TransformNormal(a.Direction, view)))
            .OrderBy(p => p.Direction.Z); // View space looks down -Z: the most negative Z is the farthest.
        foreach (var (axis, direction) in projected)
        {
            var offset = new Vector2(direction.X, -direction.Y);
            var end = center + offset * (AxesRadius - 10f);
            bool behind = direction.Z < -0.2f;
            uint color = ImGui.GetColorU32(axis.Color with { W = behind ? 0.7f : 1f });
            drawList.AddLine(center, end, shadow, 4f);
            drawList.AddLine(center, end, color, 2f);

            ImGui.PushFont(UiTheme.SemiboldFont);
            // Letter just past the tip of its line (in the line's direction, centered when it points at the camera).
            var textSize = ImGui.CalcTextSize(axis.Label);
            float length = offset.Length();
            var direction2D = length > 1e-3f ? offset / length : Vector2.Zero;
            var textPos = Widgets.Snap(end + direction2D * (textSize.X * 0.5f + 3f) - textSize * 0.5f);
            drawList.AddText(textPos + new Vector2(1f, 1f), shadow, axis.Label);
            drawList.AddText(textPos, color, axis.Label);
            ImGui.PopFont();
        }
        drawList.AddCircleFilled(center, 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.7f)));
        drawList.PopClipRect();
    }
}
