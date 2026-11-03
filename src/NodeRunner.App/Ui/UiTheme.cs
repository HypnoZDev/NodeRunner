using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// NodeRunner's look: graphite greys with a slightly cool tint, soft text, light blue accent, borderless fields a step
// lighter than the panels, discreet rounding, sections separated by thin lines.
internal static unsafe class UiTheme
{
    public const float FontSize = 15f;
    public const float SmallFontSize = 13f;
    public const float TitleFontSize = 17f;

    // --- Palette ---

    public static readonly Vector4 Chrome = Rgb(0x111214);

    public static readonly Vector4 Panel = Rgb(0x1A1B1F);

    public static readonly Vector4 Border = new(1f, 1f, 1f, 0.08f);

    public static readonly Vector4 Field = Rgb(0x25262B);
    public static readonly Vector4 FieldHovered = Rgb(0x2C2D33);
    public static readonly Vector4 FieldActive = Rgb(0x32343A);

    public static readonly Vector4 Divider = new(1f, 1f, 1f, 0.06f);

    public static readonly Vector4 Text = Rgb(0xE4E5E9);
    public static readonly Vector4 TextMuted = Rgb(0x9A9CA5);
    public static readonly Vector4 TextFaint = Rgb(0x666870);

    public static readonly Vector4 Accent = Rgb(0x4C8DFF);
    public static readonly Vector4 AccentHovered = Rgb(0x6AA1FF);
    public static readonly Vector4 AccentText = Rgb(0x8AB6FF);
    public static readonly Vector4 Success = Rgb(0x5BD08A);
    public static readonly Vector4 Warning = Rgb(0xF2B657);
    public static readonly Vector4 Error = Rgb(0xF27474);

    public static readonly Vector4 AxisX = Rgb(0xE5534B);
    public static readonly Vector4 AxisY = Rgb(0x6CC36C);
    public static readonly Vector4 AxisZ = Rgb(0x4C8DFF);
    public static readonly Vector4 AxisNeutral = Rgb(0x8A8C94);

    public const float FrameRounding = 4f;

    public static ImFontPtr SemiboldFont { get; private set; }

    public static ImFontPtr TitleFont { get; private set; }

    public static ImFontPtr BrandFont { get; private set; }

    public static ImFontPtr SmallFont { get; private set; }

    // Characters drawn by the interface: Latin-1 and Latin Extended-A (é, œ...), general punctuation
    // (’ « » … – and the narrow no-break space), arrows. Zero-terminated pairs, kept alive for the font atlas.
    private static readonly ushort* GlyphRanges = CreateGlyphRanges(
        0x0020, 0x00FF, 0x0100, 0x017F, 0x2000, 0x206F, 0x20AC, 0x20AC, 0x2190, 0x21FF);

    public static void LoadFonts(ImGuiIOPtr io)
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string regular = Path.Combine(fonts, "segoeui.ttf");
        string semibold = Path.Combine(fonts, "seguisb.ttf");
        if (!File.Exists(semibold))
            semibold = Path.Combine(fonts, "segoeuib.ttf");

