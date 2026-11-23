using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Audio;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Native.Audio;

namespace NodeRunner.App.Ui;

// "Sons" list of the viewer (GTA V): every sound bank (.awc) of the game, its sounds, and the player.
internal sealed unsafe partial class ViewerPanel
{
    private const int SoundsFilter = 3;

    private sealed record SoundFile(string Path, string Name);

    private Task<List<SoundFile>>? _soundFilesTask;
    private List<SoundFile>? _soundFiles;
    private object? _soundFilesFor;
    private List<SoundFile> _soundRows = [];
    private string? _soundRowsSearch;
    private SoundFile? _soundFile;
    private bool _scrollToSound;
    private Task<SoundBank>? _bankTask;
    private SoundBank? _bank;
    private string? _bankError;
    private int _soundTrack = -1;
    private bool _stereo = true;
    private SoundPlayer? _soundPlayer;

    private SoundPlayer Player => _soundPlayer ??= new SoundPlayer(state.Sound);

    // Leaving the sounds (other list, viewer closed) stops what plays.
    public void PauseSound()
    {
        if (_soundPlayer is { OwnsOutput: true } && state.Sound.IsPlaying)
            state.Sound.Pause();
    }

    // Opens a sound bank of the game's archives (explorer).
    public void OpenSoundFile(string path)
    {
        _filter = SoundsFilter;
        _search = "";
        var file = new SoundFile(path, SoundName(path));
        OpenBank(file);
        _scrollToSound = true;
    }

    private static string SoundName(string path)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        if (folder.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            folder = folder[..^4];
        return $"{folder}/{Path.GetFileNameWithoutExtension(path)}".ToLowerInvariant();
    }

    private void UpdateSoundFiles()
    {
        var files = state.World.Files;
        if (!ReferenceEquals(files, _soundFilesFor))
        {
            _soundFilesFor = files;
            _soundFiles = null;
            _soundRowsSearch = null;
            _soundFilesTask = files is null ? null : Task.Run(() => files.EnumerateFiles(".awc")
                .Select(p => new SoundFile(p, SoundName(p)))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList());
        }
        if (_soundFilesTask is { IsCompleted: true } done)
        {
            _soundFilesTask = null;
            _soundFiles = done.IsCompletedSuccessfully ? done.Result : [];
        }
    }

    private void DrawSoundList()
    {
        UpdateSoundFiles();
        if (_soundFiles is null)
        {
            Widgets.Hint(state.World.Files is null ? L("En attente de l'ouverture du jeu...") : L("Préparation de la liste..."));
            return;
        }
        string search = _search.Trim().ToLowerInvariant();
        if (_soundRowsSearch != search)
        {
            _soundRowsSearch = search;
            _soundRows = search.Length == 0 ? _soundFiles
                : _soundFiles.Where(f => f.Name.Contains(search, StringComparison.Ordinal)).ToList();
        }
        Widgets.Hint(_soundRows.Count == 0 ? L("Aucun résultat") : L($"{Widgets.Count(_soundRows.Count)} fichier(s) de sons"));
        if (!ImGui.BeginChild("##sound_files", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.EndChild();
            return;
        }
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        if (_scrollToSound)
        {
            _scrollToSound = false;
            int index = _soundRows.FindIndex(r => r.Path == _soundFile?.Path);
            if (index >= 0)
                ImGui.SetScrollY(Math.Max(0, index * rowHeight - ImGui.GetWindowHeight() * 0.4f));
        }
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput && _soundRows.Count > 0)
        {
            int current = _soundRows.FindIndex(r => r.Path == _soundFile?.Path);
            int next = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? current + 1 : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? current - 1 : current;
            if (next != current && next >= 0 && next < _soundRows.Count)
            {
                OpenBank(_soundRows[next]);
                _scrollToSound = true;
            }
        }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(_soundRows.Count, rowHeight);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var file = _soundRows[i];
                ImGui.PushID(i);
                if (ImGui.Selectable($"{Icons.AudioLines}  {file.Name}", file.Path == _soundFile?.Path))
                    OpenBank(file);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(file.Path);
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndChild();
    }

