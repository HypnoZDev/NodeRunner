using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private static float CarHeading(CarGenerator spot) => ((spot.Angle * 180f / MathF.PI - 90f) % 360f + 360f) % 360f;

    private void DrawCarGenerator(CarGeneratorSelection selection, CarGenerator spot, HashNames names)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Car}  Place de véhicule {selection.Index}"));
        ImGui.PopFont();
        string file = Path.GetFileName(selection.Map.FilePath ?? "");
        Widgets.Hint($"{Icons.File}  {file}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort) && selection.Map.FilePath is { } path)
            ImGui.SetTooltip(path);
        float heading = CarHeading(spot);
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la place")))
            state.FocusOn(spot.Position, MathF.Max(spot.Length, 4f));
        ImGui.SameLine();
        string copied = state.FormatPosition(spot.Position, heading);
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedCarGenerator();
            return;
        }

        var map = selection.Map;
        int index = selection.Index;
        CarGenerator Current() => map.CarGenerators[index];
        void Live(CarGenerator after) => map.SetCarGenerator(index, after);
        void Commit(CarGenerator after, string description)
        {
            var before = Current();
            if (after == before)
                return;
            Live(after);
            state.History.Add(new CarGeneratorEdit(map, index, before, after, description));
        }
        // Live change while a field is dragged; one undo step when it is released.
        void Track(CarGenerator before, string description)
        {
            if (ImGui.IsItemActivated())
                _spotEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _spotEditStart is { } start)
            {
                if (Current() != start)
                    state.History.Add(new CarGeneratorEdit(map, index, start, Current(), description));
                _spotEditStart = null;
            }
        }
        string Change(string what) => L($"Modifier la place de véhicule {index} ({what})");
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        if (Widgets.Section(L("Place"), "cargen_place") && Widgets.BeginProperties("##cargen_place"))
        {
            Widgets.Property(L("Position"), L("Centre de la place, en mètres. X = est, Y = nord, Z = haut. Aussi avec les poignées (touche 2)."));
            float width = Widgets.SplitWidth(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = Current();
                var position = current.Position;
                float value = position[i];
                if (Widgets.AxisField($"##cargen_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    position[i] = value;
                    Live(current with { Position = position });
                }
                Track(current, Change(L("position")));
            }

            Widgets.Property(L("Cap"), L("Sens de l'avant du véhicule, en degrés depuis le nord (comme le cap d'un vector4 FiveM). Aussi avec les poignées (touche 3)."));
            {
                var current = Current();
                float value = CarHeading(current);
                if (Widgets.AxisField("##cargen_heading", ref value, UiTheme.AxisZ, ImGui.GetContentRegionAvail().X, 0.5f, "%.1f°"))
                {
                    float angle = (value + 90f) * MathF.PI / 180f;
                    Live(current with { Orientation = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * current.Length });
                }
                Track(current, Change(L("cap")));
            }

            Widgets.Property(L("Longueur"), L("Mètres, le long du véhicule. Aussi avec les poignées (touche 4)."));
            {
                var current = Current();
                float value = current.Length;
                if (Widgets.AxisField("##cargen_length", ref value, UiTheme.AxisX, ImGui.GetContentRegionAvail().X, 0.02f, "%.2f m", MinSpotSide, 100f))
                {
                    var forward = current.Length > 1e-4f ? current.Orientation / current.Length : Vector2.UnitX;
                    Live(current with { Orientation = forward * MathF.Max(MinSpotSide, value) });
                }
                Track(current, Change(L("longueur")));
            }

            Widgets.Property(L("Largeur"), L("Mètres, en travers du véhicule. Aussi avec les poignées (touche 4)."));
            {
                var current = Current();
                float value = current.Width;
                if (Widgets.AxisField("##cargen_width", ref value, UiTheme.AxisY, ImGui.GetContentRegionAvail().X, 0.02f, "%.2f m", MinSpotSide, 100f))
                    Live(current with { Width = MathF.Max(MinSpotSide, value) });
                Track(current, Change(L("largeur")));
            }
            Widgets.EndProperties();
        }

        if (Widgets.Section(L("Véhicule"), "cargen_vehicle") && Widgets.BeginProperties("##cargen_vehicle"))
        {
            var preview = state.SpotPreview;
            string shown = preview.ModelName is not { } shownName ? L("aucun")
                : preview.Error is { } previewError ? L($"{shownName} (illisible : {previewError})")
                : preview.IsLoading ? L($"{shownName} (chargement...)")
                : $"{shownName} · {preview.Source}";
            Widgets.PropertyText(L("Aperçu"), shown, L("Véhicule du jeu affiché sur la place dans la vue 3D, pour juger la taille et le sens. Le jeu peut en choisir un autre."));

            Widgets.Property(L("Modèle"), L("Nom du modèle garé à cette place (ex. adder) ou son hash (0x...). Vide = au hasard : un modèle du groupe, sinon un véhicule de la zone."));
            if (CommitText("##cargen_model", HashText(spot.CarModel, names), L("au hasard"), out string model) && ParseHashText(model, names) is { } modelHash)
                Commit(Current() with { CarModel = modelHash }, Change(L("modèle")));

            Widgets.Property(L("Groupe"), L("Groupe de population (popgroups) dans lequel le jeu choisit le modèle, ou son hash (0x...). Vide = aucun."));
            if (CommitText("##cargen_group", HashText(spot.PopGroup, names), L("aucun"), out string group) && ParseHashText(group, names) is { } groupHash)
                Commit(Current() with { PopGroup = groupHash }, Change(L("groupe")));

            Widgets.Property(L("Couleurs"), L("Numéros des couleurs de carrosserie (body color remap) 1 à 4 ; -1 = au hasard."));
            {
                float width = Widgets.SplitWidth(4);
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = Current();
                    int value = i switch { 0 => current.Color1, 1 => current.Color2, 2 => current.Color3, _ => current.Color4 };
                    ImGui.SetNextItemWidth(width);
                    if (ImGui.DragInt($"##cargen_color{i}", ref value, 0.2f, -1, 255))
                    {
                        value = Math.Clamp(value, -1, 255);
                        Live(i switch
                        {
                            0 => current with { Color1 = value },
                            1 => current with { Color2 = value },
                            2 => current with { Color3 = value },
                            _ => current with { Color4 = value },
                        });
                    }
                    Track(current, Change(L("couleurs")));
                }
            }

            Widgets.Property(L("Livrée"), L("Numéro de la livrée (motifs, logos) ; -1 = au hasard."));
            {
                var current = Current();
                int value = current.Livery;
                if (ImGui.DragInt("##cargen_livery", ref value, 0.1f, -1, 127))
                    Live(current with { Livery = Math.Clamp(value, -1, 127) });
                Track(current, Change(L("livrée")));
            }

            if (!Simple)
                Widgets.Property(L("Drapeaux"), L("Réglages de la place (heures d'apparition, priorité...), en hexadécimal. Le sens de chaque bit n'est pas encore connu."));
            if (!Simple && CommitText("##cargen_flags", $"0x{spot.Flags:X}", "0x0", out string flagsText))
            {
                string hex = flagsText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? flagsText[2..] : flagsText;
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint flags))
                    Commit(Current() with { Flags = flags }, Change(L("drapeaux")));
                else
                    state.Notify(L("Drapeaux invalides : un nombre hexadécimal est attendu (ex. 0xE60)."), warning: true);
            }
            Widgets.EndProperties();
        }

        if (!Simple && Widgets.Section(L("Fichier"), "cargen_file") &&Widgets.BeginProperties("##cargen_file"))
        {
            Widgets.PropertyText(L("YMAP"), file, selection.Map.FilePath);
            Widgets.PropertyText(L("Dans ce YMAP"), L($"{selection.Map.KeptCarGenerators.Count()} place(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu peut garer un véhicule à cette place quand le joueur passe à proximité. Échap pour désélectionner ; ")
            + L("masquez le calque Véhicules garés pour sélectionner les objets."), wrap: true);
    }
}
