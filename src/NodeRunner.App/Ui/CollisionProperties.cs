using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Collisions;

namespace NodeRunner.App.Ui;

// Properties panel while the collision tool is on: version shown, selected polygons, their materials, change of
// material, removal.
internal sealed class CollisionProperties(AppState state, CollisionLayer layer)
{
    private string _materialFilter = "";

    public void Draw()
    {
        var settings = state.Settings;
        if (Widgets.Section(L("Collision"), "collision_tool"))
        {
            if (Widgets.BeginProperties("##collision_kind"))
            {
                Widgets.Property(L("Version"));
                if (ImGui.BeginCombo("##kind", CollisionLayer.KindLabel(settings.CollisionKind)))
                {
                    foreach (var kind in Enum.GetValues<CollisionKind>())
                    {
                        if (ImGui.Selectable(CollisionLayer.KindLabel(kind), kind == settings.CollisionKind) && kind != settings.CollisionKind)
                        {
                            settings.CollisionKind = kind;
                            state.SaveSettings();
                            state.SelectCollision([], CollisionSelectMode.Replace);
                        }
                    }
                    ImGui.EndCombo();
                }
                int files = state.CollisionEdits.Files.Count;
                Widgets.PropertyText(L("Fichiers modifiés"), files == 0 ? L("aucun") : $"{files} .ybn");
                Widgets.EndProperties();
            }
            Widgets.Hint(L("Le jeu a plusieurs versions de chaque collision : piétons et objets, véhicules (hi@) et une troisième (ma@). ")
                + L("Pour ouvrir un passage à tous, modifie aussi la version Véhicules."), wrap: true);
        }

        if (Widgets.Section(L("Ajouter des surfaces"), "collision_add"))
            DrawAdding();

        var selection = state.SelectedCollision;
        if (selection.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 4));
            Widgets.Hint(L("Clic sur une surface de collision pour la sélectionner. Ctrl+clic : en ajouter ou en retirer. ")
                + L("Glisser : toutes les surfaces visibles dans le rectangle (Ctrl : ajouter). Échap : tout désélectionner."), wrap: true);
            return;
        }

        var catalog = state.World.Collisions;
        var materials = layer.MaterialsOf(selection, state.CollisionSelectionVersion);
        if (Widgets.Section(L($"Sélection ({selection.Count})"), "collision_selection"))
        {
            if (Widgets.BeginProperties("##collision_selection"))
            {
                Widgets.PropertyText(L("Surfaces"), selection.Count.ToString());
                var fileNames = selection.Select(p => Path.GetFileName(p.Path)).Distinct().ToList();
                Widgets.PropertyText(L("Fichiers"), fileNames.Count == 1 ? fileNames[0] : L($"{fileNames.Count} fichiers"),
                    fileNames.Count > 1 ? string.Join("\n", fileNames.Take(20)) : null);
                var groups = materials.GroupBy(m => m.Value).OrderByDescending(g => g.Count()).Take(6).ToList();
                for (int i = 0; i < groups.Count; i++)
                {
                    string name = catalog?.Materials.Name(groups[i].Key) ?? $"#{groups[i].Key}";
                    Widgets.PropertyText(i == 0 ? L("Matériau") : "", $"{name} ({groups[i].Count()})");
                }
                Widgets.EndProperties();
            }
        }

        if (catalog is not null && catalog.Materials.Count > 0 && Widgets.Section(L("Changer le matériau"), "collision_material"))
        {
            Widgets.Hint(L("Le matériau règle le bruit des pas, les traces, l'adhérence des véhicules et les impacts."), wrap: true);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##material_filter", L($"{Icons.Search}  Rechercher un matériau (herbe, sable, metal...)"), ref _materialFilter, 64);
            if (ImGui.BeginListBox("##materials", new Vector2(-1, 180)))
            {
                string filter = _materialFilter.Trim();
                var current = materials.Values.Distinct().Count() == 1 ? materials.Values.First() : (byte?)null;
                for (int i = 0; i < catalog.Materials.Count && i < 256; i++)
                {
                    string name = catalog.Materials.Name((byte)i);
                    if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var color = CollisionLayer.Color(catalog.Materials.Surface((byte)i));
                    ImGui.TextColored(color, "■");
                    ImGui.SameLine();
                    if (ImGui.Selectable($"{name}##m{i}", current == i))
                        state.SetSelectedCollisionMaterial((byte)i);
                }
                ImGui.EndListBox();
            }
        }

        if (Widgets.Section(L("Options de la surface"), "collision_flags"))
            DrawFlags(layer.FlagsOf(selection, state.CollisionSelectionVersion));

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer la sélection (Suppr)"), L("Retire ces surfaces de la collision : on passe au travers en jeu.")))
            state.DeleteSelectedCollision();
        ImGui.SameLine();
        if (Widgets.Button(L("Désélectionner")))
            state.SelectCollision([], CollisionSelectMode.Replace);
    }

    // Drawing new surfaces with clicks in the 3D view (CollisionDrawTool).
    private void DrawAdding()
    {
        var drawing = state.CollisionDraw;
        if (drawing is null)
        {
            if (Widgets.Button(L($"{Icons.Plus}  Triangle"), L("3 clics dans la vue 3D : les 3 coins du triangle.")))
                state.CollisionDraw = new CollisionDrawTool(quad: false);
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.Plus}  Rectangle"), L("4 clics dans la vue 3D : les 4 coins, dans l'ordre du tour.")))
                state.CollisionDraw = new CollisionDrawTool(quad: true);
            Widgets.Hint(L("Le 1er coin se pose sur une collision existante : il choisit le fichier et le matériau. Les suivants peuvent être dans le vide, à la hauteur du 1er. Les coins s'aimantent aux coins proches."), wrap: true);
            return;
        }
        Widgets.Hint(drawing.Quad ? L($"Rectangle : coin {drawing.Corners.Count + 1} sur 4. Échap : effacer les coins posés, puis arrêter.")
            : L($"Triangle : coin {drawing.Corners.Count + 1} sur 3. Échap : effacer les coins posés, puis arrêter."), wrap: true);
        if (Widgets.Button(L("Arrêter de dessiner")))
            state.CollisionDraw = null;
    }

    // One box per option (CollisionFlags), in the game's order; ticked when every selected surface has it, shown as
    // partly ticked when only some have it.
    private void DrawFlags(Dictionary<Editor.CollisionPolygon, (ushort Shown, ushort Game)> flags)
    {
        Widgets.Hint(L("Règles du jeu pour ces surfaces, en plus du matériau. Survole une option pour son effet."), wrap: true);
        if (flags.Count == 0)
            return;
        var options = Options();
        for (int i = 0; i < CollisionFlags.All.Count; i++)
        {
            var (bit, gameName) = CollisionFlags.All[i];
            var (label, effect) = options[i];
            int with = flags.Values.Count(f => (f.Shown & bit) != 0);
            bool all = with == flags.Count;
            bool some = with > 0 && !all;
            bool ticked = all;
            string text = some ? L($"{label} (certaines)") : label;
            if (ImGui.Checkbox($"{text}##flag{i}", ref ticked))
                state.SetSelectedCollisionFlag(flags, bit, !all, label);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip($"{effect}\n{gameName}");
        }
    }

    // Name and effect of each option, in the order of CollisionFlags.All.
    private static (string Label, string Effect)[] Options() =>
    [
        (L("Escalier"), L("Les personnages montent et descendent cette surface comme des marches.")),
        (L("Pas d'escalade"), L("Le joueur ne peut pas grimper dessus.")),
        (L("On voit à travers"), L("Les personnages voient à travers (vitres, grillages).")),
        (L("On tire à travers"), L("Les balles la traversent (feuillage, grillage fin).")),
        (L("Pas un abri"), L("Ne sert pas de couverture pendant les fusillades.")),
        (L("Chemin praticable"), L("Marquée comme chemin pour les piétons.")),
        (L("La caméra traverse"), L("La caméra ne bute pas contre (rambardes, grilles).")),
        (L("Impacts traversants"), L("Les impacts s'affichent mais la balle continue (bâches, vitres fines).")),
        (L("Pas de traces"), L("Ni traces d'impact ni sang dessus.")),
        (L("Hors navmesh"), L("Ignorée quand le jeu calcule où les personnages peuvent marcher.")),
        (L("Pas de ragdoll"), L("Les corps inertes ne la heurtent pas.")),
        (L("Roue de véhicule"), L("Réservé aux roues des véhicules.")),
        (L("Pas de particules"), L("Ni poussière, ni étincelles, ni éclaboussures aux impacts.")),
        (L("Trop pentu pour le joueur"), L("Le joueur glisse dessus au lieu d'y marcher.")),
        (L("Pas d'apparition en ligne"), L("Personne n'apparaît dessus en multijoueur.")),
        (L("La caméra traverse (sans recul)"), L("La caméra passe au travers sans se rapprocher du joueur.")),
    ];
}
