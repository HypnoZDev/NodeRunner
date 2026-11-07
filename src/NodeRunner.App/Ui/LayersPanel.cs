using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// Layers (tab of the side panel): one section per family with an eye for the whole group, one row per category
// (click: show or hide), the related settings, and the list of the maps loaded by scripts.
internal sealed class LayersPanel(AppState state, PathNodeLayer pathNodeLayer, ScenarioLayer scenarioLayer, EntityBoxLayer entityBoxLayer, RoomLayer roomLayer, OcclusionLayer occlusionLayer, OcclusionCuller occlusionCuller)
{
    private const int MaxScriptedMapsListed = 300;

    private IReadOnlyList<string> _knownScriptedMaps = [];
    private IReadOnlySet<string> _defaultHiddenScriptedMaps = new HashSet<string>();
    private string _scriptedMapSearch = "";

    public void DrawContent()
    {
        if (LayerSection(L("Modèles 3D"), "models", [ModelLayer.LayerName]))
        {
            LayerRow(ModelLayer.LayerName, new Vector4(0.75f, 0.74f, 0.71f, 1f), L("Carte en 3D"));
            SettingSlider(L("Distance de vue"), "view_distance", state.Settings.ViewDistanceMultiplier,
                ModelLayer.MinDistanceMultiplier, ModelLayer.MaxDistanceMultiplier, "x %.2f", v => state.Settings.ViewDistanceMultiplier = v,
                L("x 1 = distances du jeu. Plus grand : les détails restent visibles de plus loin."), ImGuiSliderFlags.Logarithmic);
        }

        if (LayerSection(L("Eau"), "water", [WaterLayer.LayerName]))
            LayerRow(WaterLayer.LayerName, new Vector4(0.25f, 0.55f, 0.80f, 1f), L("Mer, lacs et rivières"));

        if (LayerSection(L("Pathnodes"), "paths", PathNodeLayer.Categories.Select(PathNodeLayer.LayerName)))
        {
            foreach (var category in PathNodeLayer.Categories)
                LayerRow(PathNodeLayer.LayerName(category), PathNodeLayer.Color(category), PathNodeLayer.Label(category), Widgets.Count(pathNodeLayer.Count(category)));
        }

        if (LayerSection(L("Points de scénario"), "scenarios", ScenarioLayer.Categories.Select(ScenarioLayer.LayerName)))
        {
            foreach (var category in ScenarioLayer.Categories)
                LayerRow(ScenarioLayer.LayerName(category), ScenarioLayer.Color(category), ScenarioLayer.Label(category), Widgets.Count(scenarioLayer.Count(category)));
        }

        if (LayerSection(L("Navmesh"), "navmesh", NavMeshLayer.Surfaces.Select(NavMeshLayer.LayerName).Append(NavMeshLayer.OutlinesLayer)))
        {
            foreach (var surface in NavMeshLayer.Surfaces)
                LayerRow(NavMeshLayer.LayerName(surface), NavMeshLayer.Color(surface), NavMeshLayer.Label(surface));
            LayerRow(NavMeshLayer.OutlinesLayer, new Vector4(0.6f, 0.6f, 0.6f, 1f), L("Contours des polygones"));
            SettingSlider(L("Rayon de chargement"), "navmesh_radius", state.Settings.NavMeshRadius,
                NavMeshLayer.MinRadius, NavMeshLayer.MaxRadius, "%.0f m", v => state.Settings.NavMeshRadius = v);
        }

        if (LayerSection(L("Collisions"), "collisions", CollisionLayer.Surfaces.Select(CollisionLayer.LayerName), defaultOpen: false))
        {
            foreach (var surface in CollisionLayer.Surfaces)
                LayerRow(CollisionLayer.LayerName(surface), CollisionLayer.Color(surface), CollisionLayer.Label(surface));
            SettingSlider(L("Rayon de chargement"), "collision_radius", state.Settings.CollisionRadius,
                CollisionLayer.MinRadius, CollisionLayer.MaxRadius, "%.0f m", v => state.Settings.CollisionRadius = v);
        }

        if (LayerSection(L("Boîtes des objets"), "entities", EntityBoxLayer.Categories.Select(EntityBoxLayer.LayerName), defaultOpen: false))
        {
            foreach (var category in EntityBoxLayer.Categories)
                LayerRow(EntityBoxLayer.LayerName(category), EntityBoxLayer.Color(category), EntityBoxLayer.Label(category), Widgets.Count(entityBoxLayer.Count(category)));
            SettingSlider(L("Rayon d'affichage"), "entity_radius", state.Settings.EntityRadius,
                EntityBoxLayer.MinRadius, EntityBoxLayer.MaxRadius, "%.0f m", v => state.Settings.EntityRadius = v);
        }

        if (LayerSection(L("Salles et portails (intérieurs)"), "rooms", RoomLayer.Parts.Select(RoomLayer.LayerName), defaultOpen: false))
        {
            foreach (var part in RoomLayer.Parts)
                LayerRow(RoomLayer.LayerName(part), RoomLayer.Color(part), RoomLayer.Label(part),
                    part == RoomLayerPart.Rooms ? Widgets.Count(roomLayer.Rooms.Count) : null);
            Widgets.Hint(L($"Intérieurs à moins de {RoomLayer.Radius:0} m. Survolez une salle ou un portail pour voir ses détails, cliquez dessus pour le modifier."), wrap: true);
        }

        if (LayerSection(L("Occlusion"), "occlusion", OcclusionLayer.Parts.Select(OcclusionLayer.LayerName), defaultOpen: false))
        {
            foreach (var part in OcclusionLayer.Parts)
                LayerRow(OcclusionLayer.LayerName(part), OcclusionLayer.Color(part), OcclusionLayer.Label(part), Widgets.Count(occlusionLayer.Count(part)));
            Widgets.Hint(L($"Occluders à moins de {OcclusionLayer.Radius:0} m : le jeu ne dessine pas ce qui est caché derrière."), wrap: true);
            bool preview = state.Settings.OcclusionPreview;
            if (ImGui.Checkbox(L("Aperçu comme le jeu"), ref preview))
            {
                state.Settings.OcclusionPreview = preview;
                state.SaveSettings();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L($"Ne dessine plus les objets entièrement cachés derrière les occluders (à moins de {OcclusionCuller.Radius:0} m), ")
                    + L("comme le jeu. Un trou dans le décor = occluder mal placé. Les objets sélectionnés restent toujours affichés."));
            if (preview)
            {
                ImGui.SameLine();
                Widgets.Hint(L($"{occlusionCuller.HiddenCount} objet(s) caché(s)"));
            }
            if (Widgets.Button(L($"{Icons.Box}  Nouvelle boîte"), L("Boîte de 10 m posée au sol devant la caméra, à ajuster avec les poignées.")))
                state.CreateOccluderBox();
            ImGui.SameLine();
            int selected = state.SelectedEntities.Count;
            if (Widgets.Button(L($"{Icons.Boxes}  Sur la sélection{(selected > 1 ? $" ({selected})" : "")}"),
                    L("Pour chaque objet sélectionné : une boîte à l'intérieur de chaque bâtiment fermé de sa collision, sinon dans la boîte de son modèle. ")
                    + L("Pour un intérieur (MLO) ou un objet dedans : des boîtes fines dans ses murs extérieurs, portes et fenêtres laissées libres. ")
                    + L("Sélectionnez d'abord les bâtiments (calque Occlusion masqué, Ctrl+clic pour plusieurs)."), enabled: selected > 0))
                state.CreateOccluderBoxesFromSelection();
        }

        if (Widgets.Section(L("Cartes de mission (scripts)"), "scripted", defaultOpen: false))
            DrawScriptedMaps();
    }

