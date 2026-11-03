using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// "Display settings" window: quality presets for smaller PCs (view distance, fog, texture filtering, shadows), sky
// and time of day, VSync.
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
