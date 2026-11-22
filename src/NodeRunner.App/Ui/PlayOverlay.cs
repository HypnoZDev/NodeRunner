using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Over the 3D view in play mode: the keys along the bottom, and what is still loading in the middle.
internal sealed class PlayOverlay(AppState state)
{
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking;

    public void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        var keys = state.Keys;
        string hint = L($"{keys.MoveText()} : se déplacer   ·   Maj : sprinter   ·   Alt : marcher   ·   Espace : sauter ou grimper   ·   Molette : distance   ·   Échap : quitter");
        Panel("##play_keys", hint, viewport.Pos + new Vector2(viewport.Size.X * 0.5f, viewport.Size.Y - 24f), new Vector2(0.5f, 1f), UiTheme.TextMuted);
        if (state.Play.Waiting is { } waiting)
            Panel("##play_wait", waiting, viewport.Pos + viewport.Size * 0.5f + new Vector2(0f, 90f), new Vector2(0.5f, 0.5f), UiTheme.Text);
    }

    private static void Panel(string id, string text, Vector2 at, Vector2 pivot, Vector4 color)
    {
        ImGui.SetNextWindowPos(at, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowBgAlpha(0.55f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 8f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        if (ImGui.Begin(id, Flags))
            ImGui.TextColored(color, text);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}
