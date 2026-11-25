using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Configuration;
using NodeRunner.App.World;
using NodeRunner.Editor.Localization;

namespace NodeRunner.App.Ui;

// "Display settings" window: quality presets for smaller PCs (view distance, fog, texture filtering, shadows), sky
// and time of day, VSync, interface size.
internal sealed class SettingsWindow(AppState state, Action browseGameFolder, Action chooseProjectsFolder)
{
    public const string Title = $"{Icons.Settings}  Paramètres d'affichage###Settings";

    private static QualityPreset[] Presets => QualityPreset.All;

    private bool _open;

    public void Open() => _open = true;

    // Opens the shortcuts page.
    public void OpenKeys()
    {
        _open = true;
        _page = KeysPage;
    }

    private const int KeysPage = 7;

    public void Draw()
    {
        if (!_open)
            return;
        // Wide rather than tall, to fit every screen: the categories on the left, the chosen one on the right.
        var viewport = ImGui.GetMainViewport();
        var room = viewport.WorkSize * 0.92f;
        var size = Vector2.Min(new Vector2(900f, 560f), room);
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(Vector2.Min(new Vector2(640f, 360f), room), room);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.GlassEdge);
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        if (visible)
        {
            OverlayStyle.WindowGlass(strong: true, titleBar: true);
            if (ImGui.BeginChild("##settings_pages", new Vector2(PagesWidth, 0), ImGuiChildFlags.None))
                DrawPages();
            ImGui.EndChild();
            ImGui.SameLine(0, 16f);
            if (ImGui.BeginChild("##settings_page", Vector2.Zero, ImGuiChildFlags.None))
                DrawContent();
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private const float PagesWidth = 180f;
    private int _page;

    private static (string Icon, string Name)[] Pages =>
    [
        (Icons.Gauge, L("Qualité")), (Icons.Camera, L("Caméra")), (Icons.Move, L("Édition")), (Icons.Layers, L("Calques")),
        (Icons.Monitor, L("Interface")), (Icons.Sun, L("Ambiance")), (Icons.FolderOpen, L("Dossiers")), (Icons.Keyboard, L("Raccourcis")),
    ];

    private KeyBindingsPage? _keysPage;

    private void DrawPages()
    {
        var pages = Pages;
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
        for (int i = 0; i < pages.Length; i++)
        {
            if (ImGui.Selectable($"{pages[i].Icon}  {pages[i].Name}##settings_page{i}", _page == i, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight() + 4f)))
                _page = i;
        }
        ImGui.PopStyleVar();
    }

    // The chosen category's title; false for the others (their settings are not drawn).
    private bool Page(int index)
    {
        if (_page != index)
            return false;
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(Pages[index].Name);
        ImGui.PopFont();
        ImGui.Dummy(new Vector2(0, 4));
        return true;
    }

