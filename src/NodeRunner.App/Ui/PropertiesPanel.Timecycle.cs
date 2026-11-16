using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Hashing;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    private void DrawTimecycle(TimecycleSelection selection, TimecycleModifier zone, HashNames names)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Sunset}  Zone de timecycle {selection.Index}"));
        ImGui.PopFont();
        string file = Path.GetFileName(selection.Map.FilePath ?? "");
        Widgets.Hint($"{Icons.File}  {file}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort) && selection.Map.FilePath is { } path)
            ImGui.SetTooltip(path);
        var size = zone.Size;
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la zone")))
            state.FocusOn(zone.Center, MathF.Max(MathF.Max(size.X, size.Y), MathF.Max(size.Z, 4f)));
        ImGui.SameLine();
        string copied = state.FormatPosition(zone.Center, null);
        if (Widgets.Button(L($"{Icons.Copy}  Copier le centre"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedTimecycleZone();
            return;
        }

        static string V(Vector3 v) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}");
        var map = selection.Map;
        int index = selection.Index;
        TimecycleModifier Current() => map.TimecycleModifiers[index];
        void Live(TimecycleModifier after) => map.SetTimecycleModifier(index, after);
        void Commit(TimecycleModifier after, string description)
        {
            var before = Current();
            if (after == before)
                return;
            Live(after);
            state.History.Add(new TimecycleEdit(map, index, before, after, description));
        }
        // Live change while a field is dragged; one undo step when it is released.
        void Track(TimecycleModifier before, string description)
        {
            if (ImGui.IsItemActivated())
                _zoneEditStart = before;
            if (ImGui.IsItemDeactivatedAfterEdit() && _zoneEditStart is { } start)
            {
                if (Current() != start)
                    state.History.Add(new TimecycleEdit(map, index, start, Current(), description));
                _zoneEditStart = null;
            }
        }
        string Change(string what) => L($"Modifier la zone de timecycle {index} ({what})");
        Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

        if (Widgets.Section(L("Zone"), "timecycle_zone") && Widgets.BeginProperties("##timecycle_zone"))
        {
            Widgets.Property(L("Modificateur"), L("Réglage de lumière, de couleurs ou de brouillard appliqué dans la zone (timecycle_mods_*.xml du jeu). ")
                + L("Tapez son nom (ex. NoAmbientmult) ou son hash (0x...)."));
            if (CommitText("##timecycle_name", TimecycleText(zone.NameHash, names), L("aucun"), out string modifier)
                && ParseTimecycle(modifier, names) is { } modifierHash)
                Commit(Current() with { NameHash = modifierHash }, Change(L("modificateur")));

            Widgets.Property(L("Centre"), L("Centre de la boîte, en mètres. Aussi avec les poignées (touche 2)."));
            float width = Widgets.SplitWidth(3);
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = Current();
                var center = current.Center;
                float value = center[i];
                if (Widgets.AxisField($"##timecycle_center{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                {
                    var delta = Vector3.Zero;
                    delta[i] = value - center[i];
                    Live(current with { BoundsMin = current.BoundsMin + delta, BoundsMax = current.BoundsMax + delta });
                }
                Track(current, Change(L("position")));
            }

            Widgets.Property(L("Taille"), L("Taille de la boîte en X, Y, Z (mètres), autour du centre. Aussi avec les poignées (touche 4)."));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                var current = Current();
                var sides = current.Size;
                float value = sides[i];
                if (Widgets.AxisField($"##timecycle_size{i}", ref value, axes[i], width, 0.05f, "%.2f", MinZoneSide, 100000f))
                {
                    var center = current.Center;
                    sides[i] = MathF.Max(MinZoneSide, value);
                    Live(current with { BoundsMin = center - sides / 2, BoundsMax = center + sides / 2 });
                }
                Track(current, Change(L("taille")));
            }

            Widgets.Property(L("Intensité"), L("Force du réglage dans la boîte, de 0 à 100 %."));
            {
                var current = Current();
                float value = current.Percentage;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.DragFloat("##timecycle_percentage", ref value, 0.5f, 0f, 100f, "%.0f %%"))
                    Live(current with { Percentage = Math.Clamp(value, 0f, 100f) });
                Track(current, Change(L("intensité")));
            }

            Widgets.Property(L("Fondu"), L("Distance autour de la boîte sur laquelle le réglage apparaît peu à peu (cadre jaune pâle dans la vue 3D)."));
            {
                var current = Current();
                float value = current.Range;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.DragFloat("##timecycle_range", ref value, 0.1f, 0f, 1000f, "%.1f m"))
                    Live(current with { Range = Math.Clamp(value, 0f, 1000f) });
                Track(current, Change(L("fondu")));
            }

            Widgets.Property(L("Heures"), L("Heures du jour où la zone s'applique : de la première à la seconde (0 à 23)."));
            {
                float half = Widgets.SplitWidth(2);
                for (int i = 0; i < 2; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = Current();
                    int value = (int)(i == 0 ? current.StartHour : current.EndHour);
                    ImGui.SetNextItemWidth(half);
                    if (ImGui.DragInt($"##timecycle_hour{i}", ref value, 0.1f, 0, 23, i == 0 ? L("de %d h") : L("à %d h")))
                    {
                        uint hour = (uint)Math.Clamp(value, 0, 23);
                        Live(i == 0 ? current with { StartHour = hour } : current with { EndHour = hour });
                    }
                    Track(current, Change(L("heures")));
                }
            }
            if (!Simple)
            {
                Widgets.PropertyText(L("Coin min"), V(zone.BoundsMin), L("Coin bas, sud-ouest de la boîte, en mètres."));
                Widgets.PropertyText(L("Coin max"), V(zone.BoundsMax), L("Coin haut, nord-est de la boîte, en mètres."));
            }
            Widgets.EndProperties();
        }
        if (!Simple && Widgets.Section(L("Fichier"), "timecycle_file") && Widgets.BeginProperties("##timecycle_file"))
        {
            Widgets.PropertyText(L("YMAP"), file, selection.Map.FilePath);
            Widgets.PropertyText(L("Dans ce YMAP"), L($"{selection.Map.TimecycleModifiers.Count(z => !z.IsDeleted)} zone(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu change l'ambiance de l'image quand la caméra est dans cette zone. Enregistré dans son YMAP au Ctrl+S. Échap pour désélectionner ; ")
            + L("masquez le calque Zones de timecycle pour sélectionner les objets."), wrap: true);
    }

    private static string TimecycleText(uint hash, HashNames names) =>
        hash == 0 ? "" : names.TryGet(hash) ?? $"0x{hash:X8}";

    // A typed timecycle name (or hash, 0x...): its hash, 0 for none; null when the text is not valid.
    private uint? ParseTimecycle(string text, HashNames names)
    {
        text = text.Trim().Trim('"', '\'');
        if (text.Length == 0)
            return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex))
            return hex;
        if (text.Any(char.IsWhiteSpace))
        {
            state.Notify(L("Nom de timecycle invalide (espaces)."), warning: true);
            return null;
        }
        names.Add(text);
        return Joaat.Hash(text);
    }
}
