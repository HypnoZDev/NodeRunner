using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    // Centre, size and rotation of a box occluder, changed live; one undo step when a field is released, the box then
    // rounded as the game's file stores it.
    private void DrawOccluderBoxFields(OccluderSelection occluder)
    {
        var file = occluder.File;
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
        float width = Widgets.SplitWidth(3);

        void Track(OccluderBox before)
        {
            if (ImGui.IsItemActivated())
                _boxEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _boxEditStart is { } start)
            {
                var end = file.Boxes[occluder.Index].Quantized();
                OccluderMaps.SetBox(file, end);
                if (end != start)
                    state.History.Add(new OccluderBoxEdit(file, start, end, L($"Modifier la boîte d'occlusion {occluder.Index}")));
                _boxEditStart = null;
            }
        }

        Widgets.Property(L("Centre"), L("Mètres. X = est, Y = nord, Z = haut. Arrondi à 25 cm (précision du format du jeu). Aussi avec les poignées (touche 2)."));
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            var box = file.Boxes[occluder.Index];
            var center = box.Center;
            float value = center[i];
            if (Widgets.AxisField($"##occl_center{i}", ref value, axes[i], width, 0.05f, "%.2f"))
            {
                center[i] = value;
                OccluderMaps.SetBox(file, box with { Center = center });
            }
            Track(box);
        }

        Widgets.Property(L("Taille"), L("Longueur (X), largeur (Y) et hauteur (Z) de la boîte, en mètres, dans son propre sens. Aussi avec les poignées (touche 4)."));
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            var box = file.Boxes[occluder.Index];
            var size = box.Size;
            float value = size[i];
            if (Widgets.AxisField($"##occl_size{i}", ref value, axes[i], width, 0.05f, "%.2f", OccluderBox.PositionUnit, 8000f))
            {
                size[i] = MathF.Max(OccluderBox.PositionUnit, value);
                OccluderMaps.SetBox(file, box with { Size = size });
            }
            Track(box);
        }

        Widgets.Property(L("Rotation Z"), L("Degrés. Une boîte d'occlusion ne tourne qu'autour de l'axe vertical. Aussi avec les poignées (touche 3)."));
        {
            var box = file.Boxes[occluder.Index];
            float degrees = (box.Angle * 180f / MathF.PI + 360f) % 360f;
            if (Widgets.AxisField("##occl_angle", ref degrees, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                OccluderMaps.SetBox(file, box with { Angle = degrees * MathF.PI / 180f });
            Track(box);
        }

        if (file.IsBoxChanged(occluder.Index))
            Widgets.PropertyText(L("État"), L("modifiée (pas encore enregistrée)"), L("L'enregistrement des boîtes d'occlusion arrive à l'étape suivante."), UiTheme.Warning);
    }

    private void DrawOccluder(OccluderSelection occluder)
    {
        var (min, max) = occluder.Bounds();
        var size = max - min;
        var center = occluder.Center;
        bool isBox = occluder.Box is not null;
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted($"{Icons.ScanEye}  {(isBox ? "Boîte d'occlusion" : "Maillage d'occlusion")} {occluder.Index}");
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  {occluder.File.FileName}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(occluder.File.GamePath);
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à l'occluder")))
            state.FocusOn(center, MathF.Max(size.Length() / 2, 5f));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), state.FormatPosition(center)))
            ImGui.SetClipboardText(state.FormatPosition(center));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedOccluder();
            return;
        }

        if (Widgets.Section(isBox ? L("Boîte") : L("Maillage"), "occluder") && Widgets.BeginProperties("##occluder"))
        {
            if (occluder.Box is not null)
                DrawOccluderBoxFields(occluder);
            else if (occluder.Model is { } model)
            {
                Widgets.PropertyText(L("Triangles"), Widgets.Count(model.TriangleCount));
                Widgets.PropertyText(L("Sommets"), Widgets.Count(model.Vertices.Count), L("255 au plus par maillage."));
                Widgets.PropertyText(L("Emprise"), $"{size.X:0} x {size.Y:0} x {size.Z:0} m");
                Widgets.PropertyText(L("Centre"), $"{center.X:0.#}, {center.Y:0.#}, {center.Z:0.#}");
            }
            Widgets.PropertyText(L("Fichier"), occluder.File.FileName, occluder.File.GamePath);
            var occluders = occluder.File.Occluders;
            Widgets.PropertyText(L("Dans ce fichier"), L($"{occluders.Boxes.Count} boîte(s), {occluders.Models.Count} maillage(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu ne dessine pas ce qui est caché derrière un occluder. Échap pour désélectionner ; ")
            + L("masquez le calque Occlusion pour sélectionner les bâtiments."), wrap: true);
    }

    private CarGenerator? _spotEditStart;
    private const float MinSpotSide = 0.5f;
}
