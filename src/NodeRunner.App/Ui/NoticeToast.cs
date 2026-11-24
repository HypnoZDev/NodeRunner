using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// The last notice (AppState.Notify) in the bottom right corner of the 3D view, where the eyes are, with an Undo button
// when it reports a change that can still be undone. Stays while hovered.
internal sealed class NoticeToast(AppState state)
{
    private const double Seconds = 8;
    private const double UndoableSeconds = 12;
    private const float Margin = 12f;

    private AppNotice? _notice;
    private DateTime _since;

    public void Draw()
    {
        if (state.Notice is not { } notice)
            return;
        if (!ReferenceEquals(notice, _notice))
            (_notice, _since) = (notice, notice.Time);

        var undoable = notice.Undoable is { } edit && state.History.NextUndo == edit && !state.Online.IsGuest ? edit : null;
        if ((DateTime.Now - _since).TotalSeconds > (undoable is null ? Seconds : UndoableSeconds))
            return;

        var (position, size) = DockLayout.SceneArea;
        if (size.X < 200f || size.Y < 120f)
            return;
        // Above the timeline of a cutscene or of the character viewer.
        float bottom = Margin + (state.CutsceneOpen || state.RecordingOpen || state.PedShown ? PlaybackBar.Height + 28f : 0f);
        float width = MathF.Min(ImGui.GetFontSize() * 30f, size.X - Margin * 2f);

        ImGui.SetNextWindowPos(new Vector2(position.X + size.X - Margin, position.Y + size.Y - bottom), ImGuiCond.Always, new Vector2(1f, 1f));
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(width, size.Y * 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("##notice_toast", flags))
        {
            OverlayStyle.WindowBackground(8f, strong: true);
            ImGui.TextColored(notice.IsWarning ? UiTheme.Warning : UiTheme.Success, notice.IsWarning ? Icons.TriangleAlert : Icons.Check);
            ImGui.SameLine();
            ImGui.PushTextWrapPos(width - 12f);
            ImGui.TextUnformatted(notice.Text);
            ImGui.PopTextWrapPos();
            if (undoable is not null)
            {
                if (Widgets.Button(L($"{Icons.Undo}  Annuler"), L($"Annuler : {undoable.Description} (Ctrl+Z)")))
                {
                    state.Undo();
                    state.Notify(L($"Annulé : {undoable.Description}"));
                }
            }
            // Read before it fades: hovering keeps it.
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
                _since = DateTime.Now;
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }
}