    private void OpenBank(SoundFile file)
    {
        if (file.Path == _soundFile?.Path)
            return;
        _soundFile = file;
        _bank = null;
        _bankError = null;
        _soundTrack = -1;
        var files = state.World.Files;
        if (files is null)
            return;
        var reader = new SoundBankReader(files.AudioKey);
        _bankTask = Task.Run(() => reader.Read(files.ReadFile(file.Path) ?? throw new InvalidDataException(L("Fichier introuvable."))));
    }

    private void UpdateBank()
    {
        if (_bankTask is not { IsCompleted: true } done)
            return;
        _bankTask = null;
        if (done.IsCompletedSuccessfully)
        {
            _bank = done.Result;
            if (_bank.Tracks.Count > 0)
                PlayTrack(0);
        }
        else
        {
            _bankError = done.Exception?.GetBaseException().Message;
        }
    }

    // The sounds of the chosen file, and the player under them.
    private void DrawSoundBank()
    {
        UpdateBank();
        if (_soundFile is null)
        {
            Widgets.Hint(L("Choisis un fichier de sons dans la liste. Chaque fichier (.awc) contient un ou plusieurs sons."), wrap: true);
            return;
        }
        ImGui.TextUnformatted($"{Icons.AudioLines}  {_soundFile.Name}");
        if (_bank is null)
        {
            Widgets.Hint(_bankError is { } error ? L($"Fichier illisible : {error}") : L("Lecture du fichier..."), wrap: true);
            return;
        }
        bool groups = _bank.Tracks.Any(t => t.Group >= 0);
        if (groups)
        {
            ImGui.SameLine();
            ImGui.Checkbox(L("Canaux ensemble (stéréo)"), ref _stereo);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(L("Les musiques et ambiances sont rangées en deux canaux (gauche, droite) : les jouer ensemble."));
        }

        float playerHeight = SoundPlayer.Height + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginTable("##sound_tracks", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
                new Vector2(0, Math.Max(80f, ImGui.GetContentRegionAvail().Y - playerHeight))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L("Son"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(L("Durée"), ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn(L("Fréquence"), ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn(L("Format"), ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn(L("Canal"), ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();
            for (int i = 0; i < _bank.Tracks.Count; i++)
            {
                var track = _bank.Tracks[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool playing = Player.IsPlaying(PlayKey(i));
                string icon = playing ? Icons.Volume : Icons.Play;
                string loop = track.LoopStart >= 0 ? $"  {Icons.Repeat}" : "";
                if (ImGui.Selectable($"{icon}  {TrackName(track)}{loop}", i == _soundTrack, ImGuiSelectableFlags.SpanAllColumns))
                    PlayTrack(i);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(track.LoopStart >= 0 ? L("Joué en boucle dans le jeu.") : L("Clic : écouter"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(SoundPlayer.Time(track.Seconds));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{track.SampleRate} Hz");
                ImGui.TableNextColumn();
                ImGui.TextDisabled(track.IsCompressed ? "ADPCM" : "PCM");
                ImGui.TableNextColumn();
                if (track.Group >= 0)
                    ImGui.TextDisabled((ChannelIndex(i) + 1).ToString());
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        Player.Draw();
    }

    private static string TrackName(SoundTrack track) => $"0x{track.Id:X8}";

    private int ChannelIndex(int index)
    {
        var track = _bank!.Tracks[index];
        int n = 0;
        for (int i = 0; i < index; i++)
            if (_bank.Tracks[i].Group == track.Group)
                n++;
        return n;
    }

    private IReadOnlyList<SoundTrack> GroupOf(int index)
    {
        var track = _bank!.Tracks[index];
        if (!_stereo || track.Group < 0)
            return [track];
        var group = _bank.Tracks.Where(t => t.Group == track.Group).ToList();
        return group.Count == 2 && group[0].SampleRate == group[1].SampleRate ? group : [track];
    }

    private object PlayKey(int index) => (_soundFile?.Path, GroupOf(index).Count == 2 ? -1 - _bank!.Tracks[index].Group : index);

    private void PlayTrack(int index)
    {
        _soundTrack = index;
        Player.Load(PlayKey(index), GroupOf(index));
    }

    private void DrawSoundInspector()
    {
        if (_soundFile is null)
        {
            Widgets.Hint(L("Choisis un fichier de sons : ses réglages s'affichent ici."), wrap: true);
            return;
        }
        if (Widgets.Section(L("Fichier"), "sound_file") && Widgets.BeginProperties("sound_file"))
        {
            Widgets.PropertyText(L("Nom"), _soundFile.Name, _soundFile.Path);
            if (state.World.Files?.GetInfo(_soundFile.Path) is { } info)
                Widgets.PropertyText(L("Taille"), L($"{info.Size / 1024.0:F0} Ko"));
            if (_bank is not null)
                Widgets.PropertyText(L("Sons"), Widgets.Count(_bank.Tracks.Count));
            Widgets.EndProperties();
        }
        if (Widgets.Button(L($"{Icons.Copy}  Copier le nom"), _soundFile.Path))
            ImGui.SetClipboardText(_soundFile.Name);

        if (_bank is null || _soundTrack < 0 || _soundTrack >= _bank.Tracks.Count)
            return;
        var track = _bank.Tracks[_soundTrack];
        ImGui.Spacing();
        if (Widgets.Section(L("Son"), "sound_track") && Widgets.BeginProperties("sound_track"))
        {
            Widgets.PropertyText(L("Identifiant"), TrackName(track), L("Nom du son dans le fichier, sous forme de hash"));
            Widgets.PropertyText(L("Durée"), SoundPlayer.Time(track.Seconds));
            Widgets.PropertyText(L("Fréquence"), $"{track.SampleRate} Hz");
            Widgets.PropertyText(L("Format"), track.IsCompressed ? L("ADPCM (compressé)") : L("PCM (non compressé)"));
            if (track.LoopStart >= 0)
                Widgets.PropertyText(L("Boucle"), L($"reprend à {SoundPlayer.Time((double)track.LoopStart / Math.Max(1, track.SampleRate))}"));
            if (track.Headroom != 0)
                Widgets.PropertyText(L("Marge de volume"), $"{track.Headroom / 100.0:F1} dB",
                    L("Le son a été baissé de cette valeur dans le fichier ; le jeu la rend à la lecture."));
            Widgets.EndProperties();
        }
        if (Widgets.Button(L($"{Icons.Save}  Enregistrer en .wav..."), L("Enregistre ce son (ou les deux canaux) en fichier .wav, lisible partout.")))
            AskWaveExport(GroupOf(_soundTrack), $"{Path.GetFileNameWithoutExtension(_soundFile.Path)}_{track.Id:X8}");
    }

    private void AskWaveExport(IReadOnlyList<SoundTrack> tracks, string name)
    {
        state.DialogRequest = owner =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = L("Enregistrer le son"),
                FileName = name + ".wav",
                Filter = L("Son WAV (*.wav)|*.wav"),
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            try
            {
                var parts = tracks.Select(t => t.Decode()).ToArray();
                int frames = parts.Min(p => p.Length);
                var samples = new short[frames * parts.Length];
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < parts.Length; c++)
                        samples[i * parts.Length + c] = parts[c][i];
                WaveFile.Write(dialog.FileName, samples, parts.Length, tracks[0].SampleRate);
                state.Notify(L($"Son enregistré : {Path.GetFileName(dialog.FileName)}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                state.Notify(L($"Enregistrement impossible : {ex.Message}"), warning: true);
            }
        };
    }
}
