using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private string _openingDoorQuery = "";

    // An interior behind a door drawn on the building's wall: the opening is placed by a click on the wall, then sized;
    // "Ouvrir le bâtiment" makes it all (AppState.OpenBuilding).
    private void DrawBuildingOpening(MapEntity entity, MapLibrary library, HashNames names)
    {
        float width = ImGui.GetContentRegionAvail().X;
        if (state.OpeningTool is not { } tool)
        {
            Widgets.Hint(L("Pour une porte dessinée sur le mur (pas un objet à part) : NodeRunner perce le mur du modèle et sa collision, ")
                + L("crée un intérieur (MLO) derrière avec son portail et pose une vraie porte qui s'ouvre."), wrap: true);
            if (Widgets.Button(L($"{Icons.DoorOpen}  Ouvrir ce bâtiment..."), L("Cliquez ensuite sur la porte dessinée, dans la vue 3D."), width))
                state.StartBuildingOpening(entity);
            return;
        }

        if (tool.Placed is null)
            Widgets.Hint(L($"{Icons.MousePointer}  Cliquez sur la porte dessinée sur le mur, dans la vue 3D (Échap : arrêter)."), wrap: true);
        else
            Widgets.Hint(L("Rouge : l'ouverture et jusqu'où elle traverse le mur. Vert : la salle de l'intérieur. Un autre clic sur le mur la déplace."), wrap: true);

        bool changed = false;
        if (Widgets.BeginProperties("##building_opening"))
        {
            Widgets.Property(L("Ouverture (m)"), L("Largeur et hauteur du trou percé dans le mur."));
            var size = new Vector2(tool.Width, tool.Height);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat2("##opening_size", ref size, 0.01f, 0.5f, 10f, "%.2f"))
            {
                (tool.Width, tool.Height) = (Math.Clamp(size.X, 0.5f, 10f), Math.Clamp(size.Y, 1f, 10f));
                changed = true;
            }
            if (tool.Placed is not null)
            {
                Widgets.Property(L("Décalage (m)"), L("Déplace l'ouverture le long du mur (droite, haut) depuis l'endroit cliqué."));
                ImGui.SetNextItemWidth(-1);
                changed |= ImGui.DragFloat2("##opening_shift", ref tool.Shift, 0.01f, -20f, 20f, "%.2f");
            }
            Widgets.Property(L("Salle (m)"), L("Largeur, profondeur (dans le bâtiment) et hauteur de la salle de l'intérieur. Ajustable ensuite avec ses poignées."));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat3("##opening_room", ref tool.RoomSize, 0.05f, 1f, 60f, "%.1f"))
            {
                tool.RoomSize = Vector3.Clamp(tool.RoomSize, new Vector3(1f), new Vector3(60f));
                changed = true;
            }
            Widgets.Property(L("Nom de l'intérieur"), L("Nom du type d'intérieur et de son fichier .ytyp (lettres minuscules, chiffres et _)."));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##opening_name", ref tool.Name, 48);
            Widgets.EndProperties();
        }
        if (!state.IsFreeInteriorName(tool.Name))
            ImGui.TextColored(UiTheme.Warning, L($"{Icons.TriangleAlert}  Nom invalide ou déjà pris."));

        ImGui.Checkbox(L("Poser une porte qui s'ouvre"), ref tool.AddDoor);
        if (tool.AddDoor)
        {
            var types = DoorTypes(library, names).Where(t => t.Kind == DoorKind.Hinged).ToArray();
            ImGui.SetNextItemWidth(width);
            if (ImGui.BeginCombo("##opening_door", tool.Door?.Name ?? L("Choisir une porte..."), ImGuiComboFlags.HeightLarge))
            {
                if (ImGui.IsWindowAppearing())
                {
                    _openingDoorQuery = "";
                    ImGui.SetKeyboardFocusHere();
                }
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##opening_door_query", L("Filtrer (ex. v_ilev, glass)"), ref _openingDoorQuery, 64);
                ImGui.Separator();
                string query = _openingDoorQuery.Trim();
                foreach (var t in types.Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => MathF.Abs(t.Width - tool.Width)).ThenBy(t => t.Name, StringComparer.Ordinal).Take(MaxTypeChoices))
                {
                    if (ImGui.Selectable(L($"{t.Name}  ·  {t.Width:0.00} x {t.Height:0.00} m##opening_door{t.Hash}"), tool.Door?.Hash == t.Hash))
                        tool.Door = (t.Hash, t.Name);
                }
                ImGui.EndCombo();
            }
            if (tool.Door is { } door && library.GetArchetype(door.Hash) is { } doorType)
            {
                float doorWidth = BuildingOpening.WidthOf(doorType, door.Name);
                if (MathF.Abs(doorWidth - tool.Width) > 0.05f
                    && Widgets.Button(L($"Ouverture à la largeur de la porte ({doorWidth:0.00} m)"), L("La porte remplit alors toute l'ouverture."), width))
                {
                    tool.Width = doorWidth;
                    changed = true;
                }
            }
        }
        ImGui.Checkbox(L("Percer la collision du bâtiment"), ref tool.CutCollision);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Sinon un mur invisible reste à la place de la porte. Les fichiers .ybn changés vont dans le projet."));
        ImGui.Checkbox(L("Retirer les occluders sur l'intérieur"), ref tool.RemoveOccluders);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Les boîtes d'occlusion du jeu dans la salle la feraient disparaître en jeu."));
        ImGui.Checkbox(L("Retirer les portes du jeu dans l'ouverture"), ref tool.RemoveGameDoors);
        if (changed)
            tool.Changed();

        ImGui.Spacing();
        ImGui.BeginDisabled(tool.Placed is null || state.Project is null || !state.IsFreeInteriorName(tool.Name));
        bool open = Widgets.PrimaryButton(L($"{Icons.DoorOpen}  Ouvrir le bâtiment"), width);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(state.Project is null ? L("Ouvrez ou créez d'abord un projet.")
                : L("Crée l'intérieur et le reste, enregistre le projet, perce le modèle puis recharge la carte.\n")
                + L("Ctrl+Z ne l'annule plus ensuite : « Remettre le modèle d'origine » (Morceaux du modèle) et Suppr sur l'intérieur le font."));
        if (open)
            state.OpenBuilding();
        if (Widgets.Button(L("Arrêter"), L("Quitte sans rien changer (Échap)."), width))
            state.OpeningTool = null;
    }
}
