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
        if (Widgets.Section("Collision", "collision_tool"))
        {
            if (Widgets.BeginProperties("##collision_kind"))
            {
                Widgets.Property("Version");
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
                Widgets.PropertyText("Fichiers modifiés", files == 0 ? "aucun" : $"{files} .ybn");
                Widgets.EndProperties();
            }
            Widgets.Hint("Le jeu a plusieurs versions de chaque collision : piétons et objets, véhicules (hi@) et une troisième (ma@). "
                + "Pour ouvrir un passage à tous, modifie aussi la version Véhicules.", wrap: true);
        }

        var selection = state.SelectedCollision;
        if (selection.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 4));
            Widgets.Hint("Clic sur une surface de collision pour la sélectionner. Ctrl+clic : en ajouter ou en retirer. "
                + "Glisser : toutes les surfaces visibles dans le rectangle (Ctrl : ajouter). Échap : tout désélectionner.", wrap: true);
            return;
        }

        var catalog = state.World.Collisions;
        var materials = layer.MaterialsOf(selection);
        if (Widgets.Section($"Sélection ({selection.Count})", "collision_selection"))
        {
            if (Widgets.BeginProperties("##collision_selection"))
            {
                Widgets.PropertyText("Surfaces", selection.Count.ToString());
                var fileNames = selection.Select(p => Path.GetFileName(p.Path)).Distinct().ToList();
                Widgets.PropertyText("Fichiers", fileNames.Count == 1 ? fileNames[0] : $"{fileNames.Count} fichiers",
                    fileNames.Count > 1 ? string.Join("\n", fileNames.Take(20)) : null);
                var groups = materials.GroupBy(m => m.Value).OrderByDescending(g => g.Count()).Take(6).ToList();
                for (int i = 0; i < groups.Count; i++)
                {
                    string name = catalog?.Materials.Name(groups[i].Key) ?? $"#{groups[i].Key}";
                    Widgets.PropertyText(i == 0 ? "Matériau" : "", $"{name} ({groups[i].Count()})");
                }
                Widgets.EndProperties();
            }
        }

        if (catalog is not null && catalog.Materials.Count > 0 && Widgets.Section("Changer le matériau", "collision_material"))
        {
            Widgets.Hint("Le matériau règle le bruit des pas, les traces, l'adhérence des véhicules et les impacts.", wrap: true);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##material_filter", $"{Icons.Search}  Rechercher un matériau (herbe, sable, metal...)", ref _materialFilter, 64);
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

        ImGui.Dummy(new Vector2(0, 4));
        if (Widgets.Button($"{Icons.Trash}  Supprimer la sélection (Suppr)", "Retire ces surfaces de la collision : on passe au travers en jeu."))
            state.DeleteSelectedCollision();
        ImGui.SameLine();
        if (Widgets.Button("Désélectionner"))
            state.SelectCollision([], CollisionSelectMode.Replace);
    }
}
