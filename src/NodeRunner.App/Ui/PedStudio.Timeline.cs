using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Models;

namespace NodeRunner.App.Ui;

// The ped viewer's timeline, under the view: play / pause, frame by frame, speed, loop, and a bar to drag the time.
internal sealed partial class PedStudio
{
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 1f, 2f];

    private bool _playing;
    private bool _loop = true;
    private int _speed = 3; // Index in Speeds: x1.
    private bool _resumeAfterDrag;

    // Bar height: one row of buttons above the time bar.
    private static float TimelineHeight => ImGui.GetFrameHeight() * 2f + ImGui.GetStyle().ItemSpacing.Y * 2f + 6f;

    private static int FrameCount(AnimationClip clip) => clip.Parts.Count > 0 ? Math.Max(1, clip.Parts.Max(p => p.Animation.FrameCount)) : 1;

    private static float FrameDuration(AnimationClip clip)
    {
        int frames = FrameCount(clip);
        return frames > 1 ? clip.Duration / (frames - 1) : clip.Duration;
    }

    private int Frame(AnimationClip clip)
    {
        float step = FrameDuration(clip);
        return step > 0f ? (int)MathF.Round(_time / step) : 0;
    }

    private void Advance(AnimationClip clip)
    {
        if (!_playing)
            return;
        _time += ImGui.GetIO().DeltaTime * Speeds[_speed];
        if (clip.Duration <= 0f)
            return;
        if (_loop)
            _time %= clip.Duration;
        else if (_time >= clip.Duration)
        {
            _time = clip.Duration;
            _playing = false;
        }
    }

    // The clips loop by themselves: the very end is drawn as the last frame, not as the first again.
    private float PoseTime(AnimationClip clip) => clip.Duration > 0f ? Math.Min(_time, clip.Duration * 0.9999f) : 0f;

    private void TogglePlay(AnimationClip clip)
    {
        // Played again from the start once stopped at the end.
        if (!_playing && !_loop && _time >= clip.Duration)
            _time = 0f;
        _playing = !_playing;
    }

    private void StepFrame(AnimationClip clip, int by)
    {
        _playing = false;
        int last = FrameCount(clip) - 1;
        int frame = Frame(clip) + by;
        frame = _loop ? (frame % (last + 1) + last + 1) % (last + 1) : Math.Clamp(frame, 0, last);
        _time = frame * FrameDuration(clip);
    }

    // Keys while the tab is under the mouse or focused: Space, arrows, Home / End.
    private void TimelineKeys()
    {
        if (_clip is not { } clip || ImGui.GetIO().WantTextInput)
            return;
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
            TogglePlay(clip);
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            StepFrame(clip, -1);
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            StepFrame(clip, 1);
        if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
            (_time, _playing) = (0f, false);
        if (ImGui.IsKeyPressed(ImGuiKey.End, false))
            (_time, _playing) = (clip.Duration, false);
    }

    private void DrawTimeline(AnimationClip clip)
    {
        ImGui.Dummy(new Vector2(0, 2));
        if (Widgets.IconButton(Icons.SkipBack, "ped_start", L("Au début (Origine)")))
            (_time, _playing) = (0f, false);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.StepBack, "ped_previous_frame", L("Image précédente (flèche gauche)")))
            StepFrame(clip, -1);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(_playing ? Icons.Pause : Icons.Play, "ped_play", _playing ? L("Pause (Espace)") : L("Lecture (Espace)"), active: _playing))
            TogglePlay(clip);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.StepForward, "ped_next_frame", L("Image suivante (flèche droite)")))
            StepFrame(clip, 1);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Repeat, "ped_loop", L("Boucle : l'animation recommence à la fin"), active: _loop))
            _loop = !_loop;
        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        int frames = FrameCount(clip);
        ImGui.TextUnformatted(L($"{_time:0.00} / {clip.Duration:0.00} s · image {Math.Min(Frame(clip), frames - 1) + 1} / {frames}"));

        // Speed, on the right.
        string speed = L($"{Icons.Gauge}  Vitesse x {Speeds[_speed]:0.##}");
        float speedWidth = ImGui.CalcTextSize(speed).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(0, 12f);
        float room = ImGui.GetContentRegionAvail().X;
        if (room > speedWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - speedWidth);
        if (Widgets.Button(speed, L("Vitesse de lecture (ralenti pour regarder un geste)")))
            ImGui.OpenPopup("##ped_speed");
        if (ImGui.BeginPopup("##ped_speed"))
        {
            for (int i = 0; i < Speeds.Length; i++)
            {
                if (ImGui.Selectable($"x {Speeds[i]:0.##}", i == _speed))
                    _speed = i;
            }
            ImGui.EndPopup();
        }

        DrawTimeBar(clip);
    }

    // The time bar: filled up to the time, a mark per second, dragged to move in the animation (paused meanwhile).
    private void DrawTimeBar(AnimationClip clip)
    {
        float width = ImGui.GetContentRegionAvail().X;
        float height = ImGui.GetFrameHeight();
        ImGui.InvisibleButton("##ped_time_bar", new Vector2(Math.Max(width, 10f), height));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (ImGui.IsItemActivated())
        {
            _resumeAfterDrag = _playing;
            _playing = false;
        }
        if (ImGui.IsItemActive() && clip.Duration > 0f)
            _time = Math.Clamp((ImGui.GetIO().MousePos.X - min.X) / (max.X - min.X), 0f, 1f) * clip.Duration;
        if (ImGui.IsItemDeactivated() && _resumeAfterDrag)
            _playing = true;
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var drawList = ImGui.GetWindowDrawList();
        float middle = (min.Y + max.Y) * 0.5f;
        var trackMin = new Vector2(min.X, middle - 3f);
        var trackMax = new Vector2(max.X, middle + 3f);
        drawList.AddRectFilled(trackMin, trackMax, Widgets.Col(UiTheme.Field), 3f);
        float at = clip.Duration > 0f ? Math.Clamp(_time / clip.Duration, 0f, 1f) : 0f;
        float x = min.X + (max.X - min.X) * at;
        drawList.AddRectFilled(trackMin, new Vector2(x, trackMax.Y), Widgets.Col(UiTheme.Accent), 3f);
        // A mark per second (per 10 s for long ones).
        if (clip.Duration > 0f)
        {
            float every = clip.Duration > 30f ? 10f : 1f;
            for (float second = every; second < clip.Duration; second += every)
            {
                float mark = min.X + (max.X - min.X) * (second / clip.Duration);
                drawList.AddLine(new Vector2(mark, middle - 7f), new Vector2(mark, middle - 4f), Widgets.Col(UiTheme.TextFaint));
            }
        }
        drawList.AddCircleFilled(new Vector2(x, middle), ImGui.IsItemActive() || ImGui.IsItemHovered() ? 7f : 6f, Widgets.Col(UiTheme.Text));
    }
}
