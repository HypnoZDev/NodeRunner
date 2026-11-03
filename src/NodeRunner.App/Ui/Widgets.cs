using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

/// <summary>
/// Interface building blocks shared by the panels: sections, segmented controls, search field, flat icon buttons,
/// layer rows, label / value property grids, axis fields.
/// </summary>
internal static class Widgets
{
    private const float SectionHeight = 28f;

    public static uint Col(Vector4 color) => ImGui.GetColorU32(color);

    /// <summary>
    /// Collapsible section: a thin line across the card, then a semibold title with a chevron on the right.
    /// <paramref name="id"/> keeps it open or closed across renames.
    /// </summary>
    public static bool Section(string title, string id, bool defaultOpen = true) =>
        Section(title, id, defaultOpen, null, null, out _);

    /// <summary>Section with a small icon button before its chevron (e.g. an eye showing or hiding the whole group).</summary>
    public static bool Section(string title, string id, bool defaultOpen, string? actionIcon, string? actionTooltip, out bool actionClicked)
    {
        var storage = ImGui.GetStateStorage();
        uint key = ImGui.GetID($"##section_{id}");
        bool open = storage.GetBool(key, defaultOpen);

        var drawList = ImGui.GetWindowDrawList();
        float windowLeft = ImGui.GetWindowPos().X;
        float lineY = ImGui.GetCursorScreenPos().Y + 2f;
        drawList.AddLine(new Vector2(windowLeft, lineY), new Vector2(windowLeft + ImGui.GetWindowWidth(), lineY), Col(UiTheme.Divider));
        ImGui.Dummy(new Vector2(0, 3));

        var min = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        ImGui.SetNextItemAllowOverlap();
        if (ImGui.InvisibleButton($"##section_button_{id}", new Vector2(width, SectionHeight)))
        {
            open = !open;
            storage.SetBool(key, open);
        }
        bool hovered = ImGui.IsItemHovered();
        var after = ImGui.GetCursorScreenPos();

        ImGui.PushFont(UiTheme.SemiboldFont);
        var titleSize = ImGui.CalcTextSize(title);
        drawList.AddText(Snap(new Vector2(min.X, min.Y + (SectionHeight - titleSize.Y) * 0.5f)), Col(UiTheme.Text), title);
        ImGui.PopFont();

        string chevron = open ? Icons.ChevronDown : Icons.ChevronRight;
        var chevronSize = ImGui.CalcTextSize(chevron);
        float chevronX = min.X + width - chevronSize.X;
        drawList.AddText(Snap(new Vector2(chevronX, min.Y + (SectionHeight - chevronSize.Y) * 0.5f)),
            Col(hovered ? UiTheme.TextMuted : UiTheme.TextFaint), chevron);

        actionClicked = false;
        if (actionIcon is not null)
        {
            float size = SectionHeight - 4f;
            ImGui.SetCursorScreenPos(new Vector2(chevronX - 6f - size, min.Y + 2f));
            actionClicked = IconButton(actionIcon, $"section_action_{id}", actionTooltip, size: size);
            ImGui.SetCursorScreenPos(after);
        }

        if (open)
            ImGui.Dummy(new Vector2(0, 1));
        return open;
    }

    /// <summary>Row of mutually exclusive choices (filters) filling the available width. Returns true when the choice changes.</summary>
    public static bool Segmented(string id, IReadOnlyList<string> labels, ref int selected, string?[]? tooltips = null)
    {
        float total = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        float height = ImGui.GetFrameHeight() + 2f;
        const float inset = 2f;
        var min = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + new Vector2(total, height), Col(UiTheme.Field), UiTheme.FrameRounding + 1f);

        float segment = (total - inset * 2f) / labels.Count;
        bool changed = false;
        for (int i = 0; i < labels.Count; i++)
        {
            var segmentMin = new Vector2(MathF.Round(min.X + inset + i * segment), min.Y + inset);
            var segmentSize = new Vector2(MathF.Round(segment), height - inset * 2f);
            ImGui.SetCursorScreenPos(segmentMin);
            if (ImGui.InvisibleButton($"##{id}_{i}", segmentSize) && selected != i)
            {
                selected = i;
                changed = true;
            }
            bool hovered = ImGui.IsItemHovered();
            if (i == selected)
                drawList.AddRectFilled(segmentMin, segmentMin + segmentSize, Col(UiTheme.Rgb(0x3A3C44)), UiTheme.FrameRounding);
            var textSize = ImGui.CalcTextSize(labels[i]);
            var color = i == selected || hovered ? UiTheme.Text : UiTheme.TextMuted;
            drawList.AddText(Snap(segmentMin + (segmentSize - textSize) * 0.5f), Col(color), labels[i]);
            if (tooltips?[i] is { } tooltip && hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                ImGui.SetTooltip(tooltip);
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(total, height));
        return changed;
    }

