using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Glass look of the elements floating over the 3D view: a tinted, see-through background with a light sheen at the top
// and a bright edge, over a soft shadow; buttons show only their icon until hovered.
internal static class OverlayStyle
{
    public const float MenuRounding = 10f;
    public const float ButtonRounding = 7f;

    private static Vector4 Edge => UiTheme.GlassEdge;

    // Frosted glass: the blurred 3D view (BackdropBlur) under the tint. MainForm sets it before the interface is
    // built and blurs the same rectangle (interface coordinates) afterwards, if Used.
    public static readonly IntPtr BackdropTexture = 0x4E52_4746; // Fixed id, never a pointer.
    public static bool BackdropAvailable;
    public static Vector2 BackdropMin, BackdropMax;
    public static bool BackdropUsed;

    public static void Panel(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding, bool strong = false)
    {
        // Soft shadow: a few growing, fading rounded rectangles.
        for (int i = 1; i <= 4; i++)
        {
            var grow = new Vector2(i * 1.5f);
            drawList.AddRectFilled(min - grow + new Vector2(0, 2), max + grow + new Vector2(0, 2),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.06f)), rounding + i * 1.5f);
        }
        GlassFill(drawList, min, max, rounding, strong);
        Sheen(drawList, min, max, rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(Edge), rounding, ImDrawFlags.None, 1f);
    }

    // The glass itself: the blurred 3D view seen through a tint where it lies over the view, the tint alone (see-through)
    // without the blur. Strong = more tint, for text (menus, search).
    public static void GlassFill(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding, bool strong,
        ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
    {
        var clipMin = Vector2.Max(min, BackdropMin);
        var clipMax = Vector2.Min(max, BackdropMax);
        var size = BackdropMax - BackdropMin;
        if (!BackdropAvailable || clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y || size.X < 1f || size.Y < 1f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(strong ? UiTheme.GlassStrong : UiTheme.Glass), rounding, corners);
            return;
        }
        BackdropUsed = true;
        // Opaque underneath: the part beyond the 3D view (a menu over a panel) shows no panel text through it.
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(UiTheme.Chrome), rounding, corners);
        drawList.PushClipRect(clipMin, clipMax, true);
        drawList.AddImageRounded(BackdropTexture, min, max, (min - BackdropMin) / size, (max - BackdropMin) / size,
            0xFFFFFFFF, rounding, corners);
        drawList.PopClipRect();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(strong ? UiTheme.FrostStrong : UiTheme.Frost), rounding, corners);
    }

    // Glass behind a window with its own background turned off (WindowBg transparent), drawn just after Begin. Below
    // the title bar only, which ImGui has already drawn.
    public static void WindowGlass(bool strong, bool titleBar = false, float rounding = -1f)
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        float title = titleBar ? ImGui.GetFrameHeight() : 0f;
        if (rounding < 0f)
            rounding = ImGui.GetStyle().WindowRounding;
        if (max.X - min.X <= 1f || max.Y - min.Y <= title + 1f)
            return;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRectFullScreen();
        GlassFill(drawList, min with { Y = min.Y + title }, max, rounding, strong,
            title > 0f ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersAll);
        drawList.PopClipRect();
    }

    // The light caught by the glass: a faint white fading downwards over the top of the panel, and a brighter line on
    // its top edge.
    public static void Sheen(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        float height = Math.Min(max.Y - min.Y, 28f);
        if (height <= rounding || max.X - min.X <= rounding * 2f)
            return;
        uint top = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f));
        uint clear = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0f));
        drawList.AddRectFilledMultiColor(new Vector2(min.X + rounding * 0.5f, min.Y + 1f), new Vector2(max.X - rounding * 0.5f, min.Y + height),
            top, top, clear, clear);
        drawList.AddLine(new Vector2(min.X + rounding, min.Y + 0.5f), new Vector2(max.X - rounding, min.Y + 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)), 1f);
    }

    public static void WindowBackground(float rounding, bool strong = false)
    {
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (size.X <= 1f || size.Y <= 1f)
            return;
        // After Begin, drawing is clipped inside the window padding: lift that for the background and its shadow.
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRectFullScreen();
        Panel(drawList, min, min + size, rounding, strong);
        drawList.PopClipRect();
    }

    public const float ButtonSize = 30f;

    private static readonly Vector4 IconIdle = new(0.80f, 0.81f, 0.84f, 1f);
    private static readonly Vector4 IconBright = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 IconDisabled = new(1f, 1f, 1f, 0.28f);
    private static readonly Vector4 ChevronColor = new(1f, 1f, 1f, 0.45f);

    // Square icon button of the floating bar: just the icon until hovered (faint light fill); the active one (current
    // tool, toggle on) keeps a soft light fill and a white icon.
    public static bool IconButton(string id, string icon, string? tooltip = null, bool active = false, bool enabled = true)
    {
        var size = new Vector2(ButtonSize);
        bool clicked = ImGui.InvisibleButton(id, size) && enabled;
        bool hovered = ImGui.IsItemHovered();
        DrawButtonBackground(active, enabled && hovered, enabled && ImGui.IsItemActive());

        var min = ImGui.GetItemRectMin();
        var color = !enabled ? IconDisabled : active || hovered ? IconBright : IconIdle;
        var textSize = ImGui.CalcTextSize(icon);
        ImGui.GetWindowDrawList().AddText(Snap(min + (size - textSize) * 0.5f), ImGui.GetColorU32(color), icon);
        ShowTooltip(hovered, tooltip);
        return clicked;
    }

    public static bool MenuButton(string id, string icon, string label, string? tooltip = null)
    {
        const float padding = 10f, iconGap = 7f, chevronGap = 5f, chevronScale = 0.8f;
        var font = ImGui.GetFont();
        float chevronFontSize = ImGui.GetFontSize() * chevronScale;
        var iconSize = ImGui.CalcTextSize(icon);
        var labelSize = ImGui.CalcTextSize(label);
        var chevronSize = font.CalcTextSizeA(chevronFontSize, float.MaxValue, 0f, Icons.ChevronDown);
        var size = new Vector2(MathF.Ceiling(padding + iconSize.X + iconGap + labelSize.X + chevronGap + chevronSize.X + padding - 2f), ButtonSize);

        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        DrawButtonBackground(false, hovered, ImGui.IsItemActive());

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        float x = min.X + padding;
        drawList.AddText(Snap(new Vector2(x, min.Y + (ButtonSize - iconSize.Y) * 0.5f)), ImGui.GetColorU32(hovered ? IconBright : IconIdle), icon);
        x += iconSize.X + iconGap;
        drawList.AddText(Snap(new Vector2(x, min.Y + (ButtonSize - labelSize.Y) * 0.5f)), ImGui.GetColorU32(hovered ? IconBright : IconIdle), label);
        x += labelSize.X + chevronGap;
        drawList.AddText(font, chevronFontSize, Snap(new Vector2(x, min.Y + (ButtonSize - chevronSize.Y) * 0.5f + 1f)),
            ImGui.GetColorU32(hovered ? IconBright with { W = 0.75f } : ChevronColor), Icons.ChevronDown);
        ShowTooltip(hovered, tooltip);
        return clicked;
    }

    private static void DrawButtonBackground(bool active, bool hovered, bool held)
    {
        float alpha = held ? 0.18f : active ? (hovered ? 0.17f : 0.13f) : hovered ? 0.08f : 0f;
        if (alpha > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)), ButtonRounding);
    }

    private static void ShowTooltip(bool hovered, string? tooltip)
    {
        if (tooltip is not null && hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(tooltip);
    }

    private static Vector2 Snap(Vector2 p) => Widgets.Snap(p);

    public static void Divider()
    {
        const float lineHeight = 16f;
        ImGui.SameLine(0, 7);
        var p = ImGui.GetCursorScreenPos();
        float top = p.Y + (ButtonSize - lineHeight) * 0.5f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(p.X, top), new Vector2(p.X, top + lineHeight), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f)));
        ImGui.Dummy(new Vector2(1, ButtonSize));
        ImGui.SameLine(0, 7);
    }

    // Popup opened from the floating bar, in the same style, just under the point anchor (bottom left of the button
    // that opens it).
    public static bool BeginPopup(string id, Vector2 anchor)
    {
        ImGui.SetNextWindowPos(anchor + new Vector2(0, 8));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(1f, 1f, 1f, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, MenuRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 6));
        bool open = ImGui.BeginPopup(id);
        if (open)
            WindowBackground(MenuRounding, strong: true);
        else
            PopStyle();
        return open;
    }

    public static void EndPopup()
    {
        ImGui.EndPopup();
        PopStyle();
    }

    private static void PopStyle()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(3);
    }
}
