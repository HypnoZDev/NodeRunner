using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Paths;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private void DrawPathNodePosition(PathNode node, PathNetwork network)
    {
        Widgets.Property(L("Position"), L("Mètres. X = est, Y = nord, Z = haut. Aussi avec les poignées (outil Déplacer, touche 2)."));
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
        var before = node.Position;
        var position = before;
        float width = Widgets.SplitWidth(3);
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            float value = position[i];
            if (Widgets.AxisField($"##node_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
            {
                position[i] = value;
                network.MoveNode(node, position);
            }
            if (ImGui.IsItemActivated())
                _nodeEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && node.Position != _nodeEditStart)
                state.History.Add(new PathNodeMoveEdit(network, node, _nodeEditStart, node.Position, L($"Déplacer le nœud {node.Id.AreaId}:{node.Id.NodeId}")));
        }
    }

    private OccluderBox? _boxEditStart;

    private void DrawPathNode(PathNode node, PathNetwork network)
    {
        var names = state.World.Names ?? new HashNames();
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Route}  Nœud {node.Id.AreaId}:{node.Id.NodeId}"));
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  nodes{node.Id.AreaId}.ynd");
        var p = node.Position;
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), state.FormatPosition(p)))
            ImGui.SetClipboardText(state.FormatPosition(p));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Supprime aussi ses liens ; les nœuds suivants du fichier sont renumérotés. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedPathNode();
            return;
        }
        if (Widgets.Button(L($"{Icons.Waypoints}  Ajouter un nœud relié"), L("Nouveau nœud à 10 m, relié à celui-ci (mêmes réglages), à placer ensuite avec les poignées.")))
        {
            state.AddPathNode(node);
            return;
        }
        ImGui.SameLine();
        bool linking = state.PathLinkSource == node;
        if (Widgets.Button(linking ? L($"{Icons.GitBranch}  Cliquez sur l'autre nœud...") : L($"{Icons.GitBranch}  Relier à un autre nœud"),
                L("Puis cliquez sur un autre nœud dans la vue 3D : ils sont reliés dans les deux sens. Échap pour annuler.")))
            state.PathLinkSource = linking ? null : node;

        if (Widgets.Section(L("Nœud"), "path_node") && Widgets.BeginProperties("##path_node"))
        {
            DrawPathNodePosition(node, network);
            Widgets.PropertyText(L("Catégorie"), PathNodeLayer.CategoryOf(node));
            if (node.StreetNameHash != 0)
                Widgets.PropertyText(L("Rue"), names.Describe(node.StreetNameHash));
            if (node.IsJunction)
                Widgets.PropertyText(L("Carrefour"), L("oui"), L("Les données de carrefour du jeu restent les mêmes."));
            DrawPathNodeValues(node, network);
            Widgets.EndProperties();
            Widgets.Hint(L("Enregistré dans le .ynd du projet au Ctrl+S, ajouté à l'export FiveM."), wrap: true);
        }

        var area = network.GetArea(node.Id.AreaId);
        var links = area?.LinksOf(node).ToList() ?? [];
        if (Widgets.Section(L($"Liens ({links.Count})"), "path_links") && links.Count > 0 && Widgets.BeginProperties("##path_links"))
        {
            foreach (var (link, i) in links.Select((l, i) => (l, i)))
            {
                var target = network.GetNode(link.Target);
                Widgets.Property(L($"Vers {link.Target.AreaId}:{link.Target.NodeId}"));
                if (target is not null && Widgets.IconButton(Icons.Trash, $"##delete_link{i}", L("Supprimer ce lien (dans les deux sens). Annulable avec Ctrl+Z.")))
                {
                    var edit = PathLinkDeleteEdit.Apply(network, node, target, L($"Supprimer le lien {node.Id.AreaId}:{node.Id.NodeId} - {target.Id.AreaId}:{target.Id.NodeId}"));
                    if (edit is not null)
                        state.History.Add(edit);
                    break;
                }
                if (target is not null)
                    ImGui.SameLine();
                string lanes = L($"{link.LanesForward} voie(s) aller, {link.LanesBackward} retour, {network.LinkLength(node, link)} m");
                if (link.IsShortcut) lanes += L(", raccourci");
                if (link.IsNarrowRoad) lanes += L(", étroite");
                if (link.DontUseForNavigation) lanes += L(", hors navigation");
                if (target is not null && Widgets.Link($"{lanes}##link{i}", L("Sélectionner ce nœud")))
                    state.SelectedPathNode = target;
                else if (target is null)
                    ImGui.TextColored(UiTheme.TextFaint, L($"{lanes} (zone non chargée)"));
                if (target is not null && DrawLinkLanes(node, target, link, i, network))
                    break;
            }
            Widgets.EndProperties();
        }
    }

    private static readonly string[] LaneCounts = ["0", "1", "2", "3", "4", "5", "6", "7"];

    // Lanes of a link, towards its target and back; the link back gets them swapped. True when they changed (the
    // links of the node were rebuilt).
    private bool DrawLinkLanes(PathNode node, PathNode target, PathLink link, int index, PathNetwork network)
    {
        Widgets.Property(L("   Voies aller / retour"), L("Nombre de voies de ce nœud vers l'autre (aller) et de l'autre vers celui-ci (retour), 0 à 7.\nLe lien dans l'autre sens est réglé pareil (inversé). Annulable avec Ctrl+Z."));
        int forward = link.LanesForward, backward = link.LanesBackward;
        float width = Widgets.SplitWidth(2);
        ImGui.SetNextItemWidth(width);
        bool changed = ImGui.Combo($"##lanes_forward{index}", ref forward, LaneCounts, LaneCounts.Length);
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.Combo($"##lanes_backward{index}", ref backward, LaneCounts, LaneCounts.Length);
        if (!changed)
            return false;
        var edit = PathLinkLanesEdit.Apply(network, node, target, forward, backward,
            L($"Voies du lien {node.Id.AreaId}:{node.Id.NodeId} - {target.Id.AreaId}:{target.Id.NodeId}"));
        if (edit is null)
            return false;
        state.History.Add(edit);
        return true;
    }

    private int? _densityDraft;

    // Speed, density, special type and flags of a node; one undo step per change (the density when the slider is
    // released, the network being rebuilt on every change).
    private void DrawPathNodeValues(PathNode node, PathNetwork network)
    {
        string change = L($"Modifier le nœud {node.Id.AreaId}:{node.Id.NodeId}");
        var values = node.Values;
        void Commit(PathNodeValues after)
        {
            if (after == values)
                return;
            network.SetValues(node, after);
            state.History.Add(new PathNodeValuesEdit(network, node, values, after, change));
        }

        Widgets.Property(L("Vitesse"), L("Vitesse des véhicules sur cette portion de route."));
        string[] speeds = [L("lente"), L("normale"), L("rapide"), L("très rapide")];
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##node_speed", speeds[values.Speed]))
        {
            for (int i = 0; i < speeds.Length; i++)
                if (ImGui.Selectable(speeds[i], i == values.Speed))
                    Commit(values.WithSpeed(i));
            ImGui.EndCombo();
        }

        Widgets.Property(L("Densité de trafic"), L("Quantité de véhicules créés ici : 0 = aucun, 15 = le plus."));
        int density = _densityDraft ?? values.Density;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##node_density", ref density, 0, 15, "%d / 15"))
            _densityDraft = Math.Clamp(density, 0, 15);
        if (ImGui.IsItemDeactivated())
        {
            if (_densityDraft is { } chosen)
                Commit(values.WithDensity(chosen));
            _densityDraft = null;
        }

        Widgets.Property(L("Type spécial"), L("Rôle particulier du nœud : place de parking, passage piéton, arrêt au feu..."));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##node_special", SpecialLabel(values.Special), ImGuiComboFlags.HeightLarge))
        {
            foreach (var type in Enum.GetValues<PathNodeSpecialType>())
                if (ImGui.Selectable(SpecialLabel(type), type == values.Special))
                    Commit(values.WithSpecial(type));
            ImGui.EndCombo();
        }

        foreach (var (flag, label, tooltip) in NodeFlags)
        {
            Widgets.Property(label, tooltip);
            bool on = values.Has(flag);
            if (ImGui.Checkbox($"##node_flag{(int)flag}", ref on))
                Commit(values.With(flag, on));
        }

        if (Simple)
            return;
        Widgets.Property(L("Octets bruts"), L("Flags 0 à 4 et 3 bits bas de l'octet de liens, tels que stockés dans le fichier .ynd, en hexadécimal."));
        string raw = $"{values.Flags0:X2} {values.Flags1:X2} {values.Flags2:X2} {values.Flags3:X2} {values.Flags4:X2} {values.LinkCountExtra:X2}";
        if (CommitText("##node_raw", raw, "", out string typed) && typed != raw)
        {
            var parts = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[6];
            bool valid = parts.Length == 6;
            for (int i = 0; valid && i < 6; i++)
                valid = byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]);
            if (valid && bytes[5] < 8)
                Commit(new PathNodeValues(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]));
            else
                state.Notify(L("Octets invalides : 6 nombres hexadécimaux attendus (ex. 02 00 40 00 08 02), le dernier de 00 à 07."), warning: true);
        }
    }

    private static (PathNodeFlag Flag, string Label, string Tooltip)[] NodeFlags =>
    [
        (PathNodeFlag.Highway, L("Autoroute"), L("Route marquée autoroute (freeway) par le jeu.")),
        (PathNodeFlag.Tunnel, L("Tunnel"), L("Portion de route dans un tunnel.")),
        (PathNodeFlag.OffRoad, L("Hors-piste"), L("Chemin de terre ou hors route.")),
        (PathNodeFlag.SlipRoad, L("Bretelle"), L("Voie d'entrée ou de sortie (slip lane).")),
        (PathNodeFlag.NoBigVehicles, L("Pas de gros véhicules"), L("Camions et bus évitent ce nœud.")),
        (PathNodeFlag.NoGps, L("Ignoré par le GPS"), L("Le GPS ne fait pas passer d'itinéraire par ce nœud.")),
        (PathNodeFlag.SwitchedOff, L("Sans trafic par défaut"), L("Route coupée : le jeu n'y crée pas de trafic, sauf si un script la rouvre.")),
    ];

    private static string SpecialLabel(PathNodeSpecialType type) => type switch
    {
        PathNodeSpecialType.None => L("aucun"),
        PathNodeSpecialType.ParkingSpace => L("place de parking"),
        PathNodeSpecialType.PedCrossing => L("passage piéton"),
        PathNodeSpecialType.PedAssistedMovement => L("déplacement piéton guidé"),
        PathNodeSpecialType.TrafficLightStop => L("arrêt au feu"),
        PathNodeSpecialType.StopSign => "stop",
        PathNodeSpecialType.Caution => L("prudence"),
        PathNodeSpecialType.PedCrossingNoWait => L("passage piéton sans attente"),
        PathNodeSpecialType.EmergencyVehiclesOnly => L("véhicules d'urgence seulement"),
        PathNodeSpecialType.OffRoadJunction => L("carrefour hors-piste"),
        _ => $"{(int)type}",
    };
}