    private bool LayerSection(string title, string id, IEnumerable<string> layers, bool defaultOpen = true)
    {
        var all = layers.ToList();
        bool any = all.Any(state.IsLayerVisible);
        bool open = Widgets.Section(title, id, defaultOpen, any ? Icons.Eye : Icons.EyeOff, any ? L("Tout masquer") : L("Tout afficher"), out bool toggle);
        if (toggle)
            foreach (var layer in all)
                state.SetLayerVisible(layer, !any);
        return open;
    }

    private void LayerRow(string layer, Vector4 color, string label, string? count = null)
    {
        bool visible = state.IsLayerVisible(layer);
        if (Widgets.LayerRow(layer, visible, color, label, count))
            state.SetLayerVisible(layer, !visible);
    }

    private void SettingSlider(string label, string id, float value, float min, float max, string format, Action<float> set,
        string? tooltip = null, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        ImGui.Dummy(new Vector2(0, 2));
        if (!Widgets.BeginProperties($"##{id}_grid", 0.45f))
            return;
        Widgets.Property(label, tooltip);
        if (ImGui.SliderFloat($"##{id}", ref value, min, max, format, flags))
            set(value);
        if (ImGui.IsItemDeactivatedAfterEdit())
            state.SaveSettings();
        Widgets.EndProperties();
    }

