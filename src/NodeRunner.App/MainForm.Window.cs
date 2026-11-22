using System.Runtime.InteropServices;
using NodeRunner.App.Ui;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
    // --- Own title bar (see WindowChrome): the Windows caption is removed, the menu bar takes its place ---

    private const int WmNcCalcSize = 0x83;
    private const int WmNcHitTest = 0x84;
    private const int WmNcMouseMove = 0xA0;
    private const int WmNcLButtonDown = 0xA1;
    private const int WmNcLButtonUp = 0xA2;
    private const int WmNcLButtonDblClk = 0xA3;
    private const int WmNcMouseLeave = 0x2A2;
    private const int WmMouseMove = 0x200;
    private const int WmSysCommand = 0x112;
    private const int ScKeyMenu = 0xF100;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtMaxButton = 9;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcCalcSize when m.WParam != IntPtr.Zero:
            {
                // Keep the sizing borders Windows computes on the sides and at the bottom, but no caption at the top.
                var before = Marshal.PtrToStructure<NativeRect>(m.LParam);
                base.WndProc(ref m);
                var after = Marshal.PtrToStructure<NativeRect>(m.LParam);
                after.Top = before.Top + (IsZoomed(Handle) ? FrameThickness() : 0); // Maximised: the frame is off-screen.
                Marshal.StructureToPtr(after, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            case WmNcHitTest:
            {
                base.WndProc(ref m);
                if ((int)m.Result == HtClient)
                    m.Result = FrameHitTest(PointFromLParam(m.LParam));
                return;
            }
            case WmNcMouseMove:
                SetMaximizeHover((int)m.WParam == HtMaxButton);
                break;
            case WmNcMouseLeave:
                SetMaximizeHover(false);
                break;
            case WmNcLButtonDown when (int)m.WParam == HtMaxButton:
            case WmNcLButtonDblClk when (int)m.WParam == HtMaxButton:
                WindowChrome.MaximizePressed = true;
                m.Result = IntPtr.Zero;
                return; // Not to Windows: it would draw its own old button.
            case WmNcLButtonUp when (int)m.WParam == HtMaxButton:
                if (WindowChrome.MaximizePressed)
                    WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                WindowChrome.MaximizePressed = false;
                m.Result = IntPtr.Zero;
                return;
            case WmNcLButtonUp:
            case WmMouseMove:
                WindowChrome.MaximizePressed = false;
                break;
            // Alt walks in play mode: released alone, it would open the window menu and keep the next keys.
            case WmSysCommand when ((long)m.WParam & 0xFFF0) == ScKeyMenu && _state.Play.IsActive:
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private void SetMaximizeHover(bool hovered)
    {
        WindowChrome.MaximizeHovered = hovered;
        if (!hovered)
            WindowChrome.MaximizePressed = false;
    }

    private IntPtr FrameHitTest(Point screen)
    {
        var p = PointToClient(screen);
        int edge = FrameThickness();
        if (!IsZoomed(Handle) && p.Y < edge)
            return p.X < edge * 2 ? HtTopLeft : p.X >= ClientSize.Width - edge * 2 ? HtTopRight : HtTop;
        // Play mode: no title bar is drawn, the whole window is the 3D view.
        if (_state.Play.IsActive)
            return HtClient;
        return WindowChrome.HitTest(new PointF(p.X / UiTheme.Scale, p.Y / UiTheme.Scale)) switch
        {
            WindowChrome.Part.MaximizeButton => HtMaxButton,
            WindowChrome.Part.Caption => HtCaption,
            _ => HtClient,
        };
    }

    private bool IsFramePoint(Point screen) => (int)FrameHitTest(screen) != HtClient;

    private static Point PointFromLParam(IntPtr lParam) =>
        new(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));

    private int FrameThickness()
    {
        uint dpi = GetDpiForWindow(Handle);
        return GetSystemMetricsForDpi(SmCyFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
    }

    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // --- Win32: dark title bar (Windows 10 2004+; caption colours: Windows 11, ignored elsewhere) ---

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private void UseDarkTitleBar()
    {
        SetWindowAttribute(DwmUseImmersiveDarkMode, 1);
        // Same colour as the menu bar, so that both look like one band.
        SetWindowAttribute(DwmCaptionColor, UiTheme.ColorRef(UiTheme.Chrome));
        SetWindowAttribute(DwmBorderColor, UiTheme.ColorRef(UiTheme.Chrome));
        SetWindowAttribute(DwmTextColor, UiTheme.ColorRef(UiTheme.TextMuted));
    }

    // --- Win32: Windows 11 backdrop (Mica) behind the interface (22H2+, see WindowAlpha) ---

    public const float MaxTransparency = 0.5f;

    public const float MinMouseSensitivity = 0.2f;
    public const float MaxMouseSensitivity = 3f;

    public const float MinFieldOfView = 30f;
    public const float MaxFieldOfView = 110f;

    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    private const int DwmSystemBackdropType = 38;
    private const int BackdropNone = 1;
    private const int BackdropMica = 2;

    private void ShowBackdrop(bool shown)
    {
        if (_backdropShown == shown)
            return;
        _backdropShown = shown;
        SetWindowAttribute(DwmSystemBackdropType, shown ? BackdropMica : BackdropNone);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    private void SetWindowAttribute(int attribute, int value) =>
        _ = DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int));

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    // --- Win32: is the message queue empty? ---

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    private static bool IsApplicationIdle() => !PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
}
