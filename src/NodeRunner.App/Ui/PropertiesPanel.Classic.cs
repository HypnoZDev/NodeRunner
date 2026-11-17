using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// The GTA III / Vice City / San Andreas object clicked in the 3D view: what the map files say about it, read only.
internal sealed partial class PropertiesPanel
{
    private void DrawClassic()
    {
        var classic = state.Classic;
        if (state.Gta4Selection is not { } entity || classic.Scene is not { } scene)
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.TextColored(UiTheme.TextMuted, L("Aucun objet sélectionné"));
            Widgets.Hint(L("Clic gauche sur un objet de la ville pour voir ses propriétés (lecture seule)."), wrap: true);
            return;
        }

        var names = scene.Names;
        string name = names.Describe(entity.ArchetypeHash);
        var definition = scene.Definitions.Find(name);
        var archetype = scene.Library.GetArchetype(entity.ArchetypeHash);

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(Widgets.Fit(name, ImGui.GetContentRegionAvail().X));
        ImGui.PopFont();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            ImGui.SetTooltip(name);
        Widgets.Hint(L($"{classic.GameName} : lecture seule"));
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

        if (Widgets.Section(L("Transformation"), "classic_transform") && Widgets.BeginProperties("##classic_transform"))
        {
            var p = entity.Position;
            var angles = EulerAngles.FromQuaternion(entity.Orientation);
            Widgets.PropertyText(L("Position"), Invariant($"{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}"), L("Mètres. X = est, Y = nord, Z = haut."));
            Widgets.PropertyText(L("Rotation (°)"), Invariant($"{angles.X:0.0}, {angles.Y:0.0}, {angles.Z:0.0}"), L("Degrés autour des axes X, Y et Z."));
            Widgets.PropertyText(L("Cap (°)"), Invariant($"{FiveMFormat.Heading(entity):0.0}"));
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Fichiers"), "classic_files") && Widgets.BeginProperties("##classic_files"))
        {
            FileRow(L("Placement"), entity.Map?.FilePath);
            FileRow(L("Modèle"), archetype is null ? null : scene.Models.Locate(archetype)?.Path);
            if (definition is not null)
            {
                string? textures = scene.Textures.Locate(archetype?.TextureDictionaryHash ?? 0);
                Widgets.PropertyText(L("Textures"), $"{definition.TextureDictionary}.txd", textures ?? L("fichier introuvable"));
                if (scene.Definitions.TextureParents.GetValueOrDefault(definition.TextureDictionary) is { } parent)
                    Widgets.PropertyText(L("Textures parentes"), $"{parent}.txd");
            }
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Niveau de détail"), "classic_lod"))
            DrawLevelOfDetail(entity, names, e => state.Gta4Selection = e);

        if (definition is not null && Widgets.Section(L("Définition (.ide)"), "classic_definition", defaultOpen: false)
            && Widgets.BeginProperties("##classic_definition"))
        {
            Widgets.PropertyText(L("Numéro"), definition.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Widgets.PropertyText(L("Distance d'affichage"), Invariant($"{definition.DrawDistance:0} m"));
            Widgets.PropertyText(L("Drapeaux"), $"0x{definition.Flags:X8}");
            if (definition.IsTimed)
                Widgets.PropertyText(L("Heures"), $"{definition.TimeOn} h - {definition.TimeOff} h");
            if (definition.AnimationFile is { } animation)
                Widgets.PropertyText(L("Animations"), animation);
            if (archetype is not null)
            {
                var size = archetype.BoundsMax - archetype.BoundsMin;
                Widgets.PropertyText(L("Taille"), Invariant($"{size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} m"));
            }
            Widgets.EndProperties();
        }
    }
}
