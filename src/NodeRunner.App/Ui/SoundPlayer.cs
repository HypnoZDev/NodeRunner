using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Audio;
using NodeRunner.Data.Audio;

namespace NodeRunner.App.Ui;

// Player of a game sound: waveform (click or drag to move in it), play / pause, back to start, loop, volume.
// Decodes in the background; the shared SoundOutput plays one sound at a time.
internal sealed class SoundPlayer(SoundOutput output)
{
    private const int Buckets = 1024;

    private sealed record Loaded(object Key, short[] Samples, int Channels, int Rate, int LoopStart, float[] Peaks);

    private Task<Loaded>? _decoding;
    private object? _decodingKey;
    private Loaded? _loaded;
    private string? _error;
    private bool _dragging;

    public object? Key => _decodingKey;
    public bool IsBusy => _decoding is not null;

    // key: what is played (compared to know whether a row is the one playing). Two tracks: played as left / right.
    public void Load(object key, IReadOnlyList<SoundTrack> tracks)
    {
        if (tracks.Count == 0)
            return;
        _decodingKey = key;
        _error = null;
        output.Stop();
        _loaded = null;
        var first = tracks[0];
        _decoding = Task.Run(() =>
        {
            short[] samples;
            int channels = tracks.Count;
            if (channels == 1)
            {
                samples = first.Decode();
            }
            else
            {
                var parts = tracks.Select(t => t.Decode()).ToArray();
                int frames = parts.Min(p => p.Length);
                samples = new short[frames * channels];
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < channels; c++)
                        samples[i * channels + c] = parts[c][i];
            }
            return new Loaded(key, samples, channels, first.SampleRate, first.LoopStart, Peaks(samples, channels));
        });
    }

    public void Unload()
    {
        output.Stop();
        _loaded = null;
        _decoding = null;
        _decodingKey = null;
    }

    public bool IsPlaying(object key) => Equals(_decodingKey, key) && output.IsPlaying;

    // The shared output plays this player's sound (not one started elsewhere).
    public bool OwnsOutput => _loaded is not null && ReferenceEquals(output.Samples, _loaded.Samples);

    public static float Height => 64f + ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;

    public void Draw()
    {
        Update();
        float width = ImGui.GetContentRegionAvail().X;
        DrawWave(width, 64f);
        DrawControls();
    }

    private void Update()
    {
        if (_decoding is not { IsCompleted: true } done)
            return;
        _decoding = null;
        if (done.IsFaulted)
        {
            _error = done.Exception?.GetBaseException().Message;
            return;
        }
        var loaded = done.Result;
        if (!Equals(loaded.Key, _decodingKey))
            return;
        _loaded = loaded;
        output.Play(loaded.Samples, loaded.Channels, loaded.Rate, loaded.LoopStart);
    }

    private void DrawWave(float width, float height)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(width, 50f), height);
        ImGui.InvisibleButton("##sound_wave", size);
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(UiTheme.Field), UiTheme.FrameRounding);

        if (_loaded is not { } loaded)
        {
            string text = _decoding is not null ? L("Lecture du son...")
                : _error is not null ? L($"Son illisible : {_error}")
                : L("Choisis un son dans la liste.");
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(min + (size - textSize) * 0.5f, ImGui.GetColorU32(UiTheme.TextMuted), text);
            return;
        }

        double duration = output.Duration;
        bool hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActivated())
            _dragging = true;
        if (_dragging && duration > 0)
        {
            float t = Math.Clamp((ImGui.GetMousePos().X - min.X) / size.X, 0f, 1f);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _dragging = false;
                output.Seek(t * duration);
            }
        }

        float played = duration > 0 ? (float)(output.Position / duration) : 0f;
        if (_dragging)
            played = Math.Clamp((ImGui.GetMousePos().X - min.X) / size.X, 0f, 1f);
        uint before = ImGui.GetColorU32(UiTheme.Accent);
        uint after = ImGui.GetColorU32(UiTheme.TextMuted with { W = 0.55f });
        int columns = (int)size.X;
        float middle = min.Y + size.Y * 0.5f, half = size.Y * 0.45f;
        for (int x = 0; x < columns; x++)
        {
            int bucket = Math.Min(Buckets - 1, x * Buckets / Math.Max(1, columns));
            float low = loaded.Peaks[bucket * 2], high = loaded.Peaks[bucket * 2 + 1];
            float px = min.X + x + 0.5f;
            drawList.AddLine(new Vector2(px, middle - high * half), new Vector2(px, middle - low * half + 1f),
                (float)x / columns < played ? before : after);
        }
        if (loaded.LoopStart >= 0 && output.Loop && duration > 0)
        {
            float lx = min.X + size.X * (float)(loaded.LoopStart / (double)loaded.Rate / duration);
            drawList.AddLine(new Vector2(lx, min.Y), new Vector2(lx, min.Y + size.Y), ImGui.GetColorU32(UiTheme.TextFaint), 1f);
        }
        float head = min.X + size.X * played;
        drawList.AddLine(new Vector2(head, min.Y), new Vector2(head, min.Y + size.Y), ImGui.GetColorU32(UiTheme.Text), 1.5f);

        if (hovered && duration > 0)
        {
            float t = Math.Clamp((ImGui.GetMousePos().X - min.X) / size.X, 0f, 1f);
            ImGui.SetTooltip(Time(t * duration));
        }
    }

    private void DrawControls()
    {
        bool ready = _loaded is not null;
        bool playing = output.IsPlaying;
        if (Widgets.IconButton(playing ? Icons.Pause : Icons.Play, "sound_play", playing ? L("Pause") : L("Lire"), ready))
        {
            if (playing)
                output.Pause();
            else
                output.Resume();
        }
        ImGui.SameLine(0, 2f);
        if (Widgets.IconButton(Icons.Square, "sound_stop", L("Arrêter et revenir au début"), ready))
        {
            output.Pause();
            output.Seek(0);
        }
        ImGui.SameLine(0, 2f);
        bool loop = output.Loop;
        if (Widgets.IconButton(Icons.Repeat, "sound_loop",
                _loaded is { LoopStart: >= 0 } ? L("Boucle (ce son est joué en boucle dans le jeu)") : L("Boucle"), ready, loop))
            output.SetLoop(!loop, _loaded?.LoopStart is >= 0 and var start ? start : 0);

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(ready ? $"{Time(output.Position)} / {Time(output.Duration)}" : "--:-- / --:--");
        if (_loaded is { } loaded)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(loaded.Channels == 2 ? L($"{loaded.Rate} Hz, stéréo") : L($"{loaded.Rate} Hz"));
        }

        float volumeWidth = 110f;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - volumeWidth - ImGui.GetFrameHeight() - 4f));
        Widgets.IconButton(output.Volume > 0 ? Icons.Volume : Icons.VolumeOff, "sound_volume_icon", L("Volume"));
        ImGui.SameLine(0, 4f);
        ImGui.SetNextItemWidth(volumeWidth);
        float volume = output.Volume * 100f;
        if (ImGui.SliderFloat("##sound_volume", ref volume, 0f, 100f, "%.0f %%"))
            output.Volume = volume / 100f;

        if (output.Error is { } error)
            Widgets.Hint(L($"Sortie audio indisponible : {error}"), wrap: true);
    }

    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        int whole = (int)seconds;
        return $"{whole / 60}:{whole % 60:00},{(int)((seconds - whole) * 10)}";
    }

    // Lowest and highest value (-1 to 1) of each slice of the sound, all channels together.
    private static float[] Peaks(short[] samples, int channels)
    {
        var peaks = new float[Buckets * 2];
        int frames = samples.Length / Math.Max(1, channels);
        if (frames == 0)
            return peaks;
        for (int b = 0; b < Buckets; b++)
        {
            int from = (int)((long)b * frames / Buckets), to = Math.Max(from + 1, (int)((long)(b + 1) * frames / Buckets));
            int low = 0, high = 0;
            for (int i = from * channels; i < Math.Min(samples.Length, to * channels); i++)
            {
                low = Math.Min(low, samples[i]);
                high = Math.Max(high, samples[i]);
            }
            peaks[b * 2] = low / 32768f;
            peaks[b * 2 + 1] = high / 32767f;
        }
        return peaks;
    }
}