    // Maps the game only loads for missions or on a script's request (tunnel under the construction site, DLC
    // offices, apartments...): all shown by default except alternative versions of a same place.
    private void DrawScriptedMaps()
    {
        if (state.World.Maps is { ScriptedMapNames.Count: > 0 } maps)
        {
            _knownScriptedMaps = maps.ScriptedMapNames;
            _defaultHiddenScriptedMaps = maps.DefaultHiddenScriptedMaps;
        }
        var settings = state.Settings;
        var hidden = settings.HiddenScriptedMaps ?? _defaultHiddenScriptedMaps;
        int shownCount = _knownScriptedMaps.Count(name => !hidden.Contains(name));

        Widgets.Hint(L("Cartes chargées par le jeu pendant une mission ou à la demande d'un script (appartements et bureaux Online, ")
            + L("yachts, Cayo Perico...). Cochées, elles sont affichées. Par défaut, une seule version de chaque lieu."), wrap: true);
        ImGui.Dummy(new Vector2(0, 2));
        Widgets.SearchField("##scripted_search", L("Rechercher (ex. apa_v_mp)"), ref _scriptedMapSearch, 64);

        if (Widgets.Button(L("Par défaut##scripted"), L("Revenir au choix par défaut"), enabled: settings.HiddenScriptedMaps is not null))
        {
            settings.HiddenScriptedMaps = null;
            state.SaveSettings();
        }
        ImGui.SameLine(0, 4);
        if (Widgets.Button(L("Tout décocher##scripted"), enabled: shownCount > 0))
        {
            settings.HiddenScriptedMaps = _knownScriptedMaps.ToHashSet();
            state.SaveSettings();
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Widgets.Hint(L($"{shownCount} / {_knownScriptedMaps.Count} affichées"));

        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiTheme.Rgb(0x141518));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        bool visible = ImGui.BeginChild("##scripted_list", new Vector2(-1, 240), ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        if (visible)
        {
            var listed = _knownScriptedMaps
                .Where(name => _scriptedMapSearch.Length == 0 || name.Contains(_scriptedMapSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(MaxScriptedMapsListed)
                .ToList();
            foreach (var name in listed)
            {
                bool on = !hidden.Contains(name);
                if (ImGui.Checkbox(name, ref on))
                {
                    // The first change turns the default choice into the user's own list.
                    var own = settings.HiddenScriptedMaps ??= _defaultHiddenScriptedMaps.ToHashSet();
                    if (on) own.Remove(name); else own.Add(name);
                    state.SaveSettings();
                }
            }
            if (listed.Count == MaxScriptedMapsListed)
                Widgets.Hint(L("... (affine la recherche pour voir les autres)"));
        }
        ImGui.EndChild();
    }
}