    /// <summary>Search field filling the width: magnifier on the left, a cross on the right to clear it. Returns true when the text changes.</summary>
    public static bool SearchField(string id, string hint, ref string text, uint maxLength = 128, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var padding = ImGui.GetStyle().FramePadding;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(30f, padding.Y + 1f));
        ImGui.SetNextItemWidth(-float.Epsilon);
        ImGui.SetNextItemAllowOverlap();
        bool changed = ImGui.InputTextWithHint(id, hint, ref text, maxLength, flags);
        ImGui.PopStyleVar();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconSize = ImGui.CalcTextSize(Icons.Search);
        drawList.AddText(Snap(new Vector2(min.X + 9f, (min.Y + max.Y - iconSize.Y) * 0.5f)), Col(UiTheme.TextFaint), Icons.Search);

        if (text.Length > 0)
        {
            var after = ImGui.GetCursorScreenPos();
            float size = max.Y - min.Y - 4f;
            ImGui.SetCursorScreenPos(new Vector2(max.X - size - 2f, min.Y + 2f));
            if (IconButton(Icons.X, $"{id}_clear", "Effacer", size: size))
            {
                text = "";
                changed = true;
            }
            ImGui.SetCursorScreenPos(after);
        }
        return changed;
    }

    /// <summary>
    /// Square frameless button showing one icon: faint fill when hovered, blue tint when <paramref name="active"/>.
    /// <paramref name="size"/> 0 = height of a field.
    /// </summary>
    public static bool IconButton(string icon, string id, string? tooltip, bool enabled = true, bool active = false, float size = 0f)
    {
        if (size <= 0f)
            size = ImGui.GetFrameHeight();
        bool clicked = ImGui.InvisibleButton($"##{id}", new Vector2(size)) && enabled;
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var min = ImGui.GetItemRectMin();
        var drawList = ImGui.GetWindowDrawList();

        if (active)
            drawList.AddRectFilled(min, min + new Vector2(size), Col(UiTheme.Accent with { W = hovered ? 0.28f : 0.20f }), UiTheme.FrameRounding);
        else if (enabled && hovered)
            drawList.AddRectFilled(min, min + new Vector2(size), Col(new Vector4(1f, 1f, 1f, ImGui.IsItemActive() ? 0.12f : 0.07f)), UiTheme.FrameRounding);

        var color = !enabled ? UiTheme.TextFaint with { W = 0.6f } : active ? UiTheme.AccentText : hovered ? UiTheme.Text : UiTheme.TextMuted;
        var iconSize = ImGui.CalcTextSize(icon);
        drawList.AddText(Snap(min + (new Vector2(size) - iconSize) * 0.5f), Col(color), icon);
        if (tooltip is not null && hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Frameless toolbar button: icon and optional text, background only when hovered (blue tint when active).</summary>
    public static bool ToolbarButton(string icon, string? label, string tooltip, bool enabled = true, bool active = false)
    {
        ImGui.BeginDisabled(!enabled);
        ImGui.PushStyleColor(ImGuiCol.Button, active ? UiTheme.Accent with { W = 0.20f } : Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? UiTheme.Accent with { W = 0.28f } : new Vector4(1f, 1f, 1f, 0.07f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text, active ? UiTheme.AccentText : UiTheme.Text);
        bool clicked = ImGui.Button(label is null ? $"{icon}##{tooltip}" : $"{icon}  {label}");
        ImGui.PopStyleColor(4);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Short faint vertical line between groups of toolbar buttons (call between two items on the same line).</summary>
    public static void ToolbarSeparator()
    {
        ImGui.SameLine(0, 8);
        var p = ImGui.GetCursorScreenPos();
        float height = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddLine(new Vector2(p.X, p.Y + 5f), new Vector2(p.X, p.Y + height - 5f), Col(new Vector4(1f, 1f, 1f, 0.10f)));
        ImGui.Dummy(new Vector2(1, height));
        ImGui.SameLine(0, 8);
    }

    /// <summary>Filled secondary button (icon and text), <paramref name="width"/> 0 = fit the text.</summary>
    public static bool Button(string label, string? tooltip = null, float width = 0f, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        bool clicked = ImGui.Button(label, new Vector2(width, 0));
        ImGui.EndDisabled();
        if (tooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Blue button for the main action of a card.</summary>
    public static bool PrimaryButton(string label, float width = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, UiTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.AccentHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
        bool clicked = ImGui.Button(label, new Vector2(width, 0));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    /// <summary>
    /// Row of a layer list, clickable as a whole: colour dot, label, grey count, and an eye on the right.
    /// Returns true when clicked (the caller toggles the layer).
    /// </summary>
    public static bool LayerRow(string id, bool visible, Vector4 color, string label, string? count = null)
    {
        float height = ImGui.GetFrameHeight();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        bool clicked = ImGui.InvisibleButton($"##layer_{id}", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
            drawList.AddRectFilled(min - new Vector2(4f, 0f), min + new Vector2(width + 4f, height), Col(new Vector4(1f, 1f, 1f, 0.05f)), UiTheme.FrameRounding);

        float centerY = min.Y + height * 0.5f;
        drawList.AddCircleFilled(new Vector2(min.X + 6f, centerY), 4f, Col(visible ? color : color with { W = 0.25f }));
        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(Snap(new Vector2(min.X + 20f, centerY - labelSize.Y * 0.5f)), Col(visible ? UiTheme.Text : UiTheme.TextFaint), label);

        string eye = visible ? Icons.Eye : Icons.EyeOff;
        var eyeSize = ImGui.CalcTextSize(eye);
        float eyeX = min.X + width - eyeSize.X - 4f;
        drawList.AddText(Snap(new Vector2(eyeX, centerY - eyeSize.Y * 0.5f)),
            Col(!visible ? UiTheme.TextFaint : hovered ? UiTheme.Text : UiTheme.TextMuted), eye);
        if (count is not null)
        {
            var countSize = ImGui.CalcTextSize(count);
            float countX = eyeX - 10f - countSize.X;
            if (countX > min.X + 20f + labelSize.X + 8f)
                drawList.AddText(Snap(new Vector2(countX, centerY - countSize.Y * 0.5f)), Col(UiTheme.TextFaint), count);
        }
        return clicked;
    }

    /// <summary>Starts a two-column grid: labels on the left, values on the right. Call <see cref="EndProperties"/> if it returns true.</summary>
    public static bool BeginProperties(string id, float labelShare = 0.36f)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.None))
            return false;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, labelShare);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 1f - labelShare);
        return true;
    }

    public static void EndProperties() => ImGui.EndTable();

    /// <summary>New row: writes the label, then moves to the value cell (the next field fills it).</summary>
    public static void Property(string label, string? tooltip = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.TextMuted, label);
        if (tooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(tooltip);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-float.Epsilon);
    }

    /// <summary>Read-only row; <paramref name="tooltip"/> (e.g. a full file path) shows when the value is hovered.</summary>
    public static void PropertyText(string label, string value, string? tooltip = null, Vector4? color = null)
    {
        Property(label);
        ImGui.AlignTextToFramePadding();
        if (color is { } c)
            ImGui.TextColored(c, value);
        else
            ImGui.TextUnformatted(value);
        if (tooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>Clickable blue text (e.g. another object to select).</summary>
    public static bool Link(string label, string? tooltip = null)
    {
        ImGui.AlignTextToFramePadding();
        bool clicked = ImGui.TextLink(label);
        if (tooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Width of each of <paramref name="count"/> fields sharing the rest of the line.</summary>
    public static float SplitWidth(int count) =>
        (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemInnerSpacing.X * (count - 1)) / count;

    /// <summary>Number field with a short coloured mark inside its left edge (red X, green Y, blue Z). Returns true while it changes.</summary>
    public static bool AxisField(string id, ref float value, Vector4 axisColor, float width, float speed, string format, float min = 0f, float max = 0f)
    {
        ImGui.SetNextItemWidth(width);
        bool changed = ImGui.DragFloat(id, ref value, speed, min, max, format);
        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(itemMin.X + 4f, itemMin.Y + 6f), new Vector2(itemMin.X + 6f, itemMax.Y - 6f),
            Col(axisColor), 1f);
        return changed;
    }

    /// <summary>Text in the small font and a faint colour (counts, hints).</summary>
    public static void Hint(string text, bool wrap = false)
    {
        ImGui.PushFont(UiTheme.SmallFont);
        ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.TextFaint);
        if (wrap)
            ImGui.TextWrapped(text);
        else
            ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    /// <summary>The text, shortened with "…" if wider than <paramref name="width"/> pixels.</summary>
    public static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + "…").X <= width) low = middle; else high = middle - 1;
        }
        return text[..low] + "…";
    }

    /// <summary>
    /// "12 345": thousands separated by a plain space. The French culture uses a narrow no-break space,
    /// which the default ImGui font cannot display.
    /// </summary>
    public static string Count(long value) =>
        value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    public static Vector2 Snap(Vector2 p) => new(MathF.Floor(p.X), MathF.Floor(p.Y));
}