        var main = File.Exists(regular)
            ? io.Fonts.AddFontFromFileTTF(regular, FontSize, null, (IntPtr)GlyphRanges)
            : io.Fonts.AddFontDefault();
        MergeIcons(io, IconSize, IconOffsetY);
        SemiboldFont = AddFont(io, semibold, FontSize, main);
        MergeIcons(io, IconSize, IconOffsetY);
        TitleFont = AddFont(io, semibold, TitleFontSize, main);
        SmallFont = AddFont(io, regular, SmallFontSize, main);
        MergeIcons(io, SmallIconSize, SmallIconOffsetY);
        BrandFont = AddFont(io, Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "unbounded-bold.ttf"), BrandFontSize, SemiboldFont);
    }

    private const float BrandFontSize = 17f;

    private static ImFontPtr AddFont(ImGuiIOPtr io, string path, float size, ImFontPtr fallback) =>
        File.Exists(path) ? io.Fonts.AddFontFromFileTTF(path, size, null, (IntPtr)GlyphRanges) : fallback;

    private static void MergeIcons(ImGuiIOPtr io, float size, float offsetY)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "lucide.ttf");
        if (!File.Exists(path))
            return; // Icons then show as "?": the interface still works.

        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        config.MergeMode = true;
        config.PixelSnapH = true;
        config.GlyphMinAdvanceX = size; // Same width for every icon, so they line up.
        config.GlyphOffset = new Vector2(0, offsetY);
        io.Fonts.AddFontFromFileTTF(path, size, config, (IntPtr)IconRanges);
        config.Destroy(); // The atlas keeps its own copy.
    }

    private static readonly ushort* IconRanges =
        CreateGlyphRanges(Icons.All().Distinct().Order().SelectMany(c => new[] { (ushort)c, (ushort)c }).ToArray());

    private const float IconSize = 16f;
    private const float IconOffsetY = 2f;
    private const float SmallIconSize = 14f;
    private const float SmallIconOffsetY = 2f;

    public static void Apply()
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();

        style.WindowPadding = new Vector2(10, 10);
        style.FramePadding = new Vector2(7, 4);
        style.CellPadding = new Vector2(6, 2);
        style.ItemSpacing = new Vector2(8, 5);
        style.ItemInnerSpacing = new Vector2(5, 4);
        style.IndentSpacing = 16;
        style.ScrollbarSize = 8;
        style.GrabMinSize = 10;

        style.WindowBorderSize = 1;
        style.ChildBorderSize = 0;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 0;
        style.TabBorderSize = 0;
        style.TabBarBorderSize = 1;
        style.SeparatorTextBorderSize = 1;
        style.DockingSeparatorSize = 2;

        style.WindowRounding = 6;
        style.ChildRounding = 4;
        style.FrameRounding = FrameRounding;
        style.PopupRounding = 6;
        style.ScrollbarRounding = 6;
        style.GrabRounding = 3;
        style.TabRounding = 4;

        style.WindowTitleAlign = new Vector2(0f, 0.5f);
        style.WindowMenuButtonPosition = ImGuiDir.None;
        style.SelectableTextAlign = new Vector2(0f, 0.5f);

        var colors = style.Colors;
        void Set(ImGuiCol col, Vector4 color, float alpha = 1f) => colors[(int)col] = color with { W = color.W * alpha };
        var white = new Vector4(1f, 1f, 1f, 1f);
        var black = new Vector4(0f, 0f, 0f, 1f);

        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, TextFaint);
        Set(ImGuiCol.WindowBg, Panel);
        Set(ImGuiCol.ChildBg, black, 0f);
        Set(ImGuiCol.PopupBg, Rgb(0x1F2025), 0.99f);
        Set(ImGuiCol.Border, Border);
        Set(ImGuiCol.BorderShadow, black, 0f);

        Set(ImGuiCol.FrameBg, Field);
        Set(ImGuiCol.FrameBgHovered, FieldHovered);
        Set(ImGuiCol.FrameBgActive, FieldActive);

        Set(ImGuiCol.TitleBg, Chrome);
        Set(ImGuiCol.TitleBgActive, Chrome);
        Set(ImGuiCol.TitleBgCollapsed, Chrome);
        Set(ImGuiCol.MenuBarBg, Chrome);

        Set(ImGuiCol.ScrollbarBg, black, 0f);
        Set(ImGuiCol.ScrollbarGrab, white, 0.10f);
        Set(ImGuiCol.ScrollbarGrabHovered, white, 0.18f);
        Set(ImGuiCol.ScrollbarGrabActive, white, 0.24f);

        Set(ImGuiCol.CheckMark, Accent);
        // Grey slider handle (a blue one hides the value written under it), blue while dragged.
        Set(ImGuiCol.SliderGrab, Rgb(0x5C5F69));
        Set(ImGuiCol.SliderGrabActive, Accent);

        Set(ImGuiCol.Button, Field);
        Set(ImGuiCol.ButtonHovered, FieldHovered);
        Set(ImGuiCol.ButtonActive, FieldActive);

        // Selected rows: soft blue; hovered rows and menu items: faint white.
        Set(ImGuiCol.Header, Accent, 0.24f);
        Set(ImGuiCol.HeaderHovered, white, 0.06f);
        Set(ImGuiCol.HeaderActive, Accent, 0.32f);

        // Separators are also the gaps between the docked panels: as dark as the bars.
        Set(ImGuiCol.Separator, Chrome);
        Set(ImGuiCol.SeparatorHovered, Accent, 0.70f);
        Set(ImGuiCol.SeparatorActive, Accent);

        Set(ImGuiCol.ResizeGrip, black, 0f);
        Set(ImGuiCol.ResizeGripHovered, Accent, 0.50f);
        Set(ImGuiCol.ResizeGripActive, Accent, 0.90f);

        // Title tabs of the docked panels: the panel's own colour, a blue line on top of the focused one.
        Set(ImGuiCol.Tab, Chrome);
        Set(ImGuiCol.TabHovered, FieldHovered);
        Set(ImGuiCol.TabSelected, Panel);
        Set(ImGuiCol.TabSelectedOverline, Accent);
        Set(ImGuiCol.TabDimmed, Chrome);
        Set(ImGuiCol.TabDimmedSelected, Panel);
        Set(ImGuiCol.TabDimmedSelectedOverline, black, 0f);

        Set(ImGuiCol.DockingPreview, Accent, 0.40f);
        Set(ImGuiCol.DockingEmptyBg, Chrome);

        Set(ImGuiCol.TableHeaderBg, black, 0f);
        Set(ImGuiCol.TableBorderStrong, Divider);
        Set(ImGuiCol.TableBorderLight, Divider);
        Set(ImGuiCol.TableRowBg, black, 0f);
        Set(ImGuiCol.TableRowBgAlt, white, 0.015f);

        Set(ImGuiCol.TextSelectedBg, Accent, 0.40f);
        Set(ImGuiCol.TextLink, AccentText);
        Set(ImGuiCol.DragDropTarget, Accent);
        Set(ImGuiCol.NavCursor, Accent);
        Set(ImGuiCol.ModalWindowDimBg, black, 0.45f);
    }

    public static Vector4 Rgb(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // The same colour as a Windows COLORREF (0x00BBGGRR), e.g. for the title bar.
    public static int ColorRef(Vector4 color) =>
        (int)(color.X * 255f + 0.5f) | (int)(color.Y * 255f + 0.5f) << 8 | (int)(color.Z * 255f + 0.5f) << 16;

    private static ushort* CreateGlyphRanges(params ushort[] ranges)
    {
        var memory = (ushort*)NativeMemory.Alloc((nuint)(ranges.Length + 1), sizeof(ushort));
        for (int i = 0; i < ranges.Length; i++)
            memory[i] = ranges[i];
        memory[ranges.Length] = 0;
        return memory;
    }
}
