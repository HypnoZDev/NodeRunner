using System.Diagnostics;
using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Opened from the NRS monogram: the author's channels and site, each behind its own logo (drawn here, no image files).
// Comes in with a fade and a rise, the rows slide in one after the other and their logos pop.
internal sealed class SocialWindow
{
    private enum Logo { YouTube, TikTok, Instagram, Site, Tipeee }

    private sealed record Channel(Logo Logo, string Name, string Detail, string Url);

    private static readonly Channel[] Channels =
    [
        new(Logo.YouTube, "YouTube", "Inside Rockstar", "https://www.youtube.com/channel/UC7VSh1gqOmgsfstzcCj61wA"),
        new(Logo.TikTok, "TikTok", "@insiderockstar", "https://www.tiktok.com/@insiderockstar"),
        new(Logo.Instagram, "Instagram", "@insiderockstar", "https://www.instagram.com/insiderockstar/"),
        new(Logo.Site, "Inside Rockstar", "insiderockstar.fr", "https://www.insiderockstar.fr/"),
        new(Logo.Tipeee, "Tipeee", "", "https://fr.tipeee.com/inside-rockstar"),
    ];

    private const float Width = 330f, RowHeight = 50f, LogoSize = 34f;
    private bool _open;
    private double _openedAt;
    private int _closedOnFrame = -1;

    public void Toggle()
    {
        // The click that closed it from outside (on the monogram) must not reopen it.
        if (!_open && _closedOnFrame == ImGui.GetFrameCount())
            return;
        _open = !_open;
        _openedAt = ImGui.GetTime();
    }

