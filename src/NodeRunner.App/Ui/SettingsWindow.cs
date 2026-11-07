using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// "Display settings" window: quality presets for smaller PCs (view distance, fog, texture filtering, shadows), sky
// and time of day, VSync, interface size.
internal sealed class SettingsWindow(AppState state)
{
    public const string Title = $"{Icons.Settings}  Paramètres d'affichage###Settings";

    // A quality preset: the values it sets.
    private sealed record Preset(string Name, string Tooltip, float ViewDistance, float FogDistance, int Anisotropy, bool Shadows, float ShadowDistance);

    private static readonly Preset[] Presets =
    [
        new("Basse", "Pour un PC modeste : moins d'objets détaillés au loin, brouillard plus proche (rien n'est dessiné derrière), textures filtrées simplement, sans ombres.",
            0.6f, 2000f, 1, false, 400f),
        new("Moyenne", "Réglages par défaut.", 1f, 4000f, 8, true, 400f),
        new("Haute", "Pour une carte graphique récente : détails plus loin, brouillard lointain, textures du sol nettes au loin, ombres plus loin.",
            1.5f, 8000f, 16, true, 800f),
    ];

    private bool _open;

    public void Open() => _open = true;

    public void Draw()
    {
        if (!_open)
            return;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(460f, 0f), new Vector2(460f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(Title, ref _open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        var settings = state.Settings;

        if (Widgets.Section("Qualité", "settings_quality"))
        {
            int current = Array.FindIndex(Presets, p => Matches(p));
            int selected = current;
            if (Widgets.Segmented("##quality", Presets.Select(p => p.Name).ToArray(), ref selected, Presets.Select(p => p.Tooltip).ToArray())
                && selected >= 0)
            {
                var preset = Presets[selected];
                settings.ViewDistanceMultiplier = preset.ViewDistance;
                settings.FogDistance = preset.FogDistance;
                settings.Anisotropy = preset.Anisotropy;
                settings.Shadows = preset.Shadows;
                settings.ShadowDistance = preset.ShadowDistance;
                settings.Fog = true;
                state.SaveSettings();
            }
            if (current < 0)
                Widgets.Hint("Réglages personnalisés.");

            if (Widgets.BeginProperties("##quality_grid", 0.42f))
            {
                Widgets.Property("Distance de vue", "Distance à laquelle les objets détaillés remplacent les modèles simplifiés (x 1 = celle du jeu).");
                float view = settings.ViewDistanceMultiplier;
                if (ImGui.SliderFloat("##view_distance", ref view, ModelLayer.MinDistanceMultiplier, ModelLayer.MaxDistanceMultiplier, "x %.2f"))
                    settings.ViewDistanceMultiplier = view;
                SaveWhenReleased();

                Widgets.Property("Brouillard", "Estompe le lointain dans la couleur du ciel ; rien n'est dessiné derrière (plus rapide).");
                bool fog = settings.Fog;
                if (ImGui.Checkbox("##fog", ref fog))
                {
                    settings.Fog = fog;
                    state.SaveSettings();
                }
                if (settings.Fog)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1f);
                    float distance = settings.FogDistance;
                    if (ImGui.SliderFloat("##fog_distance", ref distance, 500f, 15000f, "%.0f m", ImGuiSliderFlags.Logarithmic))
                        settings.FogDistance = distance;
                    SaveWhenReleased();
                }

                Widgets.Property("Filtrage des textures", "Netteté des textures vues de biais (sol au loin). 1 = le plus rapide.");
                int anisotropy = settings.Anisotropy;
                int[] levels = [1, 2, 4, 8, 16];
                int level = Math.Max(0, Array.IndexOf(levels, anisotropy));
                if (ImGui.SliderInt("##anisotropy", ref level, 0, levels.Length - 1, $"x {levels[level]}"))
                    settings.Anisotropy = levels[level];
                SaveWhenReleased();

                Widgets.Property("Ombres", "Ombres projetées par le soleil (et la lune la nuit), jusqu'à cette distance devant la caméra. Plus loin = plus lent.");
                bool shadows = settings.Shadows;
                if (ImGui.Checkbox("##shadows", ref shadows))
                {
                    settings.Shadows = shadows;
                    state.SaveSettings();
                }
                if (settings.Shadows)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1f);
                    float shadowDistance = settings.ShadowDistance;
                    if (ImGui.SliderFloat("##shadow_distance", ref shadowDistance, 100f, 1500f, "%.0f m", ImGuiSliderFlags.Logarithmic))
                        settings.ShadowDistance = shadowDistance;
                    SaveWhenReleased();
                }

                Widgets.Property("Carte graphique", "Carte utilisée pour la vue 3D. Automatique = la plus puissante (la carte dédiée d'un portable à deux cartes). Prend effet au prochain démarrage ; si la carte choisie ne marche pas, NodeRunner prend une autre carte.");
                ImGui.SetNextItemWidth(-1f);
                _graphicsCards ??= NodeRunner.Render.GraphicsDevice.AdapterNames();
                if (ImGui.BeginCombo("##graphics_card", settings.GraphicsCard ?? $"Automatique ({_graphicsCards.FirstOrDefault() ?? "?"})"))
                {
                    if (ImGui.Selectable("Automatique (la plus puissante)", settings.GraphicsCard is null))
                    {
                        settings.GraphicsCard = null;
                        state.SaveSettings();
                    }
                    foreach (string card in _graphicsCards)
                    {
                        if (ImGui.Selectable(card, settings.GraphicsCard == card))
                        {
                            settings.GraphicsCard = card;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }
                if (!state.AdapterName.StartsWith(settings.GraphicsCard ?? _graphicsCards.FirstOrDefault() ?? state.AdapterName, StringComparison.Ordinal))
                    ImGui.TextColored(UiTheme.Warning, $"En ce moment : {state.AdapterName}. Prend effet au prochain démarrage.");

                Widgets.Property("Mémoire vidéo des modèles", "Mémoire de la carte graphique gardée pour les modèles et textures de la carte. Moins = moins de risques de saccades si d'autres programmes utilisent la carte, mais les modèles lointains se rechargent plus souvent. Automatique = 70 % de ce que Windows accorde. Jamais plus de 90 % de ce que Windows accorde.");
                ImGui.SetNextItemWidth(-1f);
                string MemoryText(int mb) => mb > 0 ? $"{mb / 1024f:0.#} Go" : "Automatique";
                if (ImGui.BeginCombo("##model_memory", MemoryText(settings.ModelMemoryMb)))
                {
                    foreach (int mb in ModelMemoryChoices)
                    {
                        if (ImGui.Selectable(MemoryText(mb), settings.ModelMemoryMb == mb))
                        {
                            settings.ModelMemoryMb = mb;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }
                if (state.ModelMemory is var (used, limit) && limit < long.MaxValue)
                    ImGui.TextColored(UiTheme.TextFaint, $"Utilisée : {used / 1048576f / 1024f:0.0} Go sur {limit / 1048576f / 1024f:0.0} Go");

                Widgets.Property("Images par seconde max.", "Limite le nombre d'images par seconde : moins de chauffe et de bruit sur les portables, batterie qui dure plus. Avec la synchronisation verticale, la plus basse des deux limites s'applique.");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##max_fps", settings.MaxFps > 0 ? $"{settings.MaxFps}" : "Illimité"))
                {
                    foreach (int fps in FpsLimits)
                    {
                        if (ImGui.Selectable(fps > 0 ? $"{fps}" : "Illimité", settings.MaxFps == fps))
                        {
                            settings.MaxFps = fps;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property("En arrière-plan", "Images par seconde quand une autre fenêtre est active (navigateur, jeu, Discord...) : moins de travail pour la carte graphique. « Pas de ralentissement » = comme quand NodeRunner est actif.");
                ImGui.SetNextItemWidth(-1f);
                string BackgroundText(int fps) => fps > 0 ? $"{fps} images par seconde" : "Pas de ralentissement";
                if (ImGui.BeginCombo("##background_fps", BackgroundText(settings.BackgroundFps)))
                {
                    foreach (int fps in BackgroundFpsChoices)
                    {
                        if (ImGui.Selectable(BackgroundText(fps), settings.BackgroundFps == fps))
                        {
                            settings.BackgroundFps = fps;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property("Synchronisation verticale", "Limite les images par seconde à la fréquence de l'écran (moins de chauffe, pas de déchirure).");
                bool vsync = settings.VSync;
                if (ImGui.Checkbox("##vsync", ref vsync))
                {
                    settings.VSync = vsync;
                    state.SaveSettings();
                }
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Section("Caméra", "settings_camera"))
        {
            if (Widgets.BeginProperties("##camera_grid", 0.42f))
            {
                Widgets.Property("Sensibilité de la souris", "Vitesse à laquelle la caméra tourne quand on bouge la souris avec le clic droit enfoncé. 100 % = réglage d'origine. Double-clic pour taper une valeur.");
                ImGui.SetNextItemWidth(-1f);
                float sensitivity = settings.MouseSensitivity * 100f;
                if (ImGui.SliderFloat("##mouse_sensitivity", ref sensitivity, MainForm.MinMouseSensitivity * 100f, MainForm.MaxMouseSensitivity * 100f, "%.0f %%", ImGuiSliderFlags.Logarithmic))
                    settings.MouseSensitivity = sensitivity / 100f;
                SaveWhenReleased();

                Widgets.Property("Champ de vision", "Angle de vue vertical de la vue 3D : petit = effet zoom, grand = grand angle (bords déformés). 60° = réglage d'origine. Aussi dans le menu Caméra de la vue 3D.");
                ImGui.SetNextItemWidth(-1f);
                float fov = settings.FieldOfView;
                if (ImGui.SliderFloat("##field_of_view", ref fov, MainForm.MinFieldOfView, MainForm.MaxFieldOfView, "%.0f°"))
                    settings.FieldOfView = fov;
                SaveWhenReleased();

                Widgets.Property("Inverser l'axe vertical", "Pousser la souris vers le haut fait regarder vers le bas, comme dans un simulateur de vol.");
                bool invertY = settings.InvertMouseY;
                if (ImGui.Checkbox("##invert_mouse_y", ref invertY))
                {
                    settings.InvertMouseY = invertY;
                    state.SaveSettings();
                }

                Widgets.Property("Touches de déplacement", "Lettres qui déplacent la caméra (avancer, gauche, reculer, droite). Les deux = ZQSD et WASD en même temps. Les flèches marchent toujours.");
                ImGui.SetNextItemWidth(-1f);
                string[] keyNames = ["ZQSD et WASD", "ZQSD (clavier AZERTY)", "WASD (clavier QWERTY)"];
                int keys = Math.Clamp((int)settings.MovementKeys, 0, keyNames.Length - 1);
                if (ImGui.Combo("##movement_keys", ref keys, keyNames, keyNames.Length))
                {
                    settings.MovementKeys = (NodeRunner.App.Input.MovementKeys)keys;
                    state.SaveSettings();
                }
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Section("Édition", "settings_edition"))
        {
            if (Widgets.BeginProperties("##edition_grid", 0.42f))
            {
                Widgets.Property("Crans de déplacement", "Pas des poignées 3D quand on maintient Ctrl : déplacement des objets, des nœuds, des salles et portails, taille des boîtes d'occlusion.");
                settings.SnapMove = StepCombo("##snap_move", settings.SnapMove, MoveSteps, "{0} m");
                Widgets.Property("Crans de rotation", "Pas des anneaux de rotation quand on maintient Ctrl.");
                settings.SnapRotate = StepCombo("##snap_rotate", settings.SnapRotate, RotateSteps, "{0}°");
                Widgets.Property("Crans d'échelle", "Pas des cubes d'échelle quand on maintient Ctrl.");
                settings.SnapScale = StepCombo("##snap_scale", settings.SnapScale, ScaleSteps, "{0}");

                Widgets.Property("Copie de secours", "Toutes les X minutes, les modifications pas encore enregistrées sont copiées à part (dossier data\\secours, jamais dans le projet). Si NodeRunner se ferme brusquement, il propose de les reprendre au prochain chargement du projet.");
                ImGui.SetNextItemWidth(-1f);
                string backupText = settings.BackupMinutes > 0f ? $"Toutes les {settings.BackupMinutes:0} min" : "Désactivée";
                if (ImGui.BeginCombo("##backup_minutes", backupText))
                {
                    foreach (float minutes in BackupSteps)
                    {
                        if (ImGui.Selectable(minutes > 0f ? $"Toutes les {minutes:0} min" : "Désactivée", MathF.Abs(settings.BackupMinutes - minutes) < 0.01f))
                        {
                            settings.BackupMinutes = minutes;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property("Format de copie", "Format des positions copiées (Copier vector3 / vector4, listes d'objets, scripts d'entity sets) : Lua pour les scripts FiveM, JSON, ou C#. Aussi dans Propriétés > menu « ... ».");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##copy_format", NodeRunner.Editor.PositionText.FormatName(state.CopyFormat)))
                {
                    foreach (var format in Enum.GetValues<NodeRunner.Editor.CopyFormat>())
                    {
                        string example = NodeRunner.Editor.PositionText.Position(new Vector3(215.76f, -810.12f, 30.73f), null, format);
                        if (ImGui.Selectable($"{NodeRunner.Editor.PositionText.FormatName(format)}  ·  {example}", state.CopyFormat == format))
                            state.CopyFormat = format;
                    }
                    ImGui.EndCombo();
                }
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Section("Calques", "settings_layers"))
        {
            if (Widgets.BeginProperties("##layers_grid", 0.42f))
            {
                Widgets.Property("Grille", "Grille au niveau de la mer (touche G). Taille d'une case : Automatique = 10 m près du sol, 100 m en hauteur. Une ligne sur dix est plus marquée.");
                bool grid = settings.ShowGrid;
                if (ImGui.Checkbox("##show_grid", ref grid))
                {
                    settings.ShowGrid = grid;
                    state.SaveSettings();
                }

                Widgets.Property("Taille des cases", "Distance entre deux lignes de la grille.");
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##grid_spacing", settings.GridSpacing > 0f ? $"{settings.GridSpacing:0} m" : "Automatique"))
                {
                    foreach (float spacing in GridSpacings)
                    {
                        if (ImGui.Selectable(spacing > 0f ? $"{spacing:0} m" : "Automatique", MathF.Abs(settings.GridSpacing - spacing) < 0.01f))
                        {
                            settings.GridSpacing = spacing;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property("Couleur de la grille", "Couleur et opacité des lignes (clic sur le carré de couleur pour la choisir). « Origine » remet la couleur d'origine.");
                var gridColor = settings.GridColor is [var r, var g, var b, var a] ? new Vector4(r, g, b, a) : NodeRunner.Render.WorldGrid.DefaultColor;
                float resetWidth = ImGui.CalcTextSize("Origine").X + ImGui.GetStyle().FramePadding.X * 2f;
                ImGui.SetNextItemWidth(-resetWidth - ImGui.GetStyle().ItemSpacing.X);
                if (ImGui.ColorEdit4("##grid_color", ref gridColor, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.NoOptions))
                    settings.GridColor = [gridColor.X, gridColor.Y, gridColor.Z, gridColor.W];
                SaveWhenReleased();
                ImGui.SameLine();
                if (ImGui.Button("Origine##grid_color_reset"))
                {
                    var d = NodeRunner.Render.WorldGrid.DefaultColor;
                    settings.GridColor = [d.X, d.Y, d.Z, d.W];
                    state.SaveSettings();
                }

                Widgets.Property("Rayon des collisions", "Distance autour de la caméra où le calque Collisions est chargé. Plus grand = plus de surfaces, plus lent. Aussi dans le panneau Calques.");
                ImGui.SetNextItemWidth(-1f);
                float collision = settings.CollisionRadius;
                if (ImGui.SliderFloat("##collision_radius", ref collision, CollisionLayer.MinRadius, CollisionLayer.MaxRadius, "%.0f m"))
                    settings.CollisionRadius = collision;
                SaveWhenReleased();

                Widgets.Property("Rayon du navmesh", "Distance autour de la caméra où le calque Navmesh est chargé. Aussi dans le panneau Calques.");
                ImGui.SetNextItemWidth(-1f);
                float navMesh = settings.NavMeshRadius;
                if (ImGui.SliderFloat("##navmesh_radius", ref navMesh, NavMeshLayer.MinRadius, NavMeshLayer.MaxRadius, "%.0f m"))
                    settings.NavMeshRadius = navMesh;
                SaveWhenReleased();

                Widgets.Property("Rayon des boîtes d'objets", "Distance autour de la caméra où les boîtes des objets sont dessinées. Aussi dans le panneau Calques.");
                ImGui.SetNextItemWidth(-1f);
                float entities = settings.EntityRadius;
                if (ImGui.SliderFloat("##entity_radius", ref entities, EntityBoxLayer.MinRadius, EntityBoxLayer.MaxRadius, "%.0f m"))
                    settings.EntityRadius = entities;
                SaveWhenReleased();
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Section("Interface", "settings_interface"))
        {
            if (Widgets.BeginProperties("##interface_grid", 0.42f))
            {
                Widgets.Property("Taille de l'interface", "Agrandit les textes, panneaux et boutons. Automatique = l'échelle choisie dans Windows (Paramètres > Affichage).");
                ImGui.SetNextItemWidth(-1f);
                string current = settings.UiScale > 0f ? Percent(settings.UiScale) : $"Automatique ({Percent(UiTheme.Scale)})";
                if (ImGui.BeginCombo("##ui_scale", current))
                {
                    if (ImGui.Selectable("Automatique", settings.UiScale <= 0f))
                        SetUiScale(0f);
                    foreach (float scale in UiScales)
                        if (ImGui.Selectable(Percent(scale), MathF.Abs(settings.UiScale - scale) < 0.01f))
                            SetUiScale(scale);
                    ImGui.EndCombo();
                }

                Widgets.Property("Couleur d'accent", "Couleur des boutons principaux, de la sélection, des liens et des cases cochées. Bleu = couleur d'origine.");
                foreach (var (name, rgb) in UiTheme.Accents)
                {
                    var color = UiTheme.Rgb(rgb);
                    bool isCurrent = settings.AccentColor == rgb;
                    if (rgb != UiTheme.Accents[0].Rgb)
                        ImGui.SameLine(0f, 6f);
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, isCurrent ? 2f : 0f);
                    ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.Text);
                    if (ImGui.ColorButton($"{name}##accent_{rgb:X6}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(22f, 22f)))
                    {
                        settings.AccentColor = rgb;
                        state.SaveSettings();
                        UiTheme.SetAccent(rgb, applyNextFrame: true);
                    }
                    ImGui.PopStyleColor();
                    ImGui.PopStyleVar();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(name);
                }

                Widgets.Property("Transparence", "Laisse voir un peu le fond d'écran de Windows à travers les panneaux, comme les fenêtres de Windows 11 (effet Mica, seulement quand la fenêtre est active). 0 % = interface opaque.");
                if (!MainForm.BackdropSupported)
                    ImGui.TextColored(UiTheme.TextFaint, "Windows 11 (22H2 ou plus récent) requis");
                else
                {
                    ImGui.SetNextItemWidth(-1f);
                    float transparency = settings.Transparency * 100f;
                    if (ImGui.SliderFloat("##transparency", ref transparency, 0f, MainForm.MaxTransparency * 100f, "%.0f %%"))
                        settings.Transparency = transparency / 100f;
                    SaveWhenReleased();
                    if (!state.TransparentWindow && settings.Transparency > 0f)
                        ImGui.TextColored(UiTheme.Warning, "Prend effet au prochain démarrage de NodeRunner.");
                }
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Section("Ambiance", "settings_ambience"))
        {
            if (Widgets.BeginProperties("##ambience_grid", 0.42f))
            {
                Widgets.Property("Ciel", "Ciel en dégradé avec le soleil derrière la carte (sinon un fond sombre uni).");
                bool sky = settings.ShowSky;
                if (ImGui.Checkbox("##sky", ref sky))
                {
                    settings.ShowSky = sky;
                    state.SaveSettings();
                }

                Widgets.Property("Heure du jour", "Position du soleil, couleur de la lumière et du ciel. Lever vers 6 h, coucher vers 18 h.");
                float hour = settings.TimeOfDay;
                int minutes = (int)MathF.Round(hour * 60f) % (24 * 60);
                if (ImGui.SliderFloat("##time_of_day", ref hour, 0f, 24f, $"{minutes / 60:00} h {minutes % 60:00}"))
                    settings.TimeOfDay = hour;
                SaveWhenReleased();
                Widgets.EndProperties();
            }
        }
    }

    private IReadOnlyList<string>? _graphicsCards;

    private static readonly int[] ModelMemoryChoices = [0, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384];

    private static readonly int[] BackgroundFpsChoices = [5, 10, 15, 30, 60, 0];

    private static readonly int[] FpsLimits = [0, 30, 60, 90, 120, 144, 165, 240];

    private static readonly float[] GridSpacings = [0f, 1f, 5f, 10f, 25f, 50f, 100f];

    private static readonly float[] BackupSteps = [0f, 1f, 2f, 5f, 10f, 15f];

    private static readonly float[] MoveSteps = [0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f];
    private static readonly float[] RotateSteps = [1f, 5f, 10f, 15f, 22.5f, 30f, 45f, 90f];
    private static readonly float[] ScaleSteps = [0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f];

    // A list of preset steps; returns the chosen one (saved at once).
    private float StepCombo(string id, float value, float[] steps, string format)
    {
        static string Text(float step, string format) => string.Format(format, step.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(id, Text(value, format)))
        {
            foreach (float step in steps)
            {
                if (ImGui.Selectable(Text(step, format), MathF.Abs(step - value) < 1e-4f))
                {
                    value = step;
                    state.SaveSettings();
                }
            }
            ImGui.EndCombo();
        }
        return value;
    }

    private static readonly float[] UiScales = [1f, 1.25f, 1.5f, 1.75f, 2f, 2.25f, 2.5f, 3f];

    private static string Percent(float scale) => $"{MathF.Round(scale * 100f)} %";

    private void SetUiScale(float scale)
    {
        state.Settings.UiScale = scale;
        state.SaveSettings();
    }

    private bool Matches(Preset preset)
    {
        var settings = state.Settings;
        return settings.Fog && MathF.Abs(settings.ViewDistanceMultiplier - preset.ViewDistance) < 0.01f
            && MathF.Abs(settings.FogDistance - preset.FogDistance) < 1f && settings.Anisotropy == preset.Anisotropy
            && settings.Shadows == preset.Shadows && (!preset.Shadows || MathF.Abs(settings.ShadowDistance - preset.ShadowDistance) < 1f);
    }

    private void SaveWhenReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            state.SaveSettings();
    }
}
