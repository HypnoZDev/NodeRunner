using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private string _openingDoorQuery = "";

    // The same settings in the model workshop (Percer tool) while this building is being opened.
    public void DrawBuildingOpeningFor(MapEntity entity)
    {
        if (state.World.Maps is { } library)
            DrawBuildingOpening(entity, library, state.World.Names ?? new HashNames());
    }

    // The other objects the opening goes through (a door frame laid on the wall): cut too when ticked; a model used
    // several times on the map is cut everywhere, so it starts unticked.
    private void DrawOpeningOthers(World.BuildingOpeningTool tool)
    {
        if (tool.Placed is null)
            return;
        // Found again once the opening stops changing (not while a value is dragged).
        if (!ImGui.IsAnyItemActive())
            state.UpdateOpeningOthers(tool);
        if (tool.Others.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.SeparatorText(L("Autres objets traversés"));
        Widgets.Hint(L("L'ouverture traverse aussi ces objets (encadrement de porte, morceau de façade) : cochés, leur modèle est percé avec le bâtiment."), wrap: true);
        for (int i = 0; i < tool.Others.Count; i++)
        {
            var other = tool.Others[i];
            ImGui.Checkbox(L($"{other.Name} ({other.Triangles} triangles)##opening_other{i}"), ref other.Cut);
            if (other.Copies > 1)
                ImGui.TextColored(UiTheme.Warning, L($"{Icons.TriangleAlert}  Utilisé {other.Copies} fois sur la carte : toutes les copies seront percées au même endroit."));
        }
    }

    // An interior behind a door drawn on the building's wall: the opening is placed by a click on the wall, then sized;
    // "Ouvrir le bâtiment" makes it all (AppState.OpenBuilding).
    private void DrawBuildingOpening(MapEntity entity, MapLibrary library, HashNames names)
    {
        float width = ImGui.GetContentRegionAvail().X;
        if (state.OpeningTool is not { } tool)
        {
            Widgets.Hint(L("Pour une porte dessinée sur le mur (pas un objet à part) : NodeRunner perce le mur du modèle et sa collision, ")
                + L("crée un intérieur (MLO) derrière avec son portail et pose une vraie porte qui s'ouvre."), wrap: true);
            Widgets.Hint(L("Expérimental : les bâtiments du jeu sont souvent faits de plusieurs objets ; vérifiez l'aperçu avant d'ouvrir."), wrap: true);
            if (Widgets.Button(L($"{Icons.DoorOpen}  Ouvrir ce bâtiment... (expérimental)"), L("Le bâtiment s'ouvre dans l'Atelier, outil Percer : cliquez ensuite sur la porte dessinée."), width))
            {
                state.StartBuildingOpening(entity);
                if (state.OpeningTool is not null)
                    state.WorkshopRequest = entity;
            }
            return;
        }

        if (tool.Placed is null)
            Widgets.Hint(state.WorkshopShown
                ? L($"{Icons.MousePointer}  Cliquez sur la porte dessinée sur le mur, dans la vue de l'Atelier.")
                : L($"{Icons.MousePointer}  Cliquez sur la porte dessinée sur le mur, dans la vue 3D (Échap : arrêter)."), wrap: true);
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
        ImGui.Checkbox(L("Créer une pièce simple (sol, murs, plafond)"), ref tool.AddRoom);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Une pièce de la taille de la salle, avec les matières du bâtiment et sa collision, pour ne pas avoir un vide derrière la porte.\n")
                + L("Ses textures se changent ensuite dans l'atelier de modèle (modèle <nom>_shell)."));
        ImGui.Checkbox(L("Percer la collision du bâtiment"), ref tool.CutCollision);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Sinon un mur invisible reste à la place de la porte. Les fichiers .ybn changés vont dans le projet."));
        ImGui.Checkbox(L("Retirer les occluders sur l'intérieur"), ref tool.RemoveOccluders);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(L("Les boîtes d'occlusion du jeu dans la salle la feraient disparaître en jeu."));
        ImGui.Checkbox(L("Retirer les portes du jeu dans l'ouverture"), ref tool.RemoveGameDoors);
        if (changed)
            tool.Changed();
        DrawOpeningOthers(tool);

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
