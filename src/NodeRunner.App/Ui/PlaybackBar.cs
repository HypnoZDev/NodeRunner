using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// A timeline to play something that lasts (an animation, a cutscene): play / pause, frame by frame, speed, loop, and a
// bar to drag the time, with optional marks (camera cuts).
internal sealed class PlaybackBar(string id)
{
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 1f, 2f];

    public float Time { get; set; }
    public bool Playing { get; set; }
    public bool Loop { get; set; } = true;
    private int _speed = 3; // Index in Speeds: x1.
    private bool _resumeAfterDrag;

    // One row of buttons above the time bar.
    public static float Height => ImGui.GetFrameHeight() * 2f + ImGui.GetStyle().ItemSpacing.Y * 2f + 6f;

    public void Start()
    {
        Time = 0f;
        Playing = true;
    }

    // Moves the time on while playing; stops at the end without loop.
    public void Advance(float duration)
    {
        if (!Playing)
            return;
        Time += ImGui.GetIO().DeltaTime * Speeds[_speed];
        if (duration <= 0f)
            return;
        if (Loop)
            Time %= duration;
        else if (Time >= duration)
        {
            Time = duration;
            Playing = false;
        }
    }

    // The time to show: the very end is the last frame, not the first again (clips loop by themselves).
    public float ShownTime(float duration) => duration > 0f ? Math.Clamp(Time, 0f, duration * 0.9999f) : 0f;

    private static int Frames(float duration, float frameStep) => frameStep > 0f ? Math.Max(1, (int)MathF.Round(duration / frameStep) + 1) : 1;

    private int Frame(float frameStep) => frameStep > 0f ? (int)MathF.Round(Time / frameStep) : 0;

    public void TogglePlay(float duration)
    {
        // Played again from the start once stopped at the end.
        if (!Playing && !Loop && Time >= duration)
            Time = 0f;
        Playing = !Playing;
    }

    public void Step(float duration, float frameStep, int by)
    {
        Playing = false;
        int count = Frames(duration, frameStep);
        int frame = Frame(frameStep) + by;
        frame = Loop ? (frame % count + count) % count : Math.Clamp(frame, 0, count - 1);
        Time = Math.Min(frame * frameStep, duration);
    }

    // Space, arrows, Home / End: the caller says when they are for the bar (mouse over its view).
    public void Keys(float duration, float frameStep)
    {
        if (ImGui.GetIO().WantTextInput)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
            TogglePlay(duration);
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            Step(duration, frameStep, -1);
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            Step(duration, frameStep, 1);
        if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
            (Time, Playing) = (0f, false);
        if (ImGui.IsKeyPressed(ImGuiKey.End, false))
            (Time, Playing) = (duration, false);
    }

    // extra: more to say after the time ("plan 3 / 16"); marks: times drawn on the bar instead of one per second.
    public void Draw(float duration, float frameStep, string? extra = null, IReadOnlyList<float>? marks = null)
    {
        ImGui.PushID(id);
        ImGui.Dummy(new Vector2(0, 2));
        if (Widgets.IconButton(Icons.SkipBack, "start", L("Au début (Origine)")))
            (Time, Playing) = (0f, false);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.StepBack, "previous_frame", L("Image précédente (flèche gauche)")))
            Step(duration, frameStep, -1);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Playing ? Icons.Pause : Icons.Play, "play", Playing ? L("Pause (Espace)") : L("Lecture (Espace)"), active: Playing))
            TogglePlay(duration);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.StepForward, "next_frame", L("Image suivante (flèche droite)")))
            Step(duration, frameStep, 1);
        ImGui.SameLine(0, 2);
        if (Widgets.IconButton(Icons.Repeat, "loop", L("Boucle : l'animation recommence à la fin"), active: Loop))
            Loop = !Loop;
        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        int frames = Frames(duration, frameStep);
        string text = L($"{Time:0.00} / {duration:0.00} s · image {Math.Min(Frame(frameStep), frames - 1) + 1} / {frames}");
        ImGui.TextUnformatted(extra is null ? text : $"{text} · {extra}");

        // Speed, on the right.
        string speed = L($"{Icons.Gauge}  Vitesse x {Speeds[_speed]:0.##}");
        float speedWidth = ImGui.CalcTextSize(speed).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(0, 12f);
        float room = ImGui.GetContentRegionAvail().X;
        if (room > speedWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room - speedWidth);
        if (Widgets.Button(speed, L("Vitesse de lecture (ralenti pour regarder un geste)")))
            ImGui.OpenPopup("##speed");
        if (ImGui.BeginPopup("##speed"))
        {
            for (int i = 0; i < Speeds.Length; i++)
            {
                if (ImGui.Selectable($"x {Speeds[i]:0.##}", i == _speed))
                    _speed = i;
            }
            ImGui.EndPopup();
        }

        DrawTimeBar(duration, marks);
        ImGui.PopID();
    }

    // Filled up to the time, dragged to move in time (paused meanwhile).
    private void DrawTimeBar(float duration, IReadOnlyList<float>? marks)
    {
        float width = ImGui.GetContentRegionAvail().X;
        float height = ImGui.GetFrameHeight();
        ImGui.InvisibleButton("##time_bar", new Vector2(Math.Max(width, 10f), height));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (ImGui.IsItemActivated())
        {
            _resumeAfterDrag = Playing;
            Playing = false;
        }
        if (ImGui.IsItemActive() && duration > 0f)
            Time = Math.Clamp((ImGui.GetIO().MousePos.X - min.X) / (max.X - min.X), 0f, 1f) * duration;
        if (ImGui.IsItemDeactivated() && _resumeAfterDrag)
            Playing = true;
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var drawList = ImGui.GetWindowDrawList();
        float middle = (min.Y + max.Y) * 0.5f;
        var trackMin = new Vector2(min.X, middle - 3f);
        var trackMax = new Vector2(max.X, middle + 3f);
        drawList.AddRectFilled(trackMin, trackMax, Widgets.Col(UiTheme.Field), 3f);
        float at = duration > 0f ? Math.Clamp(Time / duration, 0f, 1f) : 0f;
        float x = min.X + (max.X - min.X) * at;
        drawList.AddRectFilled(trackMin, new Vector2(x, trackMax.Y), Widgets.Col(UiTheme.Accent), 3f);
        if (duration > 0f)
        {
            float X(float time) => min.X + (max.X - min.X) * (time / duration);
            if (marks is not null)
            {
                foreach (float mark in marks)
                    drawList.AddLine(new Vector2(X(mark), middle - 9f), new Vector2(X(mark), middle + 9f), Widgets.Col(UiTheme.TextMuted));
            }
            else
            {
                // A mark per second (per 10 s for long ones).
                float every = duration > 30f ? 10f : 1f;
                for (float second = every; second < duration; second += every)
                    drawList.AddLine(new Vector2(X(second), middle - 7f), new Vector2(X(second), middle - 4f), Widgets.Col(UiTheme.TextFaint));
            }
        }
        drawList.AddCircleFilled(new Vector2(x, middle), ImGui.IsItemActive() || ImGui.IsItemHovered() ? 7f : 6f, Widgets.Col(UiTheme.Text));
    }
}