    private void DrawContent()
    {
        var settings = state.Settings;

        if (Page(0))
        {
            int current = Array.FindIndex(Presets, p => p.Matches(settings));
            int selected = current;
            if (Widgets.Segmented("##quality", Presets.Select(p => L(p.Name)).ToArray(), ref selected, Presets.Select(p => L(p.Tooltip)).ToArray())
                && selected >= 0)
            {
                Presets[selected].ApplyTo(settings);
                state.SaveSettings();
            }
            if (current < 0)
                Widgets.Hint(L("Réglages personnalisés."));

            if (Widgets.BeginProperties("##quality_grid", 0.42f))
            {
                Widgets.Property(L("Distance de vue"), L("Distance à laquelle les objets détaillés remplacent les modèles simplifiés (x 1 = celle du jeu)."));
                float view = settings.ViewDistanceMultiplier;
                if (ImGui.SliderFloat("##view_distance", ref view, ModelLayer.MinDistanceMultiplier, ModelLayer.MaxDistanceMultiplier, "x %.2f"))
                    settings.ViewDistanceMultiplier = view;
                SaveWhenReleased();

                Widgets.Property(L("Brouillard"), L("Estompe le lointain dans la couleur du ciel ; rien n'est dessiné derrière (plus rapide)."));
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

                Widgets.Property(L("Filtrage des textures"), L("Netteté des textures vues de biais (sol au loin). 1 = le plus rapide."));
                int anisotropy = settings.Anisotropy;
                int[] levels = [1, 2, 4, 8, 16];
                int level = Math.Max(0, Array.IndexOf(levels, anisotropy));
                if (ImGui.SliderInt("##anisotropy", ref level, 0, levels.Length - 1, $"x {levels[level]}"))
                    settings.Anisotropy = levels[level];
                SaveWhenReleased();

                Widgets.Property(L("Ombres"), L("Ombres projetées par le soleil (et la lune la nuit), jusqu'à cette distance devant la caméra. Plus loin = plus lent."));
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

                Widgets.Property(L("Eau réaliste"), L("Reflets des bâtiments, des ponts et des bateaux sur l'eau, fond qui ondule sous les vagues. Sans : l'eau reflète seulement le ciel (plus rapide)."));
                bool reflections = settings.WaterReflections;
                if (ImGui.Checkbox("##water_reflections", ref reflections))
                {
                    settings.WaterReflections = reflections;
                    state.SaveSettings();
                }

                Widgets.Property(L("Vagues en 3D"), L("La surface de la mer bouge avec les vagues, comme dans le jeu : houle du large, mer calme dans les ports et marinas, vagues qui roulent vers les plages et écume (zones du fichier water.xml du jeu). Sans : surface plate, seul son aspect suit les vagues."));
                bool waves = settings.WaterWaves;
                if (ImGui.Checkbox("##water_waves", ref waves))
                {
                    settings.WaterWaves = waves;
                    state.SaveSettings();
                }

                DrawImageSettings(settings);

                Widgets.Property(L("Carte graphique"), L("Carte utilisée pour la vue 3D. Automatique = la plus puissante (la carte dédiée d'un portable à deux cartes). Prend effet au prochain démarrage ; si la carte choisie ne marche pas, NodeRunner prend une autre carte."));
                ImGui.SetNextItemWidth(-1f);
                _graphicsCards ??= NodeRunner.Render.GraphicsDevice.AdapterNames();
                if (ImGui.BeginCombo("##graphics_card", settings.GraphicsCard ?? L($"Automatique ({_graphicsCards.FirstOrDefault() ?? "?"})")))
                {
                    if (ImGui.Selectable(L("Automatique (la plus puissante)"), settings.GraphicsCard is null))
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
                    ImGui.TextColored(UiTheme.Warning, L($"En ce moment : {state.AdapterName}. Prend effet au prochain démarrage."));

                Widgets.Property(L("Mémoire vidéo des modèles"), L("Mémoire de la carte graphique gardée pour les modèles et textures de la carte. Moins = moins de risques de saccades si d'autres programmes utilisent la carte, mais les modèles lointains se rechargent plus souvent. Automatique = 70 % de ce que Windows accorde. Jamais plus de 90 % de ce que Windows accorde."));
                ImGui.SetNextItemWidth(-1f);
                string MemoryText(int mb) => mb > 0 ? L($"{mb / 1024f:0.#} Go") : L("Automatique");
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
                    ImGui.TextColored(UiTheme.TextFaint, L($"Utilisée : {used / 1048576f / 1024f:0.0} Go sur {limit / 1048576f / 1024f:0.0} Go"));

                Widgets.Property(L("Images par seconde max."), L("Limite le nombre d'images par seconde : moins de chauffe et de bruit sur les portables, batterie qui dure plus. Avec la synchronisation verticale, la plus basse des deux limites s'applique."));
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##max_fps", settings.MaxFps > 0 ? $"{settings.MaxFps}" : L("Illimité")))
                {
                    foreach (int fps in FpsLimits)
                    {
                        if (ImGui.Selectable(fps > 0 ? $"{fps}" : L("Illimité"), settings.MaxFps == fps))
                        {
                            settings.MaxFps = fps;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property(L("En arrière-plan"), L("Images par seconde quand une autre fenêtre est active (navigateur, jeu, Discord...) : moins de travail pour la carte graphique. « Pas de ralentissement » = comme quand NodeRunner est actif."));
                ImGui.SetNextItemWidth(-1f);
                string BackgroundText(int fps) => fps > 0 ? L($"{fps} images par seconde") : L("Pas de ralentissement");
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

                Widgets.Property(L("Synchronisation verticale"), L("Limite les images par seconde à la fréquence de l'écran (moins de chauffe, pas de déchirure)."));
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
        if (Page(1))
        {
            if (Widgets.BeginProperties("##camera_grid", 0.42f))
            {
                Widgets.Property(L("Sensibilité de la souris"), L("Vitesse à laquelle la caméra tourne quand on bouge la souris avec le clic droit enfoncé. 100 % = réglage d'origine. Double-clic pour taper une valeur."));
                ImGui.SetNextItemWidth(-1f);
                float sensitivity = settings.MouseSensitivity * 100f;
                if (ImGui.SliderFloat("##mouse_sensitivity", ref sensitivity, MainForm.MinMouseSensitivity * 100f, MainForm.MaxMouseSensitivity * 100f, "%.0f %%", ImGuiSliderFlags.Logarithmic))
                    settings.MouseSensitivity = sensitivity / 100f;
                SaveWhenReleased();

                Widgets.Property(L("Champ de vision"), L("Angle de vue vertical de la vue 3D : petit = effet zoom, grand = grand angle (bords déformés). 60° = réglage d'origine. Aussi dans le menu Caméra de la vue 3D."));
                ImGui.SetNextItemWidth(-1f);
                float fov = settings.FieldOfView;
                if (ImGui.SliderFloat("##field_of_view", ref fov, MainForm.MinFieldOfView, MainForm.MaxFieldOfView, "%.0f°"))
                    settings.FieldOfView = fov;
                SaveWhenReleased();

                Widgets.Property(L("Inverser l'axe vertical"), L("Pousser la souris vers le haut fait regarder vers le bas, comme dans un simulateur de vol."));
                bool invertY = settings.InvertMouseY;
                if (ImGui.Checkbox("##invert_mouse_y", ref invertY))
                {
                    settings.InvertMouseY = invertY;
                    state.SaveSettings();
                }

                Widgets.Property(L("Touches de déplacement"), L("Lettres qui déplacent la caméra (avancer, gauche, reculer, droite). Les deux = ZQSD et WASD en même temps. Les flèches marchent toujours. Toutes les touches se changent dans Raccourcis."));
                ImGui.SetNextItemWidth(-1f);
                string[] keyNames = [L("ZQSD et WASD"), L("ZQSD (clavier AZERTY)"), L("WASD (clavier QWERTY)")];
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
        if (Page(2))
        {
            if (Widgets.BeginProperties("##edition_grid", 0.42f))
            {
                Widgets.Property(L("Crans de déplacement"), L("Pas des poignées 3D quand on maintient Ctrl : déplacement des objets, des nœuds, des salles et portails, taille des boîtes d'occlusion."));
                settings.SnapMove = StepCombo("##snap_move", settings.SnapMove, MoveSteps, "{0} m");
                Widgets.Property(L("Crans de rotation"), L("Pas des anneaux de rotation quand on maintient Ctrl."));
                settings.SnapRotate = StepCombo("##snap_rotate", settings.SnapRotate, RotateSteps, "{0}°");
                Widgets.Property(L("Crans d'échelle"), L("Pas des cubes d'échelle quand on maintient Ctrl."));
                settings.SnapScale = StepCombo("##snap_scale", settings.SnapScale, ScaleSteps, "{0}");

                Widgets.Property(L("Copie de secours"), L("Toutes les X minutes, les modifications pas encore enregistrées sont copiées à part (dossier data\\secours, jamais dans le projet). Si NodeRunner se ferme brusquement, il propose de les reprendre au prochain chargement du projet."));
                ImGui.SetNextItemWidth(-1f);
                string backupText = settings.BackupMinutes > 0f ? L($"Toutes les {settings.BackupMinutes:0} min") : L("Désactivée");
                if (ImGui.BeginCombo("##backup_minutes", backupText))
                {
                    foreach (float minutes in BackupSteps)
                    {
                        if (ImGui.Selectable(minutes > 0f ? L($"Toutes les {minutes:0} min") : L("Désactivée"), MathF.Abs(settings.BackupMinutes - minutes) < 0.01f))
                        {
                            settings.BackupMinutes = minutes;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property(L("Format de copie"), L("Format des positions copiées (Copier vector3 / vector4, listes d'objets, scripts d'entity sets) : Lua pour les scripts FiveM, JSON, ou C#. Aussi dans Propriétés > menu « ... »."));
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
        if (Page(3))
        {
            if (Widgets.BeginProperties("##layers_grid", 0.42f))
            {
                Widgets.Property(L("Grille"), L("Grille au niveau de la mer (touche G). Taille d'une case : Automatique = 10 m près du sol, 100 m en hauteur. Une ligne sur dix est plus marquée."));
                bool grid = settings.ShowGrid;
                if (ImGui.Checkbox("##show_grid", ref grid))
                {
                    settings.ShowGrid = grid;
                    state.SaveSettings();
                }

                Widgets.Property(L("Taille des cases"), L("Distance entre deux lignes de la grille."));
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.BeginCombo("##grid_spacing", settings.GridSpacing > 0f ? $"{settings.GridSpacing:0} m" : L("Automatique")))
                {
                    foreach (float spacing in GridSpacings)
                    {
                        if (ImGui.Selectable(spacing > 0f ? $"{spacing:0} m" : L("Automatique"), MathF.Abs(settings.GridSpacing - spacing) < 0.01f))
                        {
                            settings.GridSpacing = spacing;
                            state.SaveSettings();
                        }
                    }
                    ImGui.EndCombo();
                }

                Widgets.Property(L("Couleur de la grille"), L("Couleur et opacité des lignes (clic sur le carré de couleur pour la choisir). « Origine » remet la couleur d'origine."));
                var gridColor = settings.GridColor is [var r, var g, var b, var a] ? new Vector4(r, g, b, a) : NodeRunner.Render.WorldGrid.DefaultColor;
                float resetWidth = ImGui.CalcTextSize(L("Origine")).X + ImGui.GetStyle().FramePadding.X * 2f;
                ImGui.SetNextItemWidth(-resetWidth - ImGui.GetStyle().ItemSpacing.X);
                if (ImGui.ColorEdit4("##grid_color", ref gridColor, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.NoOptions))
                    settings.GridColor = [gridColor.X, gridColor.Y, gridColor.Z, gridColor.W];
                SaveWhenReleased();
                ImGui.SameLine();
                if (ImGui.Button(L("Origine##grid_color_reset")))
                {
                    var d = NodeRunner.Render.WorldGrid.DefaultColor;
                    settings.GridColor = [d.X, d.Y, d.Z, d.W];
                    state.SaveSettings();
                }

                Widgets.Property(L("Rayon des collisions"), L("Distance autour de la caméra où le calque Collisions est chargé. Plus grand = plus de surfaces, plus lent. Aussi dans le panneau Calques."));
                ImGui.SetNextItemWidth(-1f);
                float collision = settings.CollisionRadius;
                if (ImGui.SliderFloat("##collision_radius", ref collision, CollisionLayer.MinRadius, CollisionLayer.MaxRadius, "%.0f m"))
                    settings.CollisionRadius = collision;
                SaveWhenReleased();

                Widgets.Property(L("Rayon du navmesh"), L("Distance autour de la caméra où le calque Navmesh est chargé. Aussi dans le panneau Calques."));
                ImGui.SetNextItemWidth(-1f);
                float navMesh = settings.NavMeshRadius;
                if (ImGui.SliderFloat("##navmesh_radius", ref navMesh, NavMeshLayer.MinRadius, NavMeshLayer.MaxRadius, "%.0f m"))
                    settings.NavMeshRadius = navMesh;
                SaveWhenReleased();

                Widgets.Property(L("Rayon des boîtes d'objets"), L("Distance autour de la caméra où les boîtes des objets sont dessinées. Aussi dans le panneau Calques."));
                ImGui.SetNextItemWidth(-1f);
                float entities = settings.EntityRadius;
                if (ImGui.SliderFloat("##entity_radius", ref entities, EntityBoxLayer.MinRadius, EntityBoxLayer.MaxRadius, "%.0f m"))
                    settings.EntityRadius = entities;
                SaveWhenReleased();
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Page(4))
        {
            if (Widgets.BeginProperties("##interface_grid", 0.42f))
            {
                Widgets.Property(L("Langue"), L("Langue de l'interface. Automatique = celle de Windows (français pour un Windows en français, sinon anglais)."));
                ImGui.SetNextItemWidth(-1f);
                string languageName = settings.Language switch
                {
                    Language.French => "Français",
                    Language.English => "English",
                    _ => L($"Automatique ({(settings.InterfaceLanguage == Language.French ? "Français" : "English")})"),
                };
                if (ImGui.BeginCombo("##language", languageName))
                {
                    if (ImGui.Selectable(L("Automatique"), settings.Language is null))
                        SetLanguage(null);
                    if (ImGui.Selectable("Français", settings.Language == Language.French))
                        SetLanguage(Language.French);
                    if (ImGui.Selectable("English", settings.Language == Language.English))
                        SetLanguage(Language.English);
                    ImGui.EndCombo();
                }

                Widgets.Property(L("Taille de l'interface"), L("Agrandit les textes, panneaux et boutons. Automatique = l'échelle choisie dans Windows (Paramètres > Affichage)."));
                ImGui.SetNextItemWidth(-1f);
                string current = settings.UiScale > 0f ? Percent(settings.UiScale) : L($"Automatique ({Percent(UiTheme.Scale)})");
                if (ImGui.BeginCombo("##ui_scale", current))
                {
                    if (ImGui.Selectable(L("Automatique"), settings.UiScale <= 0f))
                        SetUiScale(0f);
                    foreach (float scale in UiScales)
                        if (ImGui.Selectable(Percent(scale), MathF.Abs(settings.UiScale - scale) < 0.01f))
                            SetUiScale(scale);
                    ImGui.EndCombo();
                }

                Widgets.Property(L("Couleur d'accent"), L("Couleur des boutons principaux, de la sélection, des liens et des cases cochées. Bleu = couleur d'origine. En GTA IV, la couleur suit le jeu ou l'épisode ouvert."));
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
                        // The main window glides to it (GTA V only: GTA IV has its own colours).
                        settings.AccentColor = rgb;
                        state.SaveSettings();
                    }
                    ImGui.PopStyleColor();
                    ImGui.PopStyleVar();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(L(name));
                }

                Widgets.Property(L("Transparence"), L("Laisse voir un peu le fond d'écran de Windows à travers les panneaux, comme les fenêtres de Windows 11 (effet Mica, seulement quand la fenêtre est active). 0 % = interface opaque."));
                if (!MainForm.BackdropSupported)
                    ImGui.TextColored(UiTheme.TextFaint, L("Windows 11 (22H2 ou plus récent) requis"));
                else
                {
                    ImGui.SetNextItemWidth(-1f);
                    float transparency = settings.Transparency * 100f;
                    if (ImGui.SliderFloat("##transparency", ref transparency, 0f, MainForm.MaxTransparency * 100f, "%.0f %%"))
                        settings.Transparency = transparency / 100f;
                    SaveWhenReleased();
                    if (!state.TransparentWindow && settings.Transparency > 0f)
                        ImGui.TextColored(UiTheme.Warning, L("Prend effet au prochain démarrage de NodeRunner."));
                }

                Widgets.Property(L("Verre dépoli"), L("La barre flottante, ses menus, la recherche et les Paramètres laissent voir la carte floutée derrière eux. Décocher sur un ordinateur lent."));
                if (state.SoftwareRendering)
                    ImGui.TextColored(UiTheme.TextFaint, L("Indisponible en rendu logiciel"));
                else
                {
                    bool frosted = settings.FrostedGlass;
                    if (ImGui.Checkbox("##frosted_glass", ref frosted))
                    {
                        settings.FrostedGlass = frosted;
                        state.SaveSettings();
                    }
                }

                Widgets.Property(L("Fenêtres détachables"), L("Un panneau (Visionneuse, Propriétés, Scène...) glissé hors de NodeRunner devient une vraie fenêtre de Windows, que l'on peut mettre sur un autre écran. Le glisser sur NodeRunner le remet dedans."));
                bool detachable = settings.DetachableWindows;
                if (ImGui.Checkbox("##detachable", ref detachable))
                {
                    settings.DetachableWindows = detachable;
                    state.SaveSettings();
                }
                if (detachable != PlatformWindows.Enabled)
                    ImGui.TextColored(UiTheme.Warning, L("Prend effet au prochain démarrage de NodeRunner."));

                Widgets.Property(L("Nouvelles versions"), L("Au démarrage, demande à GitHub si une version plus récente de NodeRunner est sortie et l'affiche en haut à droite de la vue 3D. Rien d'autre n'est envoyé."));
                bool updates = settings.CheckForUpdates;
                if (ImGui.Checkbox("##check_updates", ref updates))
                {
                    settings.CheckForUpdates = updates;
                    state.SaveSettings();
                }
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (Page(5))
        {
            if (Widgets.BeginProperties("##ambience_grid", 0.42f))
            {
                Widgets.Property(L("Ciel"), L("Ciel en dégradé avec le soleil derrière la carte (sinon un fond sombre uni)."));
                bool sky = settings.ShowSky;
                if (ImGui.Checkbox("##sky", ref sky))
                {
                    settings.ShowSky = sky;
                    state.SaveSettings();
                }

                Widgets.Property(L("Heure du jour"), L("Position du soleil, couleur de la lumière et du ciel. Lever vers 6 h, coucher vers 18 h."));
                float hour = settings.TimeOfDay;
                int minutes = (int)MathF.Round(hour * 60f) % (24 * 60);
                if (ImGui.SliderFloat("##time_of_day", ref hour, 0f, 24f, L($"{minutes / 60:00} h {minutes % 60:00}")))
                    settings.TimeOfDay = hour;
                SaveWhenReleased();
                Widgets.EndProperties();
            }
        }

        if (Page(KeysPage))
            (_keysPage ??= new KeyBindingsPage(state)).Draw();

        ImGui.Dummy(new Vector2(0, 4));
        if (Page(6))
        {
            if (Widgets.BeginProperties("##folders_grid", 0.42f))
            {
                Widgets.Property(L("Dossier de GTA V"), L("Installation du jeu lue par NodeRunner (rien n'y est jamais écrit). Détection automatique = Steam, Epic ou Rockstar Games Launcher."));
                if (state.Game is { } game)
                {
                    ImGui.TextUnformatted(Widgets.Fit(game.FolderPath, FolderTextWidth));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(L($"{game.FolderPath}\nTrouvé via : {game.Source}"));
                }
                else
                {
                    ImGui.TextColored(UiTheme.Warning, L("Introuvable"));
                }
                if (Widgets.Button(L($"{Icons.FolderOpen}  Choisir...##game_folder")))
                    browseGameFolder();
                ImGui.SameLine();
                if (Widgets.Button(L("Détection automatique##game_folder"), L("Oublie le dossier choisi à la main et cherche le jeu dans Steam, Epic et Rockstar Games Launcher.")))
                    state.RedetectGame(clearManualChoice: true);

                Widgets.Property(L("Dossier des projets"), L("Dossier où sont créés les nouveaux projets (chacun dans son sous-dossier). Les projets déjà créés ne bougent pas."));
                string projects = state.ProjectsFolder;
                ImGui.TextUnformatted(Widgets.Fit(projects, FolderTextWidth));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(projects);
                if (Widgets.Button(L($"{Icons.FolderOpen}  Choisir...##projects_folder")))
                    chooseProjectsFolder();
                ImGui.SameLine();
                if (Widgets.Button(L("Ouvrir##projects_folder"), L("Ouvre ce dossier dans l'Explorateur Windows."), enabled: Directory.Exists(projects)))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{projects}\"");
                ImGui.SameLine();
                if (Widgets.Button(L("Par défaut##projects_folder"), L("Documents\\NodeRunner"), enabled: settings.ProjectsFolder is not null))
                {
                    settings.ProjectsFolder = null;
                    state.SaveSettings();
                }

                Widgets.Property(L("Bibliothèque de props"), L("Packs de props addons partagés par tous les projets (un sous-dossier par pack). Changer de dossier recharge la carte."));
                string library = state.LibraryFolder;
                ImGui.TextUnformatted(Widgets.Fit(library, FolderTextWidth));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(library);
                if (Widgets.Button(L($"{Icons.FolderOpen}  Choisir...##library_folder")))
                    state.LibraryRequest = LibraryAction.ChooseFolder;
                ImGui.SameLine();
                if (Widgets.Button(L("Ouvrir##library_folder"), L("Ouvre ce dossier dans l'Explorateur Windows.")))
                    ViewerPanel.OpenLibraryFolder(state);
                ImGui.SameLine();
                if (Widgets.Button(L("Par défaut##library_folder"), L("Documents\\NodeRunner\\Bibliothèque"), enabled: settings.LibraryFolder is not null))
                    state.LibraryRequest = LibraryAction.Default;
                Widgets.EndProperties();
            }
        }

        ImGui.Dummy(new Vector2(0, 8));
        if (!_confirmReset)
        {
            if (Widgets.Button(L("Remettre les réglages d'origine..."),
                L("Remet tous les réglages de cette fenêtre comme au premier lancement. Le dossier de GTA V, les projets, la version de la carte, les calques et la caméra ne changent pas.")))
                _confirmReset = true;
        }
        else
        {
            ImGui.TextColored(UiTheme.Warning, L("Remettre tous les réglages de cette fenêtre comme au premier lancement ?"));
            if (Widgets.PrimaryButton(L("Remettre##reset_settings")))
            {
                _confirmReset = false;
                ResetSettings();
            }
            ImGui.SameLine();
            if (Widgets.Button(L("Ne rien changer##reset_settings")))
                _confirmReset = false;
        }
    }

    private bool _confirmReset;

    private void ResetSettings()
    {
        var settings = state.Settings;
        settings.ResetDisplaySettings();
        Loc.Current = settings.InterfaceLanguage;
        state.SaveSettings();
        state.Notify(L("Réglages d'origine remis (la carte graphique et la transparence changent au prochain démarrage)."));
    }

    private const float FolderTextWidth = 230f;

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
        static string Text(float step, string format) => string.Format(format, step.ToString("0.##", Loc.IsFrench ? System.Globalization.CultureInfo.GetCultureInfo("fr-FR") : System.Globalization.CultureInfo.InvariantCulture));
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

    // Texts are looked up each frame: the change is immediate.
    private void SetLanguage(Language? language)
    {
        state.Settings.Language = language;
        Loc.Current = state.Settings.InterfaceLanguage;
        state.SaveSettings();
    }

    private void SetUiScale(float scale)
    {
        state.Settings.UiScale = scale;
        state.SaveSettings();
    }

    private static readonly int[] ShadowResolutions = [1024, 2048, 4096];

    // The scene's own image: HDR light, its effects, resolution.
    private void DrawImageSettings(AppSettings settings)
    {
        Widgets.Property(L("Qualité des ombres"), L("Finesse des ombres du soleil. Plus fine = bords plus nets, mais plus de mémoire vidéo et un peu plus lent."));
        ImGui.SetNextItemWidth(-1f);
        string[] shadowNames = [L("Basse (1024)"), L("Moyenne (2048)"), L("Haute (4096)")];
        int shadowLevel = Math.Max(0, Array.IndexOf(ShadowResolutions, settings.ShadowResolution));
        if (ImGui.Combo("##shadow_resolution", ref shadowLevel, shadowNames, shadowNames.Length))
        {
            settings.ShadowResolution = ShadowResolutions[shadowLevel];
            state.SaveSettings();
        }

        Widgets.Property(L("Lumière réaliste (HDR)"), L("La lumière est calculée comme dans la réalité (soleil bien plus fort que l'ombre, lumières qui brillent la nuit), puis ramenée à l'écran par une courbe de cinéma. Sans : le rendu simple des versions précédentes, le plus rapide."));
        bool hdr = settings.HdrRendering;
        if (ImGui.Checkbox("##hdr", ref hdr))
        {
            settings.HdrRendering = hdr;
            state.SaveSettings();
        }
        if (settings.HdrRendering)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            float exposure = settings.Exposure;
            if (ImGui.SliderFloat("##exposure", ref exposure, 0.4f, 2.5f, L("Luminosité x %.2f"), ImGuiSliderFlags.Logarithmic))
                settings.Exposure = exposure;
            SaveWhenReleased();

            Widgets.Property(L("Halo lumineux"), L("Les zones très lumineuses (soleil, ciel éblouissant, lampes et fenêtres allumées la nuit) débordent en une lueur douce, comme sur une vraie caméra."));
            bool bloom = settings.Bloom;
            if (ImGui.Checkbox("##bloom", ref bloom))
            {
                settings.Bloom = bloom;
                state.SaveSettings();
            }
            if (settings.Bloom)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1f);
                float glow = settings.BloomStrength * 100f;
                if (ImGui.SliderFloat("##bloom_strength", ref glow, 0f, 100f, L("Force %.0f %%")))
                    settings.BloomStrength = glow / 100f;
                SaveWhenReleased();
            }
        }

        Widgets.Property(L("Relief et reflets"), L("Bâtiments, véhicules et objets avec leurs textures de relief (briques, joints, tôles) et de brillance : reflets du soleil et du ciel sur les vitres, la carrosserie, le métal. Plus de mémoire vidéo ; recharge les modèles."));
        bool surfaces = settings.SurfaceDetail;
        if (ImGui.Checkbox("##surface_detail", ref surfaces))
        {
            settings.SurfaceDetail = surfaces;
            state.SaveSettings();
        }

        Widgets.Property(L("Anticrénelage (TAA)"), L("Lisse les bords en escalier et le scintillement des grillages, câbles et feuillages, en mêlant chaque image aux précédentes. Coûte peu ; léger flou pendant les mouvements rapides."));
        bool antialiasing = settings.Antialiasing;
        if (ImGui.Checkbox("##antialiasing", ref antialiasing))
        {
            settings.Antialiasing = antialiasing;
            state.SaveSettings();
        }

        Widgets.Property(L("Occlusion ambiante"), L("Assombrit les recoins, le pied des murs et le sol sous les objets, là où la lumière du ciel arrive moins. Normale = rapide, Haute = plus précise."));
        string[] occlusionNames = [L("Aucune"), L("Normale"), L("Haute")];
        int occlusion = Math.Clamp(settings.AmbientOcclusion, 0, 2);
        ImGui.SetNextItemWidth(settings.AmbientOcclusion > 0 ? ImGui.GetContentRegionAvail().X * 0.45f : -1f);
        if (ImGui.Combo("##ambient_occlusion", ref occlusion, occlusionNames, occlusionNames.Length))
        {
            settings.AmbientOcclusion = occlusion;
            state.SaveSettings();
        }
        if (settings.AmbientOcclusion > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            float strength = settings.OcclusionStrength * 100f;
            if (ImGui.SliderFloat("##occlusion_strength", ref strength, 0f, 200f, L("Force %.0f %%")))
                settings.OcclusionStrength = strength / 100f;
            SaveWhenReleased();
        }

        Widgets.Property(L("Résolution de rendu"), L("Part des pixels de la vue 3D vraiment calculés ; l'image est ensuite agrandie. Moins de 100 % = bien plus rapide sur un petit PC ou un écran 4K, mais moins net."));
        ImGui.SetNextItemWidth(-1f);
        float scale = settings.RenderScale * 100f;
        if (ImGui.SliderFloat("##render_scale", ref scale, 50f, 100f, "%.0f %%"))
            settings.RenderScale = MathF.Round(scale / 5f) * 0.05f;
        SaveWhenReleased();

        if (settings.HdrRendering || settings.Antialiasing || settings.RenderScale < 0.999f)
        {
            Widgets.Property(L("Netteté"), L("Renforce les détails de l'image agrandie ou lissée. 0 = image telle quelle."));
            ImGui.SetNextItemWidth(-1f);
            float sharpness = settings.Sharpness * 100f;
            if (ImGui.SliderFloat("##sharpness", ref sharpness, 0f, 100f, "%.0f %%"))
                settings.Sharpness = sharpness / 100f;
            SaveWhenReleased();
        }
    }

    private void SaveWhenReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            state.SaveSettings();
    }
}
