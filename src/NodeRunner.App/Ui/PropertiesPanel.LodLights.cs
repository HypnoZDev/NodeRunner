using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

internal sealed partial class PropertiesPanel
{
    // Hours when a light is on, from its mask (bit n = on during hour n), as ranges: "de 20 h à 6 h".
    internal static string LightHours(uint mask)
    {
        mask &= 0xFFFFFF;
        if (mask == 0xFFFFFF)
            return L("toute la journée");
        if (mask == 0)
            return L("jamais");
        bool On(int hour) => (mask >> ((hour % 24 + 24) % 24) & 1) != 0;
        var ranges = new List<string>();
        for (int h = 0; h < 24; h++)
        {
            if (!On(h) || On(h - 1))
                continue;
            int end = h;
            while (On(end + 1) && end + 1 < h + 24)
                end++;
            ranges.Add(L($"de {h} h à {(end + 1) % 24} h"));
        }
        return string.Join(", ", ranges);
    }

    private void DrawLodLight(LodLightSelection selection, DistantLodLight light)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Moon}  Lumière LOD {selection.Index}"));
        ImGui.PopFont();
        string distantFile = Path.GetFileName(selection.Distant.FilePath ?? "");
        Widgets.Hint($"{Icons.File}  {distantFile}");
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller à la lumière")))
            state.FocusOn(light.Position, 6f);
        ImGui.SameLine();
        string copied = state.FormatPosition(light.Position, null);
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Trash}  Supprimer"), L("Suppr. Retire la lumière et ses détails de leurs deux fichiers. Annulable avec Ctrl+Z.")))
        {
            state.DeleteSelectedLodLight();
            return;
        }

        var details = selection.Details;
        if (Widgets.Section(L("Lumière"), "lodlight_light") && Widgets.BeginProperties("##lodlight_light"))
        {
            var distant = selection.Distant;
            var lod = selection.Lod;
            int index = selection.Index;
            LodLightValues Current() => LodLightValues.Of(distant, lod, index);
            void Live(LodLightValues after) => after.ApplyTo(distant, lod, index);
            void Commit(LodLightValues after, string description)
            {
                var before = Current();
                if (after == before)
                    return;
                Live(after);
                state.History.Add(new LodLightEdit(distant, lod, index, before, after, description));
            }
            // Live change while a field is dragged; one undo step when it is released.
            void Track(LodLightValues before, string description)
            {
                if (ImGui.IsItemActivated())
                    _lightEditStart = (selection, before);
                if (ImGui.IsItemDeactivatedAfterEdit() && _lightEditStart is { } edit)
                {
                    CommitLightEdit(edit, description);
                    _lightEditStart = null;
                }
            }
            string Change(string what) => L($"Modifier la lumière LOD {index} ({what})");
            static DistantLodLight WithColor(DistantLodLight l, int r, int g, int b, int i) =>
                l with { Rgbi = DistantLodLight.Pack((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(i, 0, 255)) };
            Vector4[] axes = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

            if (details is { } type)
            {
                if (Simple)
                    Widgets.PropertyText(L("Type"), LightTypeName(type.Type));
                else
                {
                    Widgets.Property(L("Type"), L("Forme de la lumière : ponctuelle (tout autour), spot (un cône dans une direction) ou tube."));
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.BeginCombo("##lodlight_type", LightTypeName(type.Type)))
                    {
                        foreach (var t in (LodLightType[])[LodLightType.Point, LodLightType.Spot, LodLightType.Capsule])
                        {
                            if (ImGui.Selectable(LightTypeName(t), t == type.Type) && t != type.Type && Current().Details is { } now)
                                Commit(Current() with { Details = now.WithType(t) }, Change(L("type")));
                        }
                        ImGui.EndCombo();
                    }
                }
            }

            Widgets.Property(L("Couleur"), L("Couleur de la lumière (rouge, vert, bleu de 0 à 255). Cliquez le carré pour choisir dans une palette."));
            {
                var color = new Vector4(light.Red / 255f, light.Green / 255f, light.Blue / 255f, 1f);
                if (ImGui.ColorButton("##lodlight_color", color, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha,
                        new Vector2(ImGui.GetFrameHeight() * 1.6f, ImGui.GetFrameHeight())))
                {
                    _lightPickerStart = (selection, Current());
                    ImGui.OpenPopup("##lodlight_picker");
                }
                if (ImGui.BeginPopup("##lodlight_picker"))
                {
                    var rgb = new Vector3(color.X, color.Y, color.Z);
                    if (ImGui.ColorPicker3("##lodlight_picker_rgb", ref rgb, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoSidePreview))
                    {
                        var current = Current();
                        Live(current with
                        {
                            Light = WithColor(current.Light, (int)MathF.Round(rgb.X * 255), (int)MathF.Round(rgb.Y * 255), (int)MathF.Round(rgb.Z * 255), current.Light.Intensity),
                        });
                    }
                    ImGui.EndPopup();
                }
                else if (_lightPickerStart is { } picked)
                {
                    // The palette closed: one undo step for the whole choice.
                    CommitLightEdit(picked, L($"Modifier la lumière LOD {picked.Selection.Index} (couleur)"));
                    _lightPickerStart = null;
                }
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
                float width = (ImGui.GetContentRegionAvail().X - spacing * 2) / 3;
                string[] channels = ["R %d", "G %d", "B %d"];
                for (int c = 0; c < 3; c++)
                {
                    if (c > 0) ImGui.SameLine(0, spacing);
                    var current = Current();
                    var l = current.Light;
                    int value = c == 0 ? l.Red : c == 1 ? l.Green : l.Blue;
                    ImGui.SetNextItemWidth(width);
                    if (ImGui.DragInt($"##lodlight_rgb{c}", ref value, 0.5f, 0, 255, channels[c]))
                        Live(current with { Light = WithColor(l, c == 0 ? value : l.Red, c == 1 ? value : l.Green, c == 2 ? value : l.Blue, l.Intensity) });
                    Track(current, Change(L("couleur")));
                }
            }

            Widgets.Property(L("Intensité"), L("Force de la lumière vue de loin, de 0 à 255."));
            {
                var current = Current();
                int value = current.Light.Intensity;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.DragInt("##lodlight_intensity", ref value, 0.5f, 0, 255, "%d / 255"))
                    Live(current with { Light = WithColor(current.Light, current.Light.Red, current.Light.Green, current.Light.Blue, value) });
                Track(current, Change(L("intensité")));
            }

            Widgets.Property(L("Position"), L("Position de la lumière, en mètres. Aussi avec les poignées (touche 2)."));
            {
                float width = Widgets.SplitWidth(3);
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    var current = Current();
                    var position = current.Light.Position;
                    float value = position[i];
                    if (Widgets.AxisField($"##lodlight_position{i}", ref value, axes[i], width, 0.05f, "%.2f"))
                    {
                        position[i] = value;
                        Live(current with { Light = current.Light with { Position = position } });
                    }
                    Track(current, Change(L("position")));
                }
            }

            if (details is { } d)
            {
                static LodLightValues WithDetails(LodLightValues v, Func<LodLight, LodLight> change) =>
                    v.Details is { } now ? v with { Details = change(now) } : v;

                Widgets.Property(L("Allumée"), L("Heures où le jeu allume la lumière : de la première à la seconde (0 à 23) ; les mêmes = toute la journée. ")
                    + L($"Actuellement : {LightHours(d.HourMask)}."));
                {
                    var (first, last) = FirstHourRange(d.HourMask);
                    float half = Widgets.SplitWidth(2);
                    for (int i = 0; i < 2; i++)
                    {
                        if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                        var current = Current();
                        int value = i == 0 ? first : last;
                        ImGui.SetNextItemWidth(half);
                        if (ImGui.DragInt($"##lodlight_hour{i}", ref value, 0.1f, 0, 23, i == 0 ? L("de %d h") : L("à %d h")))
                        {
                            value = Math.Clamp(value, 0, 23);
                            uint mask = LodLight.HourRange(i == 0 ? value : first, i == 0 ? last : value);
                            Live(WithDetails(current, x => x.WithHourMask(mask)));
                        }
                        Track(current, Change(L("heures")));
                    }
                }

                Widgets.Property(L("Portée"), L("Distance éclairée autour de la lumière, en mètres (cercle dans la vue 3D)."));
                {
                    var current = Current();
                    float value = d.Falloff;
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.DragFloat("##lodlight_falloff", ref value, 0.05f, 0.01f, 500f, "%.2f m"))
                        Live(WithDetails(current, x => x with { Falloff = Math.Clamp(value, 0.01f, 500f) }));
                    Track(current, Change(L("portée")));
                }

                if (!Simple)
                {
                    Widgets.Property(L("Exposant de la portée"), L("Vitesse à laquelle la lumière baisse avec la distance."));
                    {
                        var current = Current();
                        float value = d.FalloffExponent;
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.DragFloat("##lodlight_exponent", ref value, 0.05f, 0f, 256f, "%.2f"))
                            Live(WithDetails(current, x => x with { FalloffExponent = Math.Clamp(value, 0f, 256f) }));
                        Track(current, Change(L("exposant")));
                    }

                    Widgets.Property(L("Direction"), L("Sens dans lequel éclaire un spot (ligne dans la vue 3D), en X, Y, Z."));
                    {
                        float width = Widgets.SplitWidth(3);
                        for (int i = 0; i < 3; i++)
                        {
                            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                            var current = Current();
                            var direction = current.Details?.Direction ?? d.Direction;
                            float value = direction[i];
                            if (Widgets.AxisField($"##lodlight_direction{i}", ref value, axes[i], width, 0.01f, "%.3f"))
                            {
                                direction[i] = value;
                                Live(WithDetails(current, x => x with { Direction = direction }));
                            }
                            Track(current, Change(L("direction")));
                        }
                    }

                    Widgets.Property(L("Cône"), L("Ouverture d'un spot (intérieur, extérieur), ou longueur d'un tube : valeurs brutes du fichier, de 0 à 255."));
                    {
                        float half = Widgets.SplitWidth(2);
                        for (int i = 0; i < 2; i++)
                        {
                            if (i > 0) ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                            var current = Current();
                            var now = current.Details ?? d;
                            int value = i == 0 ? now.ConeInnerAngle : now.ConeOuterAngleOrCapExt;
                            ImGui.SetNextItemWidth(half);
                            if (ImGui.DragInt($"##lodlight_cone{i}", ref value, 0.3f, 0, 255, i == 0 ? L("intérieur %d") : L("extérieur %d")))
                            {
                                byte b = (byte)Math.Clamp(value, 0, 255);
                                Live(WithDetails(current, x => i == 0 ? x with { ConeInnerAngle = b } : x with { ConeOuterAngleOrCapExt = b }));
                            }
                            Track(current, Change(L("cône")));
                        }
                    }

                    Widgets.Property(L("Halo"), L("Force du halo (corona) autour de la lumière, de 0 à 255."));
                    {
                        var current = Current();
                        int value = d.CoronaIntensity;
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.DragInt("##lodlight_corona", ref value, 0.5f, 0, 255, "%d / 255"))
                            Live(WithDetails(current, x => x with { CoronaIntensity = (byte)Math.Clamp(value, 0, 255) }));
                        Track(current, Change(L("halo")));
                    }

                    Widgets.PropertyText(L("Hash"), $"0x{d.Hash:X8}", L("Identifiant de la lumière d'origine sur son objet."));
                    Widgets.PropertyText(L("Drapeaux"), $"0x{d.TimeAndStateFlags:X8}", L("Heures (bits 0 à 23), réglages (24, 25), type (26 à 28)."));
                }
            }
            else
                Widgets.PropertyText(L("Détails"), L("aucun"), L("Ce fichier de lumières lointaines n'a pas de fichier lodlights pour cette lumière."));
            Widgets.EndProperties();
        }
        if (!Simple && Widgets.Section(L("Fichiers"), "lodlight_files") && Widgets.BeginProperties("##lodlight_files"))
        {
            Widgets.PropertyText(L("Lumières lointaines"), distantFile, selection.Distant.FilePath);
            Widgets.PropertyText(L("Détails"), Path.GetFileName(selection.Lod?.FilePath ?? "") is { Length: > 0 } lodFile ? lodFile : L("aucun"), selection.Lod?.FilePath);
            Widgets.PropertyText(L("Catégorie"), selection.Distant.DistantCategory switch { 0 => L("petite"), 1 => L("moyenne"), 2 => L("grande"), var c => c.ToString() },
                L("Taille des lumières du fichier (small, medium, large dans son nom)."));
            Widgets.PropertyText(L("Lampadaires"), selection.Index < selection.Distant.DistantStreetLights ? L("oui") : L("non"),
                L("Les premières lumières de chaque fichier sont des lampadaires de rue."));
            Widgets.PropertyText(L("Dans ce fichier"), L($"{selection.Distant.DistantLights.Count} lumière(s)"));
            Widgets.EndProperties();
        }
        Widgets.Hint(L("Le jeu remplace par ces lumières les vraies lumières des objets quand on les voit de loin, la nuit. Échap pour désélectionner ; ")
            + L("masquez le calque Lumières LOD pour sélectionner les objets."), wrap: true);
    }

    // A light whose field (or colour palette) is being changed, and its values before.
    private (LodLightSelection Selection, LodLightValues Start)? _lightEditStart, _lightPickerStart;

    // One undo step from the values before to the light's current ones (none if unchanged).
    private void CommitLightEdit((LodLightSelection Selection, LodLightValues Start) edit, string description)
    {
        var (s, before) = edit;
        if (s.Light is null)
            return;
        var now = LodLightValues.Of(s.Distant, s.Lod, s.Index);
        if (now != before)
            state.History.Add(new LodLightEdit(s.Distant, s.Lod, s.Index, before, now, description));
    }

    private static string LightTypeName(LodLightType type) => type switch
    {
        LodLightType.Point => L("Ponctuelle (dans toutes les directions)"),
        LodLightType.Spot => L("Spot (dans une direction)"),
        LodLightType.Capsule => L("Tube"),
        var other => L($"Inconnu ({(int)other})"),
    };

    // The first hours a light is on, as start and end (end excluded): (0, 0) for the whole day or never.
    internal static (int Start, int End) FirstHourRange(uint mask)
    {
        mask &= 0xFFFFFF;
        if (mask is 0 or 0xFFFFFF)
            return (0, 0);
        bool On(int hour) => (mask >> ((hour % 24 + 24) % 24) & 1) != 0;
        for (int h = 0; h < 24; h++)
        {
            if (!On(h) || On(h - 1))
                continue;
            int end = h;
            while (On(end + 1))
                end++;
            return (h, (end + 1) % 24);
        }
        return (0, 0);
    }

    private TimecycleModifier? _zoneEditStart;
    private const float MinZoneSide = 0.5f;
}
