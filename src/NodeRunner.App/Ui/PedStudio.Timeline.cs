using ImGuiNET;
using NodeRunner.Data.Models;

namespace NodeRunner.App.Ui;

// The ped viewer's timeline, under the view.
internal sealed partial class PedStudio
{
    private readonly PlaybackBar _bar = new("ped_timeline");

    private static float TimelineHeight => PlaybackBar.Height;

    private static float FrameDuration(AnimationClip clip)
    {
        int frames = clip.Parts.Count > 0 ? Math.Max(1, clip.Parts.Max(p => p.Animation.FrameCount)) : 1;
        return frames > 1 ? clip.Duration / (frames - 1) : clip.Duration;
    }

    private void Advance(AnimationClip clip) => _bar.Advance(clip.Duration);

    private float PoseTime(AnimationClip clip) => _bar.ShownTime(clip.Duration);

    // Keys while the tab is under the mouse or focused.
    private void TimelineKeys()
    {
        if (_clip is not { } clip)
            return;
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        _bar.Keys(clip.Duration, FrameDuration(clip));
    }

    private void DrawTimeline(AnimationClip clip) => _bar.Draw(clip.Duration, FrameDuration(clip));
}
