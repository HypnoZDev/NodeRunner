using System.Globalization;
using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// The GTA IV object clicked in the 3D view: what the map files say about it, read only.
internal sealed partial class PropertiesPanel
{
    private void DrawGta4()
    {
        if (state.Gta4Selection is not { } entity || state.Gta4.Scene is not { } scene)
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.TextColored(UiTheme.TextMuted, L("Aucun objet sélectionné"));
            Widgets.Hint(L("Clic gauche sur un objet de Liberty City pour voir ses propriétés (lecture seule)."), wrap: true);
            return;
        }

        var names = scene.Names;
        var definition = scene.Definitions.Find(entity.ArchetypeHash);
        var archetype = scene.Library.GetArchetype(entity.ArchetypeHash);
        string name = definition?.Name ?? names.Describe(entity.ArchetypeHash);

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(Widgets.Fit(name, ImGui.GetContentRegionAvail().X));
        ImGui.PopFont();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip(name);
        Widgets.Hint(L("GTA IV : lecture seule"));
        ImGui.Dummy(new Vector2(0, 4));

        float buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à l'objet"), L("Placer la caméra devant l'objet"), buttonWidth))
            state.FocusOn(entity);
        ImGui.SameLine();
        string position = state.FormatPosition(entity.Position);
        string withHeading = state.FormatPosition(entity.Position, FiveMFormat.Heading(entity));
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), L($"Copier {position}\nClic droit : avec le cap ({withHeading})"), buttonWidth))
            ImGui.SetClipboardText(position);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.SetClipboardText(withHeading);
        if (archetype is not null && scene.Models.Locate(archetype) is not null
            && Widgets.Button(L($"{Icons.Box}  Voir le modèle dans la visionneuse"), L("Le modèle seul, à faire tourner, avec ses pièces et ses textures (F2)"),
                ImGui.GetContentRegionAvail().X))
            showModel(entity.ArchetypeHash);

        if (Widgets.Section(L("Transformation"), "gta4_transform") && Widgets.BeginProperties("##gta4_transform"))
        {
            var p = entity.Position;
            var angles = EulerAngles.FromQuaternion(entity.Orientation);
            Widgets.PropertyText(L("Position"), Invariant($"{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}"), L("Mètres. X = est, Y = nord, Z = haut."));
            Widgets.PropertyText(L("Rotation (°)"), Invariant($"{angles.X:0.0}, {angles.Y:0.0}, {angles.Z:0.0}"), L("Degrés autour des axes X, Y et Z."));
            Widgets.PropertyText(L("Cap (°)"), Invariant($"{FiveMFormat.Heading(entity):0.0}"));
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Fichiers"), "gta4_files") && Widgets.BeginProperties("##gta4_files"))
        {
            FileRow(L("Placement"), entity.Map?.FilePath);
            if (entity.IsInterior && archetype is not null)
                Widgets.PropertyText(L("Intérieur"), L($"{archetype.InteriorEntities.Count} objets, {Math.Max(0, archetype.InteriorRooms.Count - 1)} salles, {archetype.InteriorPortals.Count} portails"),
                    L("Défini dans un fichier .ide (section mlo) ; souvent le bâtiment entier, murs extérieurs compris."));
            else
                FileRow(L("Modèle"), archetype is null ? null : scene.Models.Locate(archetype)?.Path);
            if (entity.Interior is { } interior)
            {
                string room = scene.Library.GetArchetype(interior.ArchetypeHash) is { } type && entity.InteriorRoom >= 0 && entity.InteriorRoom < type.InteriorRooms.Count
                    ? $" ({type.InteriorRooms[entity.InteriorRoom].Name})"
                    : "";
                Widgets.PropertyText(L("Intérieur"), names.Describe(interior.ArchetypeHash) + room);
            }
            if (definition is not null)
            {
                string? textures = scene.Textures.Locate(archetype?.TextureDictionaryHash ?? 0);
                Widgets.PropertyText(L("Textures"), $"{definition.TextureDictionary}.wtd", textures ?? L("fichier introuvable"));
                string? parent = scene.Definitions.TextureParents.GetValueOrDefault(definition.TextureDictionary);
                if (parent is not null)
                    Widgets.PropertyText(L("Textures parentes"), $"{parent}.wtd");
            }
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Niveau de détail"), "gta4_lod"))
            DrawLevelOfDetail(entity, names, e => state.Gta4Selection = e);

        if (definition is not null && Widgets.Section(L("Définition (.ide)"), "gta4_definition", defaultOpen: false)
            && Widgets.BeginProperties("##gta4_definition"))
        {
            Widgets.PropertyText(L("Distance d'affichage"), Invariant($"{definition.DrawDistance:0} m"));
            Widgets.PropertyText(L("Drapeaux"), $"0x{definition.Flags:X8}");
            if (definition.IsTimed)
                Widgets.PropertyText(L("Heures"), Hours(definition.TimeFlags));
            if (definition.AnimationDictionary is { } animation)
                Widgets.PropertyText(L("Animations"), animation);
            var size = definition.BoxMax - definition.BoxMin;
            Widgets.PropertyText(L("Taille"), Invariant($"{size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} m"));
            Widgets.EndProperties();
        }
    }

    // Hours shown, one bit per hour: "20 h - 6 h" style ranges.
    private static string Hours(uint flags)
    {
        var ranges = new List<string>();
        for (int h = 0; h < 24; h++)
        {
            if ((flags & (1u << h)) == 0 || (h > 0 && (flags & (1u << (h - 1))) != 0))
                continue;
            int end = h;
            while (end + 1 < 24 && (flags & (1u << (end + 1))) != 0)
                end++;
            ranges.Add(end == h ? $"{h} h" : $"{h} h - {end + 1} h");
        }
        return ranges.Count == 0 ? "-" : string.Join(", ", ranges);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
