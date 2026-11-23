using System.Globalization;
using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Audio;

namespace NodeRunner.App.Ui;

// An ambient zone or a static emitter of the game's audio data, read-only.
internal sealed partial class PropertiesPanel
{
    private const int MaxWavesListed = 60;

    private SoundPlayer? _zonePlayer;
    private AudioSelection? _audioFor;
    private readonly Dictionary<uint, (IReadOnlyList<WaveRef> Waves, bool More)> _soundWaves = new();
    private Task<(string Path, SoundBank Bank)>? _bankLoading;
    private WaveRef _bankLoadingWave;
    private (string Path, SoundBank Bank)? _lastBank;
    private string? _waveError;

    private SoundPlayer ZonePlayer => _zonePlayer ??= new SoundPlayer(state.Sound);

    // The sound of a zone stops when it is no longer selected.
    private void StopZoneSoundIfDeselected()
    {
        if (_audioFor is not null && state.SelectedAudio != _audioFor)
        {
            StopZoneSound();
            _audioFor = null;
        }
    }

    private void StopZoneSound()
    {
        if (_zonePlayer is { } player && (player.OwnsOutput || player.IsBusy))
            player.Unload();
        _soundWaves.Clear();
        _bankLoading = null;
        _waveError = null;
    }

    // Groups of waves (an ambient rule, the sound of an emitter): a node per group, a play button per wave, and the
    // player above them once a wave is chosen.
    private void DrawSoundList(IReadOnlyList<(string Label, uint Sound)> groups, string id, Func<uint, string> name)
    {
        if (state.World.SoundTree is not { } tree)
        {
            Widgets.Hint(state.World.SoundTreeError is { } error ? L($"Lecture impossible : {error}") : L("Lecture des sons du jeu..."));
            return;
        }
        UpdateBankLoading();
        if (_bankLoading is not null)
            Widgets.Hint(L("Lecture du son..."));
        else if (_waveError is { } waveError)
            WarningText(waveError);
        else if (_zonePlayer is { } player && (player.OwnsOutput || player.IsBusy))
            player.Draw();

        for (int i = 0; i < groups.Count; i++)
        {
            var (label, sound) = groups[i];
            if (!_soundWaves.TryGetValue(sound, out var listed))
                _soundWaves[sound] = listed = sound == 0 ? ([], false) : tree.Waves(sound, MaxWavesListed);
            var (waves, more) = listed;
            if (waves.Count == 0)
            {
                ImGui.Bullet();
                ImGui.TextColored(UiTheme.TextMuted, L($"{label} (aucun son à écouter)"));
                continue;
            }
            string count = more ? L($"{waves.Count}+ sons") : waves.Count == 1 ? L("1 son") : L($"{waves.Count} sons");
            if (!ImGui.TreeNode($"{label}  ({count})###{id}{i}"))
                continue;
            for (int j = 0; j < waves.Count; j++)
            {
                var wave = waves[j];
                bool playing = _zonePlayer?.IsPlaying(wave) == true;
                if (Widgets.IconButton(playing ? Icons.Pause : Icons.Play, $"{id}_{i}_{j}", playing ? L("Pause") : L("Écouter")))
                {
                    if (playing)
                        state.Sound.Pause();
                    else if (Equals(_zonePlayer?.Key, wave) && _zonePlayer!.OwnsOutput)
                        state.Sound.Resume();
                    else
                        PlayWave(tree, wave);
                }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                string bank = tree.BankPath(wave.Bank) is { } path ? BankLabel(path) : name(wave.Bank);
                ImGui.TextUnformatted($"{bank}  ·  {name(wave.Wave)}");
            }
            if (more)
                ImGui.TextDisabled(L($"Seuls les {MaxWavesListed} premiers sons sont listés."));
            ImGui.TreePop();
        }
    }

    // "resident/doors" for x64\audio\sfx\RESIDENT.rpf\doors.awc.
    private static string BankLabel(string path)
    {
        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        string folder = Path.GetFileName(Path.GetDirectoryName(normalized) ?? "");
        if (folder.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            folder = folder[..^4];
        return $"{folder.ToLowerInvariant()}/{Path.GetFileNameWithoutExtension(normalized)}";
    }

    private void PlayWave(SoundTree tree, WaveRef wave)
    {
        if (state.World.Files is not { } files || tree.BankPath(wave.Bank) is not { } path)
            return;
        _waveError = null;
        _bankLoadingWave = wave;
        var cached = _lastBank;
        var reader = new SoundBankReader(files.AudioKey);
        _bankLoading = Task.Run(() =>
        {
            if (cached is { } last && last.Path == path)
                return last;
            var data = files.ReadFile(path) ?? throw new InvalidDataException(L("Fichier introuvable."));
            return (path, reader.Read(data));
        });
    }

    private void UpdateBankLoading()
    {
        if (_bankLoading is not { IsCompleted: true } done)
            return;
        _bankLoading = null;
        if (!done.IsCompletedSuccessfully)
        {
            _waveError = L($"Son illisible : {done.Exception?.GetBaseException().Message}");
            return;
        }
        _lastBank = done.Result;
        var wave = _bankLoadingWave;
        if (done.Result.Bank.Tracks.FirstOrDefault(t => t.Id == wave.TrackId) is { } track)
            ZonePlayer.Load(wave, [track]);
        else
            _waveError = L("Ce son n'est pas dans son fichier .awc.");
    }
    private void DrawAudioPlacement(AudioSelection selection, HashNames names)
    {
        string Name(uint hash) => hash == 0 ? L("aucun")
            : state.World.AudioGameData is { } data ? AudioName(hash, data, names) : names.Describe(hash);
        static string V(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}");

        if (selection != _audioFor)
        {
            StopZoneSound();
            _audioFor = selection;
        }
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
            Widgets.Hint(L($"Chaque règle choisit des sons (oiseaux, voitures au loin...) et quand les jouer. Au plus {zone.RulesToPlay} à la fois. Ouvrez une règle pour écouter ses sons."), wrap: true);
            var data = state.World.AudioGameData;
            var groups = new List<(string Label, uint Sound)>();
            foreach (uint hash in zone.Rules)
            {
                if (data?.AmbientRule(hash) is not { } rule)
                {
                    groups.Add((name(hash), 0));
                    continue;
                }
                bool always = rule.StartMinutes == rule.EndMinutes || rule.StartMinutes == 0 && rule.EndMinutes >= 1440;
                groups.Add((always ? name(hash) : L($"{name(hash)}, de {Clock(rule.StartMinutes)} à {Clock(rule.EndMinutes)}"), rule.ChildSound));
            }
            DrawSoundList(groups, "audio_rule", name);
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

    private static string Clock(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    private void DrawStaticEmitter(StaticEmitter emitter, Func<uint, string> name, Func<Vector3, string> v)
    {
        if (Widgets.Section(L("Écouter"), "audio_emitter_sounds"))
        {
            if (emitter.RadioStation != 0)
                Widgets.Hint(L("Cette source joue une radio : elle ne peut pas être écoutée ici."), wrap: true);
            if (emitter.ChildSound != 0)
                DrawSoundList([(name(emitter.ChildSound), emitter.ChildSound)], "audio_emitter", name);
        }

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
