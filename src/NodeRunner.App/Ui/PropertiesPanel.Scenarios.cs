using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    // Heading of a scenario point in degrees, as the game counts it (0 = north, turning left).
    private static float ScenarioHeading(NodeRunner.Data.Scenarios.ScenarioPoint point) =>
        ((point.Heading * 180f / MathF.PI) % 360f + 360f) % 360f;

    private void DrawScenario(NodeRunner.Data.Scenarios.ScenarioPoint point, HashNames names)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Users}  Point de scénario {point.Index}"));
        ImGui.PopFont();
        if (point.Region is { } region)
        {
            Widgets.Hint($"{Icons.File}  {region.Name}.ymt");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort) && region.Path is { } path)
                ImGui.SetTooltip(path);
        }
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller au point")))
            state.FocusOn(point.Position, 4f);
        ImGui.SameLine();
        string copied = state.FormatPosition(point.Position, ScenarioHeading(point));
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        if (Widgets.Button(L($"{Icons.Copy}  Dupliquer"), L("Nouveau point à 2 m, avec les mêmes réglages, à placer ensuite avec les poignées.")))
        {
            state.AddScenarioPoint(point);
            return;
        }
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedScenario();
            return;
        }

        string Name(uint hash, string empty) => hash == 0 || hash == NoneHash ? empty : names.Describe(hash);
        if (state.World.Scenarios is not { } set)
            return;
        string change = L($"Modifier le point de scénario {point.Index}");
        void Live(NodeRunner.Data.Scenarios.ScenarioValues after) => set.SetValues(point, after);
        void Commit(NodeRunner.Data.Scenarios.ScenarioValues after)
        {
            var before = point.Values;
            if (after == before)
                return;
            Live(after);
            state.History.Add(new ScenarioEdit(set, point, before, after, change));
        }
        // Live change while a field is dragged; one undo step when it is released.
        void Track(NodeRunner.Data.Scenarios.ScenarioValues before)
        {
            if (ImGui.IsItemActivated())
                _scenarioEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _scenarioEditStart is { } start)
            {
                if (point.Values != start)
                    state.History.Add(new ScenarioEdit(set, point, start, point.Values, change));
                _scenarioEditStart = null;
            }
        }
        void ByteField(string id, byte value, int min, int max, string format, Func<NodeRunner.Data.Scenarios.ScenarioValues, byte, NodeRunner.Data.Scenarios.ScenarioValues> with)
        {
            var current = point.Values;
            int v = value;
            if (ImGui.DragInt(id, ref v, 0.2f, min, max, format))
                Live(with(current, (byte)Math.Clamp(v, min, max)));
            Track(current);
        }
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        if (Widgets.Section(L("Scénario"), "scenario") && Widgets.BeginProperties("##scenario"))
        {
            Widgets.Property(L("Position"), L("Pied du poteau, en mètres. X = est, Y = nord, Z = haut."));
            float width = Widgets.SplitWidth(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = point.Values;
                var position = current.Position;
                float value = position[i];
                if (Widgets.AxisField($"##scenario_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    position[i] = value;
                    Live(current with { Position = position });
                }
                Track(current);
            }

            Widgets.Property(L("Cap"), L("Sens dans lequel le PNJ ou le véhicule regarde, en degrés depuis le nord (comme le cap d'un vector4 FiveM)."));
            {
                var current = point.Values;
                float value = ScenarioHeading(point);
                if (Widgets.AxisField("##scenario_heading", ref value, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                    Live(current with { Heading = ((value % 360f + 360f) % 360f) * MathF.PI / 180f });
                Track(current);
            }

            Widgets.Property(L("Type"), L("Activité jouée à ce point (liste des scénarios du jeu, scenarios.meta)."));
            if (point.Region is not null)
            {
                var types = set.Types.ToList();
                string shown = point.TypeName ?? names.Describe(point.TypeHash);
                if (ImGui.BeginCombo("##scenario_type", shown, ImGuiComboFlags.HeightLarge))
                {
                    foreach (var (hash, name) in types)
                    {
                        if (ImGui.Selectable(name, hash == point.TypeHash))
                            Commit(point.Values with { TypeHash = hash });
                    }
                    ImGui.EndCombo();
                }
            }
            else
                ImGui.TextUnformatted(point.TypeName ?? names.Describe(point.TypeHash));
            Widgets.PropertyText(L("Catégorie"), NodeRunner.App.World.ScenarioLayer.Label(point.Category));

            Widgets.Property(L("Horaires"), L("Heure de début et de fin (0 à 24). Les deux pareilles = toute la journée."));
            {
                float half = Widgets.SplitWidth(2);
                ImGui.SetNextItemWidth(half);
                ByteField("##scenario_start", point.StartHour, 0, 24, L("de %d h"), (v, b) => v with { StartHour = b });
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.SetNextItemWidth(half);
                ByteField("##scenario_end", point.EndHour, 0, 24, L("à %d h"), (v, b) => v with { EndHour = b });
            }

            Widgets.Property(L("Probabilité"), L("Chance d'utiliser le point, en %. 0 = celle du type."));
            ByteField("##scenario_probability", point.Probability, 0, 100, point.Probability == 0 ? L("celle du type") : "%d %%", (v, b) => v with { Probability = b });

            Widgets.Property(L("Disponible"));
            {
                string[] modes = [L("solo et Online"), L("Online seulement"), L("solo seulement")];
                int mode = Math.Min((int)point.Availability, 2);
                if (ImGui.Combo("##scenario_availability", ref mode, modes, modes.Length))
                    Commit(point.Values with { Availability = (byte)mode });
            }

            Widgets.Property(L("Rayon"), L("Mètres autour du point où le PNJ peut se placer ; 0 = sur le point."));
            ByteField("##scenario_radius", point.Radius, 0, 255, "%d m", (v, b) => v with { Radius = b });

            Widgets.Property(L("Le PNJ part après"), L("Secondes avant que le PNJ quitte le point ; 0 = quand il veut."));
            ByteField("##scenario_leave", point.TimeTillPedLeaves, 0, 255, "%d s", (v, b) => v with { TimeTillPedLeaves = b });

            Widgets.PropertyText(point.Category == NodeRunner.Data.Scenarios.ScenarioCategory.Vehicle ? L("Véhicules") : L("Modèles"),
                Name(point.ModelSetHash, L("tous")), L("Groupe de modèles (model set) qui peut apparaître ici."));
            {
                var preview = state.ScenarioPreview;
                string source = preview.Source switch
                {
                    NodeRunner.Data.Scenarios.ScenarioModelSource.Point => L("groupe du point"),
                    NodeRunner.Data.Scenarios.ScenarioModelSource.Type => L("groupe du type de scénario"),
                    _ => L("exemple (le jeu choisit dans la population de la zone)"),
                };
                string shown = !state.IsLayerVisible(ScenarioLayer.LayerName(point.Category)) ? L("calque masqué")
                    : preview.ModelName is not { } shownName ? L("aucun")
                    : preview.Error is { } previewError ? L($"{shownName} (illisible : {previewError})")
                    : preview.IsLoading ? L($"{shownName} (chargement...)")
                    : $"{shownName} · {source}";
                Widgets.PropertyText(L("Aperçu"), shown,
                    L("Modèle du jeu affiché sur le point dans la vue 3D, pour juger la place et le sens. Le jeu peut en choisir un autre."));
                if (preview.ModelName is not null && point.Category != NodeRunner.Data.Scenarios.ScenarioCategory.Vehicle)
                {
                    Widgets.PropertyText(L("Animation"), preview.Clip is { } clip ? state.ScenarioClips.Describe(clip.Clip) : L("aucune (pose de base)"),
                        L("Clip joué en boucle par le modèle de l'aperçu, choisi comme le jeu d'après le type de scénario (scenarios.meta) et le sexe du PNJ."));
                    if (preview.Clip?.Prop is { } prop)
                        Widgets.PropertyText(L("Objet tenu"), preview.Clip.LeftHand ? L($"{prop} (main gauche)") : L($"{prop} (main droite)"),
                            L("Objet que le jeu met dans la main du PNJ pour cette animation (PropSet de scenarios.meta, propsets.meta)."));
                }
            }
            if (point.Region is { } owner && owner.GroupOf(point.Index) is var cluster and >= 0)
                Widgets.PropertyText(L("Ensemble"), L($"groupe {cluster} ({owner.ClusterSizes[cluster]} points)"),
                    L("Les points d'un ensemble (cluster) apparaissent tous en même temps (ex. un groupe de PNJ qui discutent)."));
            if (Name(point.GroupHash, "") is { Length: > 0 } group)
                Widgets.PropertyText(L("Groupe"), group, L("Les points d'un groupe sont activés ou coupés ensemble (par un script)."));
            if (Name(point.InteriorHash, "") is { Length: > 0 } interior)
                Widgets.PropertyText(L("Intérieur"), interior);
            if (Name(point.RequiredMapHash, "") is { Length: > 0 } map)
                Widgets.PropertyText(L("YMAP requis"), map, L("Le point n'est utilisé que si ce fichier de carte est chargé."));
            if (!Simple)
                Widgets.PropertyText(L("Drapeaux"), $"0x{point.Flags:X8}");
            Widgets.EndProperties();
            Widgets.Hint(L("Enregistré dans le .ymt du projet au Ctrl+S, ajouté à l'export FiveM."), wrap: true);
        }
    }

    private NodeRunner.Data.Scenarios.ScenarioValues? _scenarioEditStart;
}
