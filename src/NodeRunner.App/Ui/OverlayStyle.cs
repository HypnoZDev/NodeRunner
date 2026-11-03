using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Flat, minimal look of the elements floating over the 3D view (no frosted glass): solid dark background, faint edge,
// soft shadow; buttons show only their icon until hovered.
internal static class OverlayStyle
{
    public const float MenuRounding = 10f;
    public const float ButtonRounding = 7f;

    private static readonly Vector4 Background = new(0.105f, 0.108f, 0.118f, 0.97f);
    private static readonly Vector4 Edge = new(1f, 1f, 1f, 0.07f);

    public static void Panel(ImDrawListPtr drawList, Vector2 min, Vector2 max, float rounding)
    {
        // Soft shadow: a few growing, fading rounded rectangles.
        for (int i = 1; i <= 4; i++)
        {
            var grow = new Vector2(i * 1.5f);
            drawList.AddRectFilled(min - grow + new Vector2(0, 2), max + grow + new Vector2(0, 2),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.06f)), rounding + i * 1.5f);
        }
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(Background), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(Edge), rounding, ImDrawFlags.None, 1f);
    }

    public static void WindowBackground(float rounding)
    {
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (size.X <= 1f || size.Y <= 1f)
            return;
        // After Begin, drawing is clipped inside the window padding: lift that for the background and its shadow.
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRectFullScreen();
        Panel(drawList, min, min + size, rounding);
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
            WindowBackground(MenuRounding);
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