    public void Draw()
    {
        if (!_open)
            return;
        float since = (float)(ImGui.GetTime() - _openedAt);
        float appear = EaseOut(Math.Clamp(since / 0.32f, 0f, 1f));

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter() + new Vector2(0f, (1f - appear) * 22f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowFocus();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f, 16f));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, appear);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
        bool visible = ImGui.Begin("##social", flags);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent(since, appear);
        bool inside = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        ImGui.End();
        ImGui.PopStyleVar();

        if (since > 0.1f && (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !inside))
            (_open, _closedOnFrame) = (false, ImGui.GetFrameCount());
    }

    private void DrawContent(float since, float appear)
    {
        OverlayStyle.WindowBackground(12f, strong: true);
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted("Inside Rockstar");
        ImGui.PopFont();
        Widgets.Hint(L("Retrouve-moi sur mes réseaux"));

        // Close cross, top right.
        var cross = new Vector2(origin.X + Width - 20f, origin.Y - 2f);
        ImGui.SetCursorScreenPos(cross);
        if (ImGui.InvisibleButton("##social_close", new Vector2(20f, 20f)))
            _open = false;
        bool crossHovered = ImGui.IsItemHovered();
        if (crossHovered)
            drawList.AddCircleFilled(cross + new Vector2(10f), 11f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f)));
        var crossSize = ImGui.CalcTextSize(Icons.X);
        drawList.AddText(cross + (new Vector2(20f) - crossSize) * 0.5f, ImGui.GetColorU32(crossHovered ? UiTheme.Text : UiTheme.TextMuted), Icons.X);

        // Accent line that grows from the middle.
        float y = origin.Y + ImGui.GetTextLineHeightWithSpacing() * 2f + 10f;
        float grow = EaseOut(Math.Clamp((since - 0.12f) / 0.45f, 0f, 1f));
        float mid = origin.X + Width * 0.5f;
        uint accent = ImGui.GetColorU32(UiTheme.Accent);
        uint clear = ImGui.GetColorU32(UiTheme.Accent with { W = 0f });
        drawList.AddRectFilledMultiColor(new Vector2(mid - Width * 0.5f * grow, y), new Vector2(mid, y + 2f), clear, accent, accent, clear);
        drawList.AddRectFilledMultiColor(new Vector2(mid, y), new Vector2(mid + Width * 0.5f * grow, y + 2f), accent, clear, clear, accent);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, y + 12f));
        for (int i = 0; i < Channels.Length; i++)
            DrawRow(Channels[i], i, since, appear);

        ImGui.Dummy(new Vector2(Width, 2f));
        Widgets.Hint(L("Merci, ton soutien garde NodeRunner Studio gratuit."), wrap: true);
    }

    private void DrawRow(Channel channel, int index, float since, float appear)
    {
        float local = Math.Clamp((since - 0.1f - index * 0.065f) / 0.38f, 0f, 1f);
        float slide = EaseOut(local);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, appear * slide);

        bool clicked = ImGui.InvisibleButton($"##social_{index}", new Vector2(Width, RowHeight));
        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin() + new Vector2((1f - slide) * 28f, 0f);
        var max = min + new Vector2(Width, RowHeight - 6f);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 0.09f : 0.035f)), 9f);
        drawList.AddRect(min, max, ImGui.GetColorU32(channel.Logo == Logo.Tipeee ? Rgb(0xF2545B, 0.55f) : new Vector4(1f, 1f, 1f, hovered ? 0.14f : 0.06f)), 9f);

        // Logo pops in slightly after its row, with a small overshoot.
        float pop = BackOut(Math.Clamp((local - 0.25f) / 0.75f, 0f, 1f)) * (hovered ? 1.06f : 1f);
        var centre = new Vector2(min.X + 10f + LogoSize * 0.5f, (min.Y + max.Y) * 0.5f);
        if (pop > 0.01f)
            DrawLogo(drawList, channel.Logo, centre, LogoSize * pop);

        float textX = min.X + 20f + LogoSize;
        string detail = channel.Logo == Logo.Tipeee ? L("Me soutenir") : channel.Detail;
        string name = channel.Logo == Logo.Site ? L("Le site Inside Rockstar") : channel.Name;
        float lineHeight = ImGui.GetTextLineHeight();
        float top = (min.Y + max.Y) * 0.5f - lineHeight + 1f;
        drawList.AddText(UiTheme.SemiboldFont, UiTheme.SemiboldFont.FontSize, new Vector2(textX, top), ImGui.GetColorU32(UiTheme.Text), name);
        drawList.AddText(new Vector2(textX, top + lineHeight), ImGui.GetColorU32(UiTheme.TextMuted), detail);

        var arrowSize = ImGui.CalcTextSize(Icons.ChevronRight);
        var arrow = new Vector2(max.X - 14f - arrowSize.X + (hovered ? 3f : 0f), (min.Y + max.Y - arrowSize.Y) * 0.5f);
        drawList.AddText(arrow, ImGui.GetColorU32(hovered ? UiTheme.Text : UiTheme.TextFaint), Icons.ChevronRight);
        ImGui.PopStyleVar();

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
            OpenLink(channel.Url);
    }

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            ImGui.SetClipboardText(url);
        }
    }

    // Logos as rounded tiles, like app icons, s = tile size.
    private static void DrawLogo(ImDrawListPtr drawList, Logo logo, Vector2 c, float s)
    {
        var half = new Vector2(s * 0.5f);
        float rounding = s * 0.24f;
        uint white = ImGui.GetColorU32(Vector4.One);
        switch (logo)
        {
            case Logo.YouTube:
                drawList.AddRectFilled(c - half, c + half, ImGui.GetColorU32(Rgb(0xFF0033)), rounding);
                drawList.AddTriangleFilled(c + new Vector2(-0.12f, -0.16f) * s, c + new Vector2(-0.12f, 0.16f) * s, c + new Vector2(0.17f, 0f) * s, white);
                break;
            case Logo.TikTok:
                drawList.AddRectFilled(c - half, c + half, ImGui.GetColorU32(Rgb(0x010101)), rounding);
                Note(drawList, c + new Vector2(-0.045f, -0.035f) * s, s, ImGui.GetColorU32(Rgb(0x25F4EE)));
                Note(drawList, c + new Vector2(0.045f, 0.035f) * s, s, ImGui.GetColorU32(Rgb(0xFE2C55)));
                Note(drawList, c, s, white);
                break;
            case Logo.Instagram:
            {
                int first = drawList.VtxBuffer.Size;
                drawList.AddRectFilled(c - half, c + half, white, rounding);
                Gradient(drawList, first, c - half, s);
                float line = MathF.Max(1.5f, s * 0.07f);
                var inner = new Vector2(s * 0.29f);
                drawList.AddRect(c - inner, c + inner, white, s * 0.14f, ImDrawFlags.None, line);
                drawList.AddCircle(c, s * 0.12f, white, 0, line);
                drawList.AddCircleFilled(c + new Vector2(0.17f, -0.17f) * s, s * 0.035f, white);
                break;
            }
            case Logo.Site:
            {
                drawList.AddRectFilled(c - half, c + half, ImGui.GetColorU32(UiTheme.Accent), rounding);
                ImGui.PushFont(UiTheme.TitleFont);
                var size = ImGui.CalcTextSize(Icons.Globe) * (s / LogoSize);
                drawList.AddText(UiTheme.TitleFont, UiTheme.TitleFont.FontSize * (s / LogoSize), c - size * 0.5f, white, Icons.Globe);
                ImGui.PopFont();
                break;
            }
            case Logo.Tipeee:
                drawList.AddRectFilled(c - half, c + half, ImGui.GetColorU32(Rgb(0xF2545B)), rounding);
                drawList.AddCircleFilled(c + new Vector2(-0.095f, -0.06f) * s, s * 0.115f, white);
                drawList.AddCircleFilled(c + new Vector2(0.095f, -0.06f) * s, s * 0.115f, white);
                drawList.AddTriangleFilled(c + new Vector2(-0.205f, -0.025f) * s, c + new Vector2(0.205f, -0.025f) * s, c + new Vector2(0f, 0.22f) * s, white);
                break;
        }
    }

    // TikTok's note: round head, stem and a curled flag.
    private static void Note(ImDrawListPtr drawList, Vector2 c, float s, uint color)
    {
        float stem = s * 0.085f;
        var top = c + new Vector2(0.05f, -0.25f) * s;
        var foot = c + new Vector2(0.05f, 0.12f) * s;
        drawList.AddLine(top, foot, color, stem);
        drawList.AddCircleFilled(c + new Vector2(-0.055f, 0.12f) * s, s * 0.115f, color);
        drawList.AddBezierQuadratic(top, c + new Vector2(0.08f, -0.1f) * s, c + new Vector2(0.23f, -0.11f) * s, color, stem * 0.9f);
    }

    // Instagram's yellow, orange, pink, purple, blue gradient from the bottom left corner to the top right one.
    private static unsafe void Gradient(ImDrawListPtr drawList, int first, Vector2 min, float s)
    {
        ReadOnlySpan<uint> stops = [0xFEDA75, 0xFA7E1E, 0xD62976, 0x962FBF, 0x4F5BD5];
        var vertices = (ImDrawVert*)drawList.VtxBuffer.Data;
        for (int i = first; i < drawList.VtxBuffer.Size; i++)
        {
            ref var v = ref vertices[i];
            float t = Math.Clamp(((v.pos.X - min.X) + (min.Y + s - v.pos.Y)) / (2f * s), 0f, 1f) * (stops.Length - 1);
            int a = Math.Min((int)t, stops.Length - 2);
            var from = Rgb(stops[a]);
            var color = Vector4.Lerp(from, Rgb(stops[a + 1]), t - a);
            v.col = (v.col & 0xFF000000) | ((uint)(color.Z * 255f) << 16) | ((uint)(color.Y * 255f) << 8) | (uint)(color.X * 255f);
        }
    }

    private static Vector4 Rgb(uint rgb, float alpha = 1f) => UiTheme.Rgb(rgb) with { W = alpha };

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

    private static float BackOut(float t)
    {
        const float k = 1.9f;
        t -= 1f;
        return 1f + t * t * ((k + 1f) * t + k);
    }
}
