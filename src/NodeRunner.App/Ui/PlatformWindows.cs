using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.Render;

namespace NodeRunner.App.Ui;

// Dear ImGui multi-viewports: a panel dragged out of the main window becomes a real Windows window (second screen).
// ImGui coordinates stay those of the main 3D surface (0, 0 = its top-left corner, in points), so the rest of the
// interface is unchanged; the extra windows are placed from them.
internal static unsafe class PlatformWindows
{
    private static Form? _owner;
    private static Control? _surface;
    private static ImGuiInput? _input;
    private static GraphicsDevice? _graphics;
    private static ImGuiRenderer? _renderer;
    private static Point _origin;
    private static float _scale;
    private static bool _monitorsChanged = true;

    public static bool Enabled { get; private set; }

    public static void Initialize(Form owner, Control surface, ImGuiInput input, GraphicsDevice graphics, ImGuiRenderer renderer)
    {
        (_owner, _surface, _input, _graphics, _renderer) = (owner, surface, input, graphics, renderer);
        Enabled = true;
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
        io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports;
        io.ConfigViewportsNoTaskBarIcon = true;

        var platform = ImGui.GetPlatformIO();
        platform.Platform_CreateWindow = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void>)&CreateWindow;
        platform.Platform_DestroyWindow = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void>)&DestroyWindow;
        platform.Platform_ShowWindow = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void>)&ShowWindow;
        platform.Platform_SetWindowPos = (IntPtr)(delegate* unmanaged<ImGuiViewport*, Vector2, void>)&SetWindowPos;
        platform.Platform_GetWindowPos = (IntPtr)(delegate* unmanaged<ImGuiViewport*, Vector2>)&GetWindowPos;
        platform.Platform_SetWindowSize = (IntPtr)(delegate* unmanaged<ImGuiViewport*, Vector2, void>)&SetWindowSize;
        platform.Platform_GetWindowSize = (IntPtr)(delegate* unmanaged<ImGuiViewport*, Vector2>)&GetWindowSize;
        platform.Platform_SetWindowFocus = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void>)&SetWindowFocus;
        platform.Platform_GetWindowFocus = (IntPtr)(delegate* unmanaged<ImGuiViewport*, byte>)&GetWindowFocus;
        platform.Platform_GetWindowMinimized = (IntPtr)(delegate* unmanaged<ImGuiViewport*, byte>)&GetWindowMinimized;
        platform.Platform_SetWindowTitle = (IntPtr)(delegate* unmanaged<ImGuiViewport*, byte*, void>)&SetWindowTitle;
        platform.Renderer_CreateWindow = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void>)&RendererCreateWindow;
        platform.Renderer_DestroyWindow = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void>)&RendererDestroyWindow;
        platform.Renderer_SetWindowSize = (IntPtr)(delegate* unmanaged<ImGuiViewport*, Vector2, void>)&RendererSetWindowSize;
        platform.Renderer_RenderWindow = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void*, void>)&RendererRenderWindow;
        platform.Renderer_SwapBuffers = (IntPtr)(delegate* unmanaged<ImGuiViewport*, void*, void>)&RendererSwapBuffers;

        ImGui.GetMainViewport().NativePtr->PlatformHandle = (void*)owner.Handle;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        UpdateMonitors();
    }

    private static void OnDisplaySettingsChanged(object? sender, EventArgs e) => _monitorsChanged = true;

    // Before ImGui.NewFrame: the main window moved = the extra windows did not (their position is read again).
    public static void NewFrame()
    {
        if (!Enabled)
            return;
        var origin = _surface!.PointToScreen(Point.Empty);
        if (origin != _origin || _scale != UiTheme.Scale || _monitorsChanged)
        {
            bool moved = origin != _origin || _scale != UiTheme.Scale;
            _origin = origin;
            _scale = UiTheme.Scale;
            UpdateMonitors();
            if (moved)
            {
                var viewports = ImGui.GetPlatformIO().Viewports;
                for (int i = 1; i < viewports.Size; i++)
                    viewports[i].PlatformRequestMove = true;
            }
        }
    }

    // After the main window's interface is drawn.
    public static void RenderWindows()
    {
        if (!Enabled)
            return;
        ImGui.UpdatePlatformWindows();
        ImGui.RenderPlatformWindowsDefault();
    }

    public static void Shutdown()
    {
        if (!Enabled)
            return;
        Enabled = false;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        ImGui.DestroyPlatformWindows();
        ref var monitors = ref ImGui.GetPlatformIO().NativePtr->Monitors;
        if (monitors.Data != IntPtr.Zero)
            ImGui.MemFree(monitors.Data);
        monitors = default;
    }

    private static void UpdateMonitors()
    {
        _monitorsChanged = false;
        _origin = _surface!.PointToScreen(Point.Empty);
        _scale = UiTheme.Scale;
        var screens = Screen.AllScreens;
        ref var monitors = ref ImGui.GetPlatformIO().NativePtr->Monitors;
        if (monitors.Data != IntPtr.Zero)
            ImGui.MemFree(monitors.Data);
        var data = (ImGuiPlatformMonitor*)ImGui.MemAlloc((uint)(screens.Length * sizeof(ImGuiPlatformMonitor)));
        for (int i = 0; i < screens.Length; i++)
        {
            var (bounds, work) = (screens[i].Bounds, screens[i].WorkingArea);
            data[i] = new ImGuiPlatformMonitor
            {
                MainPos = ToImGui(bounds.Location),
                MainSize = new Vector2(bounds.Width, bounds.Height) / _scale,
                WorkPos = ToImGui(work.Location),
                WorkSize = new Vector2(work.Width, work.Height) / _scale,
                DpiScale = 1f,
            };
        }
        monitors = new ImVector(screens.Length, screens.Length, (IntPtr)data);
    }

    private static Vector2 ToImGui(Point screen) => new Vector2(screen.X - _origin.X, screen.Y - _origin.Y) / _scale;

    private static Point ToScreen(Vector2 pos) =>
        new(_origin.X + (int)MathF.Round(pos.X * _scale), _origin.Y + (int)MathF.Round(pos.Y * _scale));

    private static Size ToPixels(Vector2 size) =>
        new(Math.Max(1, (int)MathF.Round(size.X * _scale)), Math.Max(1, (int)MathF.Round(size.Y * _scale)));

    private static bool IsMain(ImGuiViewport* viewport) => viewport == ImGui.GetMainViewport().NativePtr;

    // Keyed by viewport id: ImGui may still ask about a viewport whose window is already gone.
    private static readonly Dictionary<uint, ViewportForm> Forms = new();

    private static ViewportForm? FormOf(ImGuiViewport* viewport) =>
        !IsMain(viewport) && Forms.TryGetValue(viewport->ID, out var form) && !form.IsDisposed ? form : null;

    // An exception must not leave a callback called by ImGui (the program would close at once).
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Diagnostics.ErrorLog.Add(L("Fenêtres détachées"), exception.Message);
        }
    }

    [UnmanagedCallersOnly]
    private static void CreateWindow(ImGuiViewport* viewport)
    {
        var flags = viewport->Flags;
        var pos = viewport->Pos;
        var size = viewport->Size;
        var form = new ViewportForm(viewport->ID, flags)
        {
            StartPosition = FormStartPosition.Manual,
            Location = ToScreen(pos),
            ClientSize = ToPixels(size),
        };
        form.Owner = _owner;
        _input!.Attach(form.Surface, _surface);
        _ = form.Handle;
        Forms[viewport->ID] = form;
        viewport->PlatformUserData = (void*)(IntPtr)1;
        viewport->PlatformHandle = (void*)form.Handle;
    }

    [UnmanagedCallersOnly]
    private static void DestroyWindow(ImGuiViewport* viewport)
    {
        if (IsMain(viewport) || !Forms.Remove(viewport->ID, out var form))
            return;
        viewport->PlatformUserData = null;
        viewport->PlatformHandle = null;
        Guard(() =>
            {
                // The main window gets the keyboard back when the window that had it goes.
                form.SwapChain?.Dispose();
                form.SwapChain = null;
                bool hadFocus = form.ContainsFocus;
                form.Dispose();
                if (hadFocus)
                    _surface?.Focus();
            });
    }

    [UnmanagedCallersOnly]
    private static void ShowWindow(ImGuiViewport* viewport)
    {
        if (FormOf(viewport) is { } form)
            Guard(form.Show);
    }

    [UnmanagedCallersOnly]
    private static void SetWindowPos(ImGuiViewport* viewport, Vector2 pos)
    {
        if (FormOf(viewport) is { } form)
            Guard(() => form.MoveTo(ToScreen(pos)));
    }

    [UnmanagedCallersOnly]
    private static Vector2 GetWindowPos(ImGuiViewport* viewport) =>
        FormOf(viewport) is { } form ? ToImGui(form.Location) : IsMain(viewport) ? Vector2.Zero : viewport->Pos;

    [UnmanagedCallersOnly]
    private static void SetWindowSize(ImGuiViewport* viewport, Vector2 size)
    {
        if (FormOf(viewport) is { } form)
            Guard(() => form.ResizeTo(ToPixels(size)));
    }

    [UnmanagedCallersOnly]
    private static Vector2 GetWindowSize(ImGuiViewport* viewport)
    {
        if (IsMain(viewport))
            return new Vector2(_surface!.ClientSize.Width, _surface.ClientSize.Height) / _scale;
        return FormOf(viewport) is { IsDisposed: false } form
            ? new Vector2(form.ClientSize.Width, form.ClientSize.Height) / _scale
            : viewport->Size;
    }

    [UnmanagedCallersOnly]
    private static void SetWindowFocus(ImGuiViewport* viewport)
    {
        if (IsMain(viewport))
            Guard(() => _owner!.Activate());
        else if (FormOf(viewport) is { } form)
            Guard(() =>
            {
                form.Activate();
                form.Surface.Focus();
            });
    }

    [UnmanagedCallersOnly]
    private static byte GetWindowFocus(ImGuiViewport* viewport)
    {
        Form? form = IsMain(viewport) ? _owner : FormOf(viewport);
        return (byte)(form is not null && Form.ActiveForm == form ? 1 : 0);
    }

    [UnmanagedCallersOnly]
    private static byte GetWindowMinimized(ImGuiViewport* viewport)
    {
        Form? form = IsMain(viewport) ? _owner : FormOf(viewport);
        return (byte)(form is { WindowState: FormWindowState.Minimized } ? 1 : 0);
    }

    [UnmanagedCallersOnly]
    private static void SetWindowTitle(ImGuiViewport* viewport, byte* title)
    {
        if (FormOf(viewport) is { } form)
        {
            string text = Marshal.PtrToStringUTF8((IntPtr)title) ?? "";
            int id = text.IndexOf("##", StringComparison.Ordinal);
            Guard(() => form.Text = (id >= 0 ? text[..id] : text).Trim());
        }
    }

    [UnmanagedCallersOnly]
    private static void RendererCreateWindow(ImGuiViewport* viewport)
    {
        if (FormOf(viewport) is not { } form)
            return;
        var size = form.Surface.ClientSize;
        Guard(() => form.SwapChain = _graphics!.CreateWindowSwapChain(form.Surface.Handle, size.Width, size.Height));
    }

    [UnmanagedCallersOnly]
    private static void RendererDestroyWindow(ImGuiViewport* viewport)
    {
        if (FormOf(viewport) is { } form)
        {
            form.SwapChain?.Dispose();
            form.SwapChain = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void RendererSetWindowSize(ImGuiViewport* viewport, Vector2 size)
    {
        if (FormOf(viewport) is { SwapChain: { } swapChain })
        {
            var pixels = ToPixels(size);
            Guard(() => swapChain.Resize(pixels.Width, pixels.Height));
        }
    }

    [UnmanagedCallersOnly]
    private static void RendererRenderWindow(ImGuiViewport* viewport, void* argument)
    {
        if (FormOf(viewport) is not { SwapChain: { } swapChain })
            return;
        var drawData = new ImDrawDataPtr(viewport->DrawData);
        var background = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        background.W = 1f;
        Guard(() =>
        {
            swapChain.Begin(background);
            _renderer!.Render(drawData);
        });
    }

    [UnmanagedCallersOnly]
    private static void RendererSwapBuffers(ImGuiViewport* viewport, void* argument)
    {
        if (FormOf(viewport) is { SwapChain: { } swapChain })
            Guard(swapChain.Present);
    }

    // Borderless window holding a detached panel; ImGui draws its title bar.
    private sealed class ViewportForm : Form
    {
        private readonly ImGuiViewportFlags _flags;
        private bool _placing;

        public uint ViewportId { get; }
        public RenderSurface Surface { get; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public WindowSwapChain? SwapChain { get; set; }

        public ViewportForm(uint viewportId, ImGuiViewportFlags flags)
        {
            ViewportId = viewportId;
            _flags = flags;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(26, 28, 33);
            TopMost = (flags & ImGuiViewportFlags.TopMost) != 0;
            Surface = new RenderSurface { Dock = DockStyle.Fill, IsWindowFrame = _ => NoInputs };
            Controls.Add(Surface);
            Move += (_, _) => Request(v => v->PlatformRequestMove = 1);
            Resize += (_, _) => Request(v => v->PlatformRequestResize = 1);
        }

        protected override bool ShowWithoutActivation => (_flags & ImGuiViewportFlags.NoFocusOnAppearing) != 0;

        public void MoveTo(Point location)
        {
            _placing = true;
            Location = location;
            _placing = false;
        }

        public void ResizeTo(Size size)
        {
            _placing = true;
            ClientSize = size;
            _placing = false;
        }

        // Tooltips and windows being dragged: the mouse goes to the window below.
        private bool NoInputs
        {
            get
            {
                if (ImGui.GetCurrentContext() == IntPtr.Zero)
                    return false;
                var viewport = ImGui.FindViewportByID(ViewportId);
                return viewport.NativePtr != null && (viewport.Flags & ImGuiViewportFlags.NoInputs) != 0;
            }
        }

        private delegate void ViewportChange(ImGuiViewport* viewport);

        // Windows moved or resized the window itself (Win + arrows, screen removed): ImGui reads it again.
        private void Request(ViewportChange change)
        {
            if (_placing || ImGui.GetCurrentContext() == IntPtr.Zero)
                return;
            var viewport = ImGui.FindViewportByID(ViewportId);
            if (viewport.NativePtr != null)
                change(viewport.NativePtr);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Alt+F4: ImGui closes the panel itself.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Request(v => v->PlatformRequestClose = 1);
            }
            base.OnFormClosing(e);
        }

        private const int WmNcHitTest = 0x84, WmDpiChanged = 0x02E0, HtTransparent = -1;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest && NoInputs)
            {
                m.Result = HtTransparent; // Tooltips: the mouse goes through.
                return;
            }
            if (m.Msg == WmDpiChanged)
            {
                m.Result = IntPtr.Zero; // ImGui keeps the size it chose (one interface scale for every screen).
                return;
            }
            base.WndProc(ref m);
        }
    }
}
