using System.Globalization;
using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Hashing;

namespace NodeRunner.App.Ui;

// An ambient zone or a static emitter of the game's audio data, read-only.
internal sealed partial class PropertiesPanel
{
    private void DrawAudioPlacement(AudioSelection selection, HashNames names)
    {
        string Name(uint hash) => hash == 0 ? L("aucun")
            : state.World.AudioGameData is { } data ? AudioName(hash, data, names) : names.Describe(hash);
        static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}");

        string name = Name(selection.NameHash);
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(selection.Zone is not null ? L($"{Icons.AudioLines}  Zone d'ambiance {name}") : L($"{Icons.Volume}  Source sonore {name}"));
        ImGui.PopFont();
        Widgets.Hint($"{Icons.File}  {Path.GetFileName(selection.File)}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(selection.File);

        var (centre, size) = AudioZoneLayer.Extent(selection);
        if (Widgets.Button(L($"{Icons.Crosshair}  Aller")))
            state.FocusOn(centre, size);
        ImGui.SameLine();
        string copied = state.FormatPosition(centre, null);
        if (Widgets.Button(L($"{Icons.Copy}  Copier la position"), copied))
            ImGui.SetClipboardText(copied);
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom"), name))
            ImGui.SetClipboardText(name);
        Widgets.Hint(L("Données audio du jeu, en lecture seule."), wrap: true);

        if (selection.Zone is { } zone)
            DrawAmbientZone(zone, Name, V);
        else if (selection.Emitter is { } emitter)
            DrawStaticEmitter(emitter, Name, V);
    }

    private void DrawAmbientZone(AmbientZone zone, Func<uint, string> name, Func<Vector3, string> v)
    {
        if (Widgets.Section(L("Zone"), "audio_zone_shape") && Widgets.BeginProperties("##audio_zone_shape"))
        {
            var p = zone.Positioning;
            string shape = zone.Shape switch
            {
                AudioZoneShape.Sphere => L("Sphère"),
                AudioZoneShape.Line => L("Ligne"),
                _ => L("Boîte"),
            };
            Widgets.PropertyText(L("Forme"), shape);
            if (zone.Shape == AudioZoneShape.Line)
            {
                Widgets.PropertyText(L("Début"), v(p.Centre));
                Widgets.PropertyText(L("Fin"), v(p.Size));
            }
            else
            {
                Widgets.PropertyText(L("Centre"), v(p.Centre), L("Là où le jeu place les sons de la zone (zone jaune)."));
                Widgets.PropertyText(zone.Shape == AudioZoneShape.Sphere ? L("Rayon") : L("Taille"),
                    zone.Shape == AudioZoneShape.Sphere ? $"{p.Size.X:0.##} m" : v(p.Size));
                if (zone.Shape == AudioZoneShape.Box)
                    Widgets.PropertyText(L("Rotation"), $"{p.RotationDegrees:0}°");
            }
            Widgets.PropertyText(L("Zone d'activation"), $"{v(zone.Activation.Size)} ; {zone.Activation.RotationDegrees:0}°",
                L("Boîte plus large (contour pâle) où la zone commence à jouer quand le joueur y entre."));
            Widgets.PropertyText(L("Drapeaux"), $"0x{zone.Flags:X8}");
            Widgets.EndProperties();
        }

        if (Widgets.Section(L($"Règles d'ambiance ({zone.Rules.Count})"), "audio_zone_rules"))
        {
            Widgets.Hint(L($"Chaque règle choisit des sons (oiseaux, voitures au loin...) et quand les jouer. Au plus {zone.RulesToPlay} à la fois."), wrap: true);
            foreach (uint rule in zone.Rules)
                ImGui.BulletText(name(rule));
            if (zone.DirectionalAmbiences.Count > 0)
            {
                ImGui.Dummy(new Vector2(0, 2));
                ImGui.TextColored(UiTheme.TextMuted, L("Ambiances directionnelles"));
                foreach (var ambience in zone.DirectionalAmbiences)
                    ImGui.BulletText(L($"{name(ambience.Sound)} (volume {ambience.Volume:0.##})"));
            }
        }

        if (Widgets.Section(L("Réglages"), "audio_zone_settings", defaultOpen: false) && Widgets.BeginProperties("##audio_zone_settings"))
        {
            Widgets.PropertyText(L("Ville (0 à 1)"), $"{zone.BuiltUpFactor:0.##}", L("Part de ville dans la zone : change l'écho des bruits (tirs, moteurs)."));
            Widgets.PropertyText(L("Densité de piétons"), $"{zone.MinPedDensity:0.##} - {zone.MaxPedDensity:0.##}");
            Widgets.PropertyText(L("Bruit de foule"), name(zone.PedWalla));
            Widgets.PropertyText(L("Vent"), name(zone.WindSounds));
            Widgets.PropertyText(L("Règle d'environnement"), name(zone.EnvironmentRule));
            Widgets.PropertyText(L("Scène audio"), name(zone.AudioScene));
            Widgets.EndProperties();
        }
    }

    private void DrawStaticEmitter(StaticEmitter emitter, Func<uint, string> name, Func<Vector3, string> v)
    {
        static string Clock(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

        if (Widgets.Section(L("Source"), "audio_emitter") && Widgets.BeginProperties("##audio_emitter"))
        {
            Widgets.PropertyText(L("Position"), v(emitter.Position));
            if (emitter.RadioStation != 0)
                Widgets.PropertyText(L("Radio"), name(emitter.RadioStation), L("Station de radio jouée par cette source."));
            Widgets.PropertyText(L("Son"), name(emitter.ChildSound));
            Widgets.PropertyText(L("Distance"), L($"{emitter.MinDistance:0.#} à {emitter.MaxDistance:0.#} m"),
                L("Entendu à plein volume jusqu'à la première distance (cercle jaune), plus du tout au-delà de la seconde (cercle pâle)."));
            Widgets.PropertyText(L("Volume"), string.Create(CultureInfo.InvariantCulture, $"{emitter.Volume / 100f:+0.#;-0.#;0} dB"));
            bool always = emitter.StartMinutes == emitter.EndMinutes || emitter.StartMinutes == 0 && emitter.EndMinutes >= 1440;
            Widgets.PropertyText(L("Heures"), always ? L("toujours") : L($"de {Clock(emitter.StartMinutes)} à {Clock(emitter.EndMinutes)}"));
            if (emitter.Interior != 0)
                Widgets.PropertyText(L("Intérieur"), name(emitter.Interior));
            if (emitter.Room != 0)
                Widgets.PropertyText(L("Salle"), name(emitter.Room));
            Widgets.PropertyText(L("Drapeaux"), $"0x{emitter.Flags:X8}");
            Widgets.EndProperties();
        }
    }
}
