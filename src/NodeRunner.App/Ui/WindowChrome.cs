using System.Drawing;
using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// The window's own title bar: the menu bar takes the place of the Windows caption (brand monogram, menus, credit,
// minimise / maximise / close buttons).
internal static class WindowChrome
{
    public const float ButtonWidth = 46f;

    // What a point of the window is, for Windows (WM_NCHITTEST).
    public enum Part { Client, Caption, MaximizeButton }

    private static RectangleF _bar;
    private static RectangleF _maximize;
    private static readonly List<RectangleF> _interactive = [];
    private static bool _blocked;

    public static bool MaximizeHovered { get; set; }

    public static bool MaximizePressed { get; set; }

    public static bool IsMaximized { get; set; }

    public static bool MinimizeRequested { get; set; }
    public static bool CloseRequested { get; set; }

    public static void Begin(Vector2 position, Vector2 size)
    {
        _bar = new RectangleF(position.X, position.Y, size.X, size.Y);
        _interactive.Clear();
        // While a menu is open, a click on the bar must reach ImGui (it closes the menu).
        _blocked = ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
    }

    public static void TrackItem()
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        _interactive.Add(new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y));
    }

    public static Part HitTest(PointF point)
    {
        if (_maximize.Contains(point))
            return Part.MaximizeButton;
        if (_blocked || !_bar.Contains(point) || _interactive.Any(r => r.Contains(point)))
            return Part.Client;
        return Part.Caption;
    }

    public static void DrawButtons()
    {
        var drawList = ImGui.GetWindowDrawList();
        float height = _bar.Height;
        float x = _bar.Right - ButtonWidth * 3;
        ImGui.PushClipRect(new Vector2(_bar.Left, _bar.Top), new Vector2(_bar.Right, _bar.Bottom), false);

        if (Button(drawList, "##minimize", x, height, false, out var center))
            MinimizeRequested = true;
        drawList.AddLine(center + new Vector2(-5, 0), center + new Vector2(5, 0), Widgets.Col(UiTheme.Text));

        // Maximise: clicks come through Windows (see the main form), only drawn here.
        _maximize = new RectangleF(x + ButtonWidth, _bar.Top, ButtonWidth, height);
        var maxMin = new Vector2(_maximize.Left, _maximize.Top);
        var maxMax = new Vector2(_maximize.Right, _maximize.Bottom);
        if (MaximizeHovered || MaximizePressed)
            drawList.AddRectFilled(maxMin, maxMax, Widgets.Col(new Vector4(1f, 1f, 1f, MaximizePressed ? 0.12f : 0.07f)));
        var c = Widgets.Snap((maxMin + maxMax) * 0.5f) + new Vector2(0.5f, 0.5f);
        uint color = Widgets.Col(UiTheme.Text);
        if (IsMaximized)
        {
            drawList.AddRect(c + new Vector2(-5, -3), c + new Vector2(3, 5), color, 1f);
            drawList.AddLine(c + new Vector2(-3, -5), c + new Vector2(5, -5), color);
            drawList.AddLine(c + new Vector2(5, -5), c + new Vector2(5, 3), color);
        }
        else
            drawList.AddRect(c + new Vector2(-5, -5), c + new Vector2(5, 5), color, 1f);

        if (Button(drawList, "##close", x + ButtonWidth * 2, height, true, out center))
            CloseRequested = true;
        uint closeColor = Widgets.Col(UiTheme.Text);
        drawList.AddLine(center + new Vector2(-5, -5), center + new Vector2(5.5f, 5.5f), closeColor);
        drawList.AddLine(center + new Vector2(5, -5), center + new Vector2(-5.5f, 5.5f), closeColor);

        ImGui.PopClipRect();
    }

    private static bool Button(ImDrawListPtr drawList, string id, float x, float height, bool danger, out Vector2 center)
    {
        var position = new Vector2(x, _bar.Top);
        ImGui.SetCursorScreenPos(position);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(ButtonWidth, height));
        TrackItem();
        var size = new Vector2(ButtonWidth, height);
        if (ImGui.IsItemHovered())
        {
            var fill = danger ? UiTheme.Rgb(0xC42B1C) : new Vector4(1f, 1f, 1f, ImGui.IsItemActive() ? 0.12f : 0.07f);
            drawList.AddRectFilled(position, position + size, Widgets.Col(fill));
        }
        center = Widgets.Snap(position + size * 0.5f) + new Vector2(0.5f, 0.5f);
        return clicked;
    }
}
