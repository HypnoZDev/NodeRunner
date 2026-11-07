using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using NodeRunner.App.Configuration;
using NodeRunner.App.Input;
using NodeRunner.App.Ui;
using NodeRunner.App.World;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

// Main window: hosts the DirectX surface and runs the render loop.
internal sealed unsafe partial class MainForm : Form
{
    private static readonly Vector4 BackgroundColor = new(0.10f, 0.11f, 0.13f, 1f);

    private readonly AppState _state;
    private readonly RenderSurface _surface;
    private readonly ImGuiInput _input = new();
    private readonly CameraController _cameraController = new();
    private readonly MainUi _ui;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private GraphicsDevice? _graphics;
    private ImGuiRenderer? _imguiRenderer;
    private ColorRenderer? _lineRenderer;
    private ModelRenderer? _modelRenderer;
    private SkyRenderer? _skyRenderer;
    private WaterRenderer? _waterRenderer;
    private ShadowMaps? _shadowMaps;
    private object? _waterShown; // The WaterMap uploaded to _waterRenderer.
    private PickBuffer? _pickBuffer;
    private (Point Location, bool Control)? _pickRequest;

    // Collision tool: left button pressed at this point (a drag draws a selection rectangle), and the click or
    // rectangle to select at the next frame.
    private Point? _collisionDragStart;
    private Point _collisionDragCurrent;
    private (Point A, Point B, bool Control)? _collisionPickRequest;
    private readonly PathNodeLayer _pathNodeLayer = new();
    private readonly ScenarioLayer _scenarioLayer = new();
    private readonly NavMeshLayer _navMeshLayer = new();
    private readonly CollisionLayer _collisionLayer = new();
    private readonly EntityBoxLayer _entityBoxLayer = new();
    private readonly RoomLayer _roomLayer = new();
    private readonly OcclusionLayer _occlusionLayer = new();
    private readonly OcclusionCuller _occlusionCuller = new();
    private readonly ModelLayer _modelLayer = new();
    private WindowAlpha? _windowAlpha;
    private readonly bool _backdrop;
    private bool? _backdropShown;
    private IntPtr _imguiContext;
    private double _lastFrameTime;

    public MainForm(AppState state)
    {
        _state = state;
        _ui = new MainUi(state, _cameraController, _pathNodeLayer, _scenarioLayer, _navMeshLayer, _collisionLayer, _entityBoxLayer, _roomLayer, _occlusionLayer, _occlusionCuller, _modelLayer);

        Text = Program.Name; // Credit and version are in the menu bar and the About window.
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        float scale = DeviceDpi / 96f;
        var screen = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1920, 1080);
        ClientSize = new Size(Math.Min((int)(1600 * scale), screen.Width * 9 / 10), Math.Min((int)(900 * scale), screen.Height * 9 / 10));
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size((int)(640 * scale), (int)(360 * scale));
        BackColor = Color.FromArgb(0x11, 0x12, 0x14); // Shown for an instant while resizing: colour of the menu bar.

        _backdrop = state.Settings.Transparency > 0f && BackdropSupported;
        _surface = new RenderSurface { Dock = DockStyle.Fill, IsWindowFrame = IsFramePoint };
        Controls.Add(_surface);
    }

    // No GDI surface: the window shows only DirectX's image and, through its transparent pixels, the backdrop.
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (_backdrop)
                parameters.ExStyle |= WsExNoRedirectionBitmap;
            return parameters;
        }
    }

    private const int WsExNoRedirectionBitmap = 0x00200000;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UseDarkTitleBar();
        if (_backdrop)
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(Handle, ref margins);
        }
        // Recompute the frame now that WM_NCCALCSIZE removes the Windows caption.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    // --- Own title bar (see WindowChrome): the Windows caption is removed, the menu bar takes its place ---

    private const int WmNcCalcSize = 0x83;
    private const int WmNcHitTest = 0x84;
    private const int WmNcMouseMove = 0xA0;
    private const int WmNcLButtonDown = 0xA1;
    private const int WmNcLButtonUp = 0xA2;
    private const int WmNcLButtonDblClk = 0xA3;
    private const int WmNcMouseLeave = 0x2A2;
    private const int WmMouseMove = 0x200;
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

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        InitializeGraphics();
        _graphics!.Resize(_surface.ClientSize.Width, _surface.ClientSize.Height);

        _input.Attach(_surface);
        _cameraController.Attach(_surface);
        AttachSelectionInput();
        _surface.Focus();

        Application.Idle += OnApplicationIdle;
    }

    // Called by the start-up screen once the map is loaded, before this window is shown: prepares the GPU and loads
    // the 3D models around the camera, so the window opens on a finished view.
    public float PreloadModels()
    {
        if (_state.World.Maps is null || !_state.IsLayerVisible(ModelLayer.LayerName))
            return 1f;
        InitializeGraphics();
        var settings = _state.Settings;
        _modelLayer.Update(_state.World, _modelRenderer!, _state.Camera.Position, settings.ViewDistanceMultiplier,
            (int)settings.TimeOfDay % 24, enabled: true);
        return _modelLayer.PreloadProgress();
    }

    private void InitializeGraphics()
    {
        if (_graphics is not null)
            return;

        _imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiContext);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors; // See ImGuiInput.UpdateCursor (also enables resizing windows from their edges).
        // ImGui window layout is remembered next to settings.json.
        io.NativePtr->IniFilename = (byte*)Marshal.StringToCoTaskMemUTF8(Path.Combine(_state.Paths.ConfigDirectory, "imgui.ini"));
        _ = Handle; // The window may not be shown yet (start-up screen): its handle comes before the 3D surface's.
        UiTheme.LoadFonts(io, DisplayScale());
        UiTheme.Apply();

        _graphics = new GraphicsDevice(_surface.Handle, _surface.ClientSize.Width, _surface.ClientSize.Height, _backdrop);
        if (_graphics.IsTransparent)
            _windowAlpha = new WindowAlpha(_graphics.Device, _graphics.Context);
        _state.TransparentWindow = _graphics.IsTransparent;
        _imguiRenderer = new ImGuiRenderer(_graphics.Device, _graphics.Context);
        _lineRenderer = new ColorRenderer(_graphics.Device, _graphics.Context);
        _modelRenderer = new ModelRenderer(_graphics.Device, _graphics.Context);
        _skyRenderer = new SkyRenderer(_graphics.Device, _graphics.Context);
        _waterRenderer = new WaterRenderer(_graphics.Device, _graphics.Context);
        _pickBuffer = new PickBuffer(_graphics.Device, _graphics.Context);
        var graphics = _graphics;
        // Most of the video memory for the models, the rest for the window, shadows and other layers.
        _modelLayer.MemoryBudget = () => graphics.VideoMemoryBudget() is > 0 and var budget ? budget * 7 / 10 : long.MaxValue;
        _state.AdapterName = _graphics.IsSoftware ? $"{_graphics.AdapterName} (rendu logiciel, lent)" : _graphics.AdapterName;
        if (_graphics.IsSoftware)
            Diagnostics.ErrorLog.Add("Carte graphique", "Aucune carte graphique compatible DirectX 11 : affichage en rendu logiciel (lent). "
                + "Installe ou mets à jour le pilote de ta carte graphique.");

        RestoreCamera();
        _surface.Resize += (_, _) => _graphics?.Resize(_surface.ClientSize.Width, _surface.ClientSize.Height);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.Idle -= OnApplicationIdle;
        SaveCamera();
        _state.World.Dispose();
        _pathNodeLayer.Dispose();
        _scenarioLayer.Dispose();
        _navMeshLayer.Dispose();
        _collisionLayer.Dispose();
        _entityBoxLayer.Dispose();
        _roomLayer.Dispose();
        _occlusionLayer.Dispose();
        DisposeCollisionPreview();
        _modelLayer.Dispose();
        _state.Viewer.Dispose();
        _modelRenderer?.Dispose();
        _modelRenderer = null;
        _skyRenderer?.Dispose();
        _skyRenderer = null;
        _waterRenderer?.Dispose();
        _waterRenderer = null;
        _shadowMaps?.Dispose();
        _shadowMaps = null;
        _pickBuffer?.Dispose();
        _pickBuffer = null;
        _windowAlpha?.Dispose();
        _windowAlpha = null;
        _lineRenderer?.Dispose();
        _lineRenderer = null;
        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _graphics?.Dispose();
        _graphics = null;
        if (_imguiContext != IntPtr.Zero)
        {
            ImGui.DestroyContext(_imguiContext);
            _imguiContext = IntPtr.Zero;
        }
        base.OnFormClosed(e);
    }

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        while (_graphics is not null && !IsDisposed && IsApplicationIdle())
            RenderFrame();
    }

    private void RenderFrame()
    {
        if (_graphics is null || _imguiRenderer is null || _lineRenderer is null)
            return;

        // Do not burn GPU when minimized, hidden or after the GPU was lost.
        if (_deviceLost || WindowState == FormWindowState.Minimized || _graphics.IsOccluded && _clock.Elapsed.TotalSeconds - _lastFrameTime < 0.25)
        {
            Thread.Sleep(50);
            return;
        }
        // In the background (another window has the focus): about 30 images per second are enough.
        if (ActiveForm is null && _clock.Elapsed.TotalSeconds - _lastFrameTime is var elapsed && elapsed < BackgroundFrameTime)
        {
            Thread.Sleep((int)Math.Ceiling((BackgroundFrameTime - elapsed) * 1000));
            return;
        }

        try
        {
            DrawFrame();
        }
        catch (SharpGen.Runtime.SharpGenException) when (_graphics?.IsLost == true)
        {
        }
        // The close button of the title bar disposes everything during DrawFrame.
        if (_graphics?.IsLost == true)
            OnDeviceLost();
    }

    private const double BackgroundFrameTime = 1.0 / 30;

    private bool _deviceLost;

    // The GPU was taken away: every GPU resource would have to be rebuilt, so the changes are saved and NodeRunner
    // starts again.
    private void OnDeviceLost()
    {
        if (_deviceLost)
            return;
        _deviceLost = true;
        string reason = _graphics!.LostReason;
        Diagnostics.ErrorLog.Add("Carte graphique", $"Carte graphique perdue : {reason}.");
        BeginInvoke(() => RestartAfterDeviceLost(reason));
    }

    private void RestartAfterDeviceLost(string reason)
    {
        string text = $"La carte graphique ne répond plus ({reason}).\n"
            + "Cela arrive quand son pilote plante, redémarre ou se met à jour. NodeRunner doit redémarrer pour réafficher la carte.";
        if (_state.History.HasUnsavedChanges && _state.PendingSaveCount > 0)
        {
            string count = _state.PendingSaveText;
            if (_state.Project is null)
            {
                // No window can be drawn anymore to name a project: the changes go into a new one.
                string name = $"Récupération {DateTime.Now:yyyy-MM-dd HH-mm}";
                string folder = Path.Combine(_state.ProjectsFolder, name);
                if (_state.CreateProject(folder, name) is { } error)
                    text += $"\n\nLes modifications ({count}) n'ont pas pu être enregistrées : {error}";
                else if (SaveMaps())
                    text += $"\n\nLes modifications ({count}) ont été enregistrées dans le nouveau projet « {name} » :\n{folder}";
                else
                    text += $"\n\nLes modifications ({count}) n'ont pas pu être enregistrées.";
            }
            else if (SaveMaps())
                text += $"\n\nLes modifications du projet « {_state.Project.Name} » ({count}) ont été enregistrées.";
            else
                text += $"\n\nLes modifications ({count}) n'ont pas pu être enregistrées.";
        }
        MessageBox.Show(this, text, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Restart();
    }

    private void DrawFrame()
    {
        var graphics = _graphics!;
        var imguiRenderer = _imguiRenderer!;
        double now = _clock.Elapsed.TotalSeconds;
        float deltaTime = (float)Math.Max(now - _lastFrameTime, 1e-5);
        var io = ImGui.GetIO();
        io.DeltaTime = deltaTime;
        UpdateDisplayScale(io);
        io.DisplaySize = new Vector2(graphics.Width, graphics.Height) / UiTheme.Scale;
        io.DisplayFramebufferScale = new Vector2(UiTheme.Scale);
        _lastFrameTime = now;

        // Cap the step so a long pause (dialog box, window drag) doesn't teleport the camera.
        _state.Camera.FieldOfView = Math.Clamp(_state.Settings.FieldOfView, MinFieldOfView, MaxFieldOfView) * MathF.PI / 180f;
        _cameraController.Sensitivity = Math.Clamp(_state.Settings.MouseSensitivity, MinMouseSensitivity, MaxMouseSensitivity);
        _cameraController.InvertY = _state.Settings.InvertMouseY;
        _cameraController.MovementKeys = _state.Settings.MovementKeys;
        _cameraController.Update(_state.Camera, Math.Min(deltaTime, 0.1f));

        ImGui.NewFrame();
        _ui.Draw();
        DrawCollisionRectangle();
        ImGui.Render();
        _input.UpdateCursor();

        // The viewer's image first (it binds its own target), for the interface drawn below.
        _state.Viewer.Render(_modelRenderer!, imguiRenderer, graphics);
        var scene = SceneViewport();
        PickClickedObject(scene);
        PickCollision(scene);
        PlaceRequestedObject(scene);
        graphics.BeginFrame(BackgroundColor);
        graphics.SetViewport(scene.X, scene.Y, scene.Width, scene.Height);
        DrawWorld(_lineRenderer!, scene);
        float opacity = 1f - Math.Clamp(_state.Settings.Transparency, 0f, MaxTransparency);
        _windowAlpha?.MakeOpaque(scene.X, scene.Y, scene.Width, scene.Height);
        imguiRenderer.Render(ImGui.GetDrawData());
        if (_windowAlpha is not null && opacity < 1f)
            _windowAlpha.Fade(graphics.Width, graphics.Height, scene.X, scene.Y, scene.Width, scene.Height, opacity);
        ShowBackdrop(_windowAlpha is not null && opacity < 1f);
        graphics.Present(_state.Settings.VSync);

        HandleUiRequests();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string title = _state.Project is { } project ? $"{project.Name} — {Program.Name}" : Program.Name;
        if (Text != title)
            Text = title;
    }

    private float DisplayScale() =>
        _state.Settings.UiScale > 0f ? Math.Clamp(_state.Settings.UiScale, 1f, 3f) : WindowsScale();

    private float WindowsScale() => Math.Max(1f, GetDpiForWindow(Handle) / 96f);

    // Interface size changed, or the window went to a screen with another scale: fonts are rasterised again.
    private void UpdateDisplayScale(ImGuiIOPtr io)
    {
        float scale = DisplayScale();
        if (scale == UiTheme.Scale)
            return;
        UiTheme.LoadFonts(io, scale);
        _imguiRenderer!.RebuildFontTexture();
    }

    private Rectangle SceneViewport()
    {
        var (position, size) = DockLayout.SceneArea;
        position *= UiTheme.Scale;
        size *= UiTheme.Scale;
        int x = Math.Clamp((int)MathF.Round(position.X), 0, _graphics!.Width - 1);
        int y = Math.Clamp((int)MathF.Round(position.Y), 0, _graphics.Height - 1);
        int width = Math.Clamp((int)MathF.Round(size.X), 1, _graphics.Width - x);
        int height = Math.Clamp((int)MathF.Round(size.Y), 1, _graphics.Height - y);
        return new Rectangle(x, y, width, height);
    }

    private void DrawWorld(ColorRenderer lines, Rectangle scene)
    {
        var camera = _state.Camera;
        var viewProjection = camera.ViewProjection((float)scene.Width / scene.Height);

        // Start loading when a game folder is found or changed, then build the GPU data once it is ready.
        _state.World.Load(_state.Game, _state.Settings.MapSelection, _state.Settings.HiddenScriptedMaps, _state.SaveFolder);

        // Sky behind everything, then opaque models, then the translucent navmesh, then lines.
        var settings = _state.Settings;
        float fogDistance = settings.Fog ? settings.FogDistance : 0f;
        var lighting = SceneLighting.ForHour(settings.TimeOfDay, fogDistance);
        _modelRenderer!.Lighting = lighting;
        _modelRenderer.CameraPosition = camera.Position;
        _modelRenderer.Anisotropy = settings.Anisotropy;

        bool modelsEnabled = _state.IsLayerVisible(ModelLayer.LayerName);
        int hour = (int)settings.TimeOfDay % 24;
        _modelLayer.Update(_state.World, _modelRenderer, camera.Position, settings.ViewDistanceMultiplier, hour, modelsEnabled);

        // Sun shadows: the objects seen from the sun first, into their own images.
        bool shadows = modelsEnabled && settings.Shadows;
        if (shadows)
        {
            _shadowMaps ??= new ShadowMaps(_graphics!.Device, _graphics.Context);
            _shadowMaps.Update(camera, (float)scene.Width / scene.Height, lighting.SunDirection, settings.ShadowDistance);
            _modelLayer.DrawShadows(_modelRenderer, _shadowMaps);
            _graphics!.BindBackBuffer();
            _graphics.SetViewport(scene.X, scene.Y, scene.Width, scene.Height);
        }
        _modelRenderer.Shadows = shadows ? _shadowMaps : null;

        if (settings.ShowSky)
            _skyRenderer!.Draw(viewProjection, lighting);
        bool occlusionPreview = modelsEnabled && settings.OcclusionPreview;
        _occlusionCuller.Update(_state.World, camera.Position, occlusionPreview);
        if (occlusionPreview)
            _occlusionCuller.Prepare(viewProjection, camera.Position, (float)scene.Width / scene.Height);
        if (modelsEnabled)
            _modelLayer.Draw(_modelRenderer, viewProjection, _state.SelectedEntities, camera.Position, fogDistance > 0f ? fogDistance : float.PositiveInfinity,
                () => DrawWater(viewProjection, lighting), occlusionPreview ? _occlusionCuller : null);
        else
            DrawWater(viewProjection, lighting);

        bool navMeshEnabled = NavMeshLayer.Surfaces.Any(s => _state.IsLayerVisible(NavMeshLayer.LayerName(s)));
        _navMeshLayer.Update(_state.World, lines, camera.Position, _state.Settings.NavMeshRadius, navMeshEnabled);
        _navMeshLayer.Draw(lines, viewProjection, camera.Position,
            s => _state.IsLayerVisible(NavMeshLayer.LayerName(s)), _state.IsLayerVisible(NavMeshLayer.OutlinesLayer));
        bool collisionsEnabled = CollisionLayer.Surfaces.Any(s => _state.IsLayerVisible(CollisionLayer.LayerName(s)));
        // The collision tool shows the collision even when its layer is hidden.
        bool collisionTool = _state.Tool == TransformTool.Collision;
        _collisionLayer.Update(_state.World, lines, camera.Position, _state.Settings.CollisionRadius, collisionsEnabled || collisionTool,
            _state.Settings.CollisionKind, _state.CollisionEdits);
        _collisionLayer.Draw(lines, viewProjection, camera.Position,
            s => _state.IsLayerVisible(CollisionLayer.LayerName(s)) || (collisionTool && !collisionsEnabled));
        if (collisionTool)
            _collisionLayer.DrawSelection(lines, viewProjection, camera.Position, _state.SelectedCollision, _state.CollisionSelectionVersion);
        _pathNodeLayer.Update(_state.World, lines, deferChanges: _ui.IsGizmoDragging || ImGui.IsAnyItemActive());
        _pathNodeLayer.Draw(lines, viewProjection, camera.Position, c => _state.IsLayerVisible(PathNodeLayer.LayerName(c)));
        bool entitiesEnabled = EntityBoxLayer.Categories.Any(c => _state.IsLayerVisible(EntityBoxLayer.LayerName(c)));
        _entityBoxLayer.Update(_state.World, lines, camera.Position, _state.Settings.EntityRadius, entitiesEnabled);
        _entityBoxLayer.Draw(lines, viewProjection, camera.Position, c => _state.IsLayerVisible(EntityBoxLayer.LayerName(c)));
        bool roomsEnabled = RoomLayer.Parts.Any(p => _state.IsLayerVisible(RoomLayer.LayerName(p)));
        _roomLayer.Update(_state.World, lines, camera.Position, roomsEnabled);
        _roomLayer.Draw(lines, viewProjection, camera.Position, p => _state.IsLayerVisible(RoomLayer.LayerName(p)));
        bool occlusionEnabled = OcclusionLayer.Parts.Any(p => _state.IsLayerVisible(OcclusionLayer.LayerName(p)));
        _occlusionLayer.Update(_state.World, lines, camera.Position, occlusionEnabled);
        _occlusionLayer.Draw(lines, viewProjection, camera.Position, p => _state.IsLayerVisible(OcclusionLayer.LayerName(p)));

        _scenarioLayer.Update(_state.World, lines);
        _scenarioLayer.Draw(lines, viewProjection, camera.Position, c => _state.IsLayerVisible(ScenarioLayer.LayerName(c)));

        if (_state.Settings.ShowGrid)
        {
            float fadeDistance = WorldGrid.AddTo(lines, camera.Position);
            lines.Flush(viewProjection, camera.Position, fadeDistance);
        }

        DrawSelectionBox(lines, viewProjection);
        if (_state.SelectedPathNode is { } selectedNode && _state.World.PathNetwork is { } pathNetwork)
            PathNodeLayer.DrawSelected(lines, pathNetwork, selectedNode, viewProjection, camera.Position);
        if (_state.SelectedOccluder is { } selectedOccluder)
            _occlusionLayer.DrawSelected(lines, selectedOccluder, viewProjection, camera.Position);
        DrawDeletionPreview(lines, viewProjection);
        DrawCollisionPreview(lines, viewProjection);
        DrawMarker(lines, viewProjection);
    }

    // Sea, lakes and rivers, then the water meshes of the models (pools, fountains), over the solid models (whose
    // depth tells how much water is crossed).
    private void DrawWater(Matrix4x4 viewProjection, SceneLighting lighting)
    {
        var water = _state.World.Water;
        if (!ReferenceEquals(water, _waterShown))
        {
            _waterShown = water;
            _waterRenderer!.SetSurfaces(water?.Quads.SelectMany(q => q.Triangles()) ?? [], water?.OceanBounds ?? Vector4.Zero);
        }
        if (!_state.IsLayerVisible(WaterLayer.LayerName))
            return;
        // Wrapped every hour, so the time stays precise enough for the waves.
        float time = (float)(_clock.Elapsed.TotalSeconds % 3600.0);
        _waterRenderer!.Begin(viewProjection, _state.Camera, lighting, time, _graphics!.CopyDepth());
        _waterRenderer.DrawSurfaces();
        if (_state.IsLayerVisible(ModelLayer.LayerName))
            _modelLayer.DrawWater(_waterRenderer);
        _waterRenderer.End();
    }

    // Pieces the export will remove: collision in red, occluders in blue.
    private readonly CutPreview _collisionPreview = new(new Vector3(1f, 0.15f, 0.1f), new Vector3(1f, 0.35f, 0.25f));
    private readonly CutPreview _occlusionPreview = new(new Vector3(0.2f, 0.55f, 1f), new Vector3(0.45f, 0.75f, 1f));

    // While the export window lists them: the collision pieces and occluders that will be removed, translucent with
    // their outlines.
    private void DrawCollisionPreview(ColorRenderer lines, Matrix4x4 viewProjection)
    {
        var camera = _state.Camera.Position;
        bool collisionHovered = _state.CollisionPreviewHovered is not null;
        bool occlusionHovered = _state.OcclusionPreviewHovered is not null;
        _collisionPreview.Draw(lines, viewProjection, camera, _state.CollisionPreview, _state.CollisionPreviewHovered, occlusionHovered, c => c.Triangles);
        _occlusionPreview.Draw(lines, viewProjection, camera, _state.OcclusionPreview, _state.OcclusionPreviewHovered, collisionHovered, c => c.Preview);
    }

    private void DisposeCollisionPreview()
    {
        _collisionPreview.Dispose();
        _occlusionPreview.Dispose();
    }

    // Removed pieces of a list of files, as translucent surfaces (lit or faint) and outlines, rebuilt when the list
    // changes.
    private sealed class CutPreview(Vector3 color, Vector3 edgeColor) : IDisposable
    {
        private readonly Vector4 _surfaceColor = new(color, 0.4f);
        private readonly Vector4 _faintColor = new(color, 0.1f);
        private readonly Vector4 _edgeColor = new(edgeColor, 0.95f);
        private object? _source;
        private readonly List<(object Cut, ColorMesh? Surface, ColorMesh? Faint, ColorMesh? Edges)> _meshes = new();

        public void Draw<T>(ColorRenderer lines, Matrix4x4 viewProjection, Vector3 camera, IReadOnlyList<T>? cuts, T? hovered,
            bool otherHovered, Func<T, IReadOnlyList<Vector3>> triangles) where T : class
        {
            if (!ReferenceEquals(cuts, _source))
            {
                _source = cuts;
                Dispose();
                foreach (var cut in cuts ?? [])
                {
                    var surface = new TriangleBatch();
                    var faint = new TriangleBatch();
                    var edges = new LineBatch();
                    var points = triangles(cut);
                    for (int i = 0; i + 2 < points.Count; i += 3)
                    {
                        var (a, b, c) = (points[i], points[i + 1], points[i + 2]);
                        surface.AddTriangle(a, b, c, _surfaceColor);
                        faint.AddTriangle(a, b, c, _faintColor);
                        edges.AddLine(a, b, _edgeColor);
                        edges.AddLine(b, c, _edgeColor);
                        edges.AddLine(c, a, _edgeColor);
                    }
                    _meshes.Add((cut, lines.CreateMesh(surface), lines.CreateMesh(faint), lines.CreateMesh(edges)));
                }
            }

            foreach (var (cut, surface, faint, edges) in _meshes)
            {
                bool lit = hovered is null ? !otherHovered : ReferenceEquals(hovered, cut);
                if ((lit ? surface : faint) is { } mesh)
                    lines.Draw(mesh, viewProjection, camera);
                if (lit && edges is not null)
                    lines.Draw(edges, viewProjection, camera);
            }
        }

        public void Dispose()
        {
            foreach (var (_, surface, faint, edges) in _meshes)
            {
                surface?.Dispose();
                faint?.Dispose();
                edges?.Dispose();
            }
            _meshes.Clear();
        }
    }

    private static readonly Vector4 DeletionColor = new(1f, 0.25f, 0.2f, 1f);

    // While a deletion waits for confirmation: the boxes of every object that would go, in red, visible through
    // walls.
    private void DrawDeletionPreview(ColorRenderer lines, Matrix4x4 viewProjection)
    {
        if (_state.PendingDeletion is not { } plan || _state.World.Maps is not { } library)
            return;
        foreach (var entity in plan.Entities)
        {
            if (library.GetArchetype(entity.ArchetypeHash) is { } archetype)
                lines.AddBox(archetype.BoundsMin, archetype.BoundsMax, entity.Transform, DeletionColor);
        }
        lines.Flush(viewProjection, _state.Camera.Position, onTop: true);
    }

    // --- Selection: left click on an object, Escape to deselect ---

    private static readonly Vector4 SelectionBoxColor = new(1f, 0.75f, 0.1f, 1f);
    private static readonly Vector4 SecondarySelectionBoxColor = new(0.85f, 0.55f, 0.1f, 0.8f);

    private void AttachSelectionInput()
    {
        _surface.MouseDown += (_, e) =>
        {
            // Clicks on an ImGui window belong to the interface; a click outside the global search only closes it;
            // a click on a 3D handle grabs it.
            if (e.Button == MouseButtons.Left && !ImGui.GetIO().WantCaptureMouse && !_ui.IsSearchOpen && !_ui.IsGizmoHovered)
            {
                if (_state.Tool == TransformTool.Collision)
                    _collisionDragStart = _collisionDragCurrent = e.Location;
                else
                    _pickRequest = (e.Location, (ModifierKeys & Keys.Control) != 0);
            }
        };
        _surface.MouseMove += (_, e) =>
        {
            if (_collisionDragStart is not null)
                _collisionDragCurrent = e.Location;
        };
        _surface.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _collisionDragStart is { } start)
            {
                _collisionPickRequest = (start, e.Location, (ModifierKeys & Keys.Control) != 0);
                _collisionDragStart = null;
            }
        };
        _surface.KeyDown += (_, e) =>
        {
            // Escape during a drag puts the object back (TransformGizmo) instead of deselecting.
            if (e.KeyCode == Keys.Escape && !ImGui.GetIO().WantCaptureKeyboard && !_ui.IsGizmoDragging)
            {
                _state.Select(null);
                _state.Marker = null;
                _state.SelectCollision([], CollisionSelectMode.Replace);
            }
        };
    }

    private const int DragThreshold = 4; // pixels: below, a click

    private void DrawCollisionRectangle()
    {
        if (_collisionDragStart is not { } start || !IsDrag(start, _collisionDragCurrent))
            return;
        var a = new Vector2(Math.Min(start.X, _collisionDragCurrent.X), Math.Min(start.Y, _collisionDragCurrent.Y)) / UiTheme.Scale;
        var b = new Vector2(Math.Max(start.X, _collisionDragCurrent.X), Math.Max(start.Y, _collisionDragCurrent.Y)) / UiTheme.Scale;
        var drawList = ImGui.GetForegroundDrawList();
        drawList.AddRectFilled(a, b, Widgets.Col(new Vector4(1f, 0.62f, 0.1f, 0.12f)));
        drawList.AddRect(a, b, Widgets.Col(new Vector4(1f, 0.75f, 0.3f, 0.9f)));
    }

    private static bool IsDrag(Point a, Point b) => Math.Abs(a.X - b.X) > DragThreshold || Math.Abs(a.Y - b.Y) > DragThreshold;

    // Collision tool: click = the polygon under the mouse, drag = the polygons seen in the rectangle; Ctrl adds
    // (a Ctrl+click on a selected polygon removes it).
    private void PickCollision(Rectangle scene)
    {
        if (_collisionPickRequest is not var (a, b, control))
            return;
        _collisionPickRequest = null;
        var viewProjection = _state.Camera.ViewProjection((float)scene.Width / scene.Height);
        var viewport = new Vector2(scene.Width, scene.Height);
        var origin = new Vector2(scene.X, scene.Y);
        HashSet<CollisionPolygon> picked;
        bool drag = IsDrag(a, b);
        if (drag)
        {
            var min = new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)) - origin;
            var max = new Vector2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)) - origin;
            picked = _collisionLayer.Pick(viewProjection, viewport, Vector2.Max(min, Vector2.Zero), Vector2.Min(max, viewport));
        }
        else
        {
            if (!scene.Contains(b))
                return;
            var point = new Vector2(b.X, b.Y) - origin;
            picked = _collisionLayer.Pick(viewProjection, viewport, point - new Vector2(0.5f), point + new Vector2(0.5f));
        }
        var mode = !control ? CollisionSelectMode.Replace : drag ? CollisionSelectMode.Add : CollisionSelectMode.Toggle;
        _state.SelectCollision(picked, mode);
        if (drag)
            _state.Notify(picked.Count == 0 ? "aucune surface de collision dans le rectangle" : $"{_state.SelectedCollision.Count} surface(s) de collision sélectionnée(s)", warning: picked.Count == 0);
    }

    private void PickClickedObject(Rectangle scene)
    {
        if (_pickRequest is not var (click, control) || _graphics is null || _modelRenderer is null || _pickBuffer is null)
            return;
        _pickRequest = null;
        if (!scene.Contains(click))
            return;

        var viewProjection = _state.Camera.ViewProjection((float)scene.Width / scene.Height);
        // Road nodes first: a dot on the road is clicked on purpose, the road model is under it.
        if (!control && _state.World.PathNetwork is { } network
            && PathNodeLayer.PickNode(network, viewProjection, _state.Camera.Position, new Vector2(click.X - scene.X, click.Y - scene.Y),
                new Vector2(scene.Width, scene.Height), c => _state.IsLayerVisible(PathNodeLayer.LayerName(c)), 10f * UiTheme.Scale) is { } node)
        {
            _state.Select(null);
            _state.SelectedPathNode = node;
            return;
        }
        // Occluders next, while their layer is shown: they sit inside the buildings that would be picked instead.
        if (!control && _occlusionLayer.Pick(viewProjection, new Vector2(click.X - scene.X, click.Y - scene.Y), new Vector2(scene.Width, scene.Height),
                p => _state.IsLayerVisible(OcclusionLayer.LayerName(p))) is { } occluder)
        {
            _state.Select(null);
            _state.SelectedOccluder = occluder;
            return;
        }
        // Then portals and rooms, while their layer is shown.
        if (!control)
        {
            var (room, portal) = _roomLayer.Pick(viewProjection, _state.Camera.Position, new Vector2(click.X - scene.X, click.Y - scene.Y),
                new Vector2(scene.Width, scene.Height), p => _state.IsLayerVisible(RoomLayer.LayerName(p)));
            if (portal is not null)
            {
                _state.SelectPortal(new RoomSelection(portal.Interior, portal.Index));
                return;
            }
            if (room is not null)
            {
                _state.SelectRoom(new RoomSelection(room.Interior, room.Index));
                return;
            }
        }
        if (!_state.IsLayerVisible(ModelLayer.LayerName))
            return;
        int x = click.X - scene.X, y = click.Y - scene.Y;
        // Ctrl+click adds the object to the selection or removes it; Ctrl+click on the sky keeps the selection.
        if (control)
        {
            if (_modelLayer.Pick(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, x, y) is { } toggled)
                _state.ToggleSelect(toggled);
            return;
        }

        // Clicked again at the same place: the next object behind those already picked there.
        var camera = (_state.Camera.Position, _state.Camera.Forward);
        bool again = _clickCycle.Count > 0 && Math.Abs(click.X - _cycleClick.X) <= 4 && Math.Abs(click.Y - _cycleClick.Y) <= 4
                     && camera == _cycleCamera && _state.Selection == _clickCycle[^1];
        if (again && _modelLayer.PickHit(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, x, y, _clickCycle.ToHashSet()) is { } next)
        {
            _clickCycle.Add(next.Entity);
            _state.Select(next.Entity);
            return;
        }

        _clickCycle.Clear();
        _cycleClick = click;
        _cycleCamera = camera;
        var hit = _modelLayer.PickHit(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, x, y, null);
        // A decal or painted shadow laid on a surface: the surface right under it first (a second click takes the decal).
        if (hit is { Overlay: true } overlay
            && _modelLayer.PickHit(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, x, y, new HashSet<Data.Maps.MapEntity> { overlay.Entity }) is { } under
            && Vector3.Distance(under.Point, overlay.Point) < OverlayDepth)
        {
            hit = under;
            var names = _state.World.Names;
            _state.Notify($"Recliquez au même endroit pour sélectionner {names?.Describe(overlay.Entity.ArchetypeHash) ?? "l'objet"} "
                + "(décalque ou ombre posé dessus).");
        }
        if (hit is { } chosen)
            _clickCycle.Add(chosen.Entity);
        _state.Select(hit?.Entity);
    }

    // Objects picked one after the other by clicking again at the same place (front first).
    private readonly List<Data.Maps.MapEntity> _clickCycle = [];
    private Point _cycleClick;
    private (Vector3, Vector3) _cycleCamera;
    private const float OverlayDepth = 0.5f; // Metres: a decal lies on the surface it covers.

    // An object asked to be added (viewer "Placer"), or copied objects to paste (Ctrl+V): put on the ground at the
    // centre of the 3D view, or 15 m in front of the camera when nothing is there (sky, models hidden).
    private void PlaceRequestedObject(Rectangle scene)
    {
        if (_state.PlaceRequest is 0 && !_state.PasteRequest || _modelRenderer is null || _pickBuffer is null)
            return;
        uint archetype = _state.PlaceRequest;
        bool paste = _state.PasteRequest;
        _state.PlaceRequest = 0;
        _state.PasteRequest = false;
        var camera = _state.Camera;
        Vector3? ground = null;
        if (_state.IsLayerVisible(ModelLayer.LayerName))
        {
            var viewProjection = camera.ViewProjection((float)scene.Width / scene.Height);
            ground = _modelLayer.PickPoint(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, scene.Width / 2, scene.Height / 2);
        }
        if (ground is not { } point || Vector3.Distance(point, camera.Position) > 500f)
            point = camera.Position + camera.Forward * 15f;
        if (paste)
            _state.Paste(point);
        else
            _state.Place(archetype, point);
    }

    private void DrawSelectionBox(ColorRenderer lines, Matrix4x4 viewProjection)
    {
        var selected = _state.SelectedEntities;
        if (selected.Count == 0 || _state.World.Maps is not { } library)
            return;
        for (int i = 0; i < selected.Count; i++)
        {
            var entity = selected[i];
            if (library.GetArchetype(entity.ArchetypeHash) is { } archetype)
                lines.AddBox(archetype.BoundsMin, archetype.BoundsMax, entity.Transform,
                    i == selected.Count - 1 ? SelectionBoxColor : SecondarySelectionBoxColor);
        }
        lines.Flush(viewProjection, _state.Camera.Position, onTop: true);
    }

    private static readonly Vector4 MarkerColor = new(0.30f, 0.55f, 1f, 1f);

    private void DrawMarker(ColorRenderer lines, Matrix4x4 viewProjection)
    {
        if (_state.Marker is not var (p, facing))
            return;
        lines.AddLine(p, p + new Vector3(0, 0, 4f), MarkerColor);
        lines.AddLine(p - new Vector3(1f, 0, 0), p + new Vector3(1f, 0, 0), MarkerColor);
        lines.AddLine(p - new Vector3(0, 1f, 0), p + new Vector3(0, 1f, 0), MarkerColor);
        if (facing is { } f)
        {
            var dir = new Vector3(f.X, f.Y, 0f);
            var side = new Vector3(-f.Y, f.X, 0f);
            var tip = p + dir * 2.5f;
            lines.AddLine(p, tip, MarkerColor);
            lines.AddLine(tip, tip - dir * 0.7f + side * 0.5f, MarkerColor);
            lines.AddLine(tip, tip - dir * 0.7f - side * 0.5f, MarkerColor);
        }
        lines.Flush(viewProjection, _state.Camera.Position, onTop: true);
    }

    private void RestoreCamera()
    {
        var camera = _state.Camera;
        if (_state.Settings.Camera is { } saved)
        {
            camera.Position = new Vector3(saved.X, saved.Y, saved.Z);
            camera.Yaw = saved.Yaw;
            camera.Pitch = saved.Pitch;
            _cameraController.Speed = Math.Clamp(saved.Speed, CameraController.MinSpeed, CameraController.MaxSpeed);
        }
        else
        {
            // Default view: above the world origin, looking down at it at an angle.
            camera.Position = new Vector3(-150f, -150f, 100f);
            camera.LookAt(Vector3.Zero);
        }
    }

    private void SaveCamera()
    {
        var camera = _state.Camera;
        _state.Settings.Camera = new CameraSettings
        {
            X = camera.Position.X,
            Y = camera.Position.Y,
            Z = camera.Position.Z,
            Yaw = camera.Yaw,
            Pitch = camera.Pitch,
            Speed = _cameraController.Speed,
        };
        _state.SaveSettings();
    }

    private void HandleUiRequests()
    {
        _state.Backup.Update(_clock.Elapsed.TotalSeconds);
        if (_state.Backup.Found is { } found)
            OfferRecovery(found);
        WindowChrome.IsMaximized = WindowState == FormWindowState.Maximized;
        if (WindowChrome.MinimizeRequested)
        {
            WindowChrome.MinimizeRequested = false;
            WindowState = FormWindowState.Minimized;
        }
        if (WindowChrome.CloseRequested)
        {
            WindowChrome.CloseRequested = false;
            _ui.ExitRequested = true;
        }

        if (_ui.ExitRequested)
        {
            _ui.ExitRequested = false;
            Close();
            return;
        }

        HandleProjectRequests();

        if (_ui.ArchiveExtractRequested is { } extract)
        {
            _ui.ArchiveExtractRequested = null;
            ExtractArchiveItem(extract);
        }

        if (_ui.ChooseExportFolderRequested)
        {
            _ui.ChooseExportFolderRequested = false;
            ChooseExportFolder();
        }

        if (_ui.SaveRequested)
        {
            _ui.SaveRequested = false;
            SaveMaps();
        }

        if (_ui.BrowseGameFolderRequested)
        {
            _ui.BrowseGameFolderRequested = false;
            BrowseForGameFolder();
        }
    }

    // Saves the modified map files into the project.
    private bool SaveMaps()
    {
        if (_state.PendingSaveCount == 0)
            return true;
        if (_state.Online.IsGuest)
        {
            _state.Notify("Session Online : c'est l'hôte qui enregistre le projet.", warning: true);
            return false;
        }
        if (_state.Project is null)
        {
            _ui.ProjectWindow.Open(saveAfter: true);
            return false;
        }
        _state.SaveMaps();
        if (_state.LastSave is { Success: false } failed)
        {
            MessageBox.Show(this, failed.Text, "Enregistrement", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    private void HandleProjectRequests()
    {
        if (_ui.ChooseProjectsFolderRequested)
        {
            _ui.ChooseProjectsFolderRequested = false;
            using var dialog = new FolderBrowserDialog
            {
                Description = "Dossier où créer les projets (chaque projet aura son sous-dossier).",
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(_state.ProjectsFolder) ? _state.ProjectsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _state.Settings.ProjectsFolder = dialog.SelectedPath;
                _state.SaveSettings();
            }
        }

        if (_ui.ProjectWindow.Requested is { } create)
        {
            _ui.ProjectWindow.Requested = null;
            CreateProject(create.Folder, create.Name, create.SaveAfter, create.ExitAfter);
        }

        if (_ui.ProjectWindow.OpenRequested is { } existing)
        {
            _ui.ProjectWindow.OpenRequested = null;
            OpenProject(existing);
        }

        if (_ui.RecentProjectRequested is { } recent)
        {
            _ui.RecentProjectRequested = null;
            OpenProject(recent);
        }

        if (_ui.OpenProjectRequested)
        {
            _ui.OpenProjectRequested = false;
            BrowseForProject();
        }

        if (_ui.ImportMappingRequested)
        {
            _ui.ImportMappingRequested = false;
            BrowseForMapping();
        }

        if (_ui.CloseProjectRequested)
        {
            _ui.CloseProjectRequested = false;
            if (ConfirmLeaveChanges())
                _state.CloseProject();
        }
    }

    private void CreateProject(string folder, string name, bool saveAfter, bool exitAfter)
    {
        // Without a project, the changes in progress go into the new one; otherwise the map is loaded again.
        bool reloads = _state.Project is not null || MapProject.HasMapFiles(folder);
        if (reloads && !ConfirmLeaveChanges())
            return;
        if (_state.CreateProject(folder, name) is { } error)
        {
            MessageBox.Show(this, $"Le projet n'a pas pu être créé :\n{error}", "Nouveau projet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (saveAfter && !reloads && !SaveMaps())
            return;
        if (exitAfter)
            Close();
    }

    private void OpenProject(string folder)
    {
        if (string.Equals(Path.GetFullPath(folder), _state.Project?.Folder, StringComparison.OrdinalIgnoreCase) || !ConfirmLeaveChanges())
            return;
        if (_state.OpenProject(folder) is { } error)
        {
            if (!MapProject.Exists(folder))
                _state.ForgetRecentProject(folder);
            MessageBox.Show(this, $"Le projet n'a pas pu être ouvert :\n{error}", "Ouvrir un projet", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Picks a project folder; a folder without projet.json can become a project (its .ymap files go to "stream").
    private void BrowseForProject()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choisis le dossier d'un projet NodeRunner (celui qui contient projet.json).",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_state.ProjectsFolder) ? _state.ProjectsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string folder = dialog.SelectedPath;
        if (MapProject.Exists(folder))
        {
            OpenProject(folder);
            return;
        }
        var answer = MessageBox.Show(this,
            $"« {Path.GetFileName(folder)} » n'est pas un projet NodeRunner.\nEn faire un projet ? Les .ymap qu'il contient seront rangés dans son dossier « stream ».",
            "Ouvrir un projet", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            CreateProject(folder, Path.GetFileName(folder), saveAfter: false, exitAfter: false);
    }

    private void BrowseForMapping()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choisis le dossier du mapping : une ressource FiveM (avec fxmanifest.lua) ou un dossier stream.",
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmLeaveChanges())
            return;
        NodeRunner.Editor.ImportResult result;
        try
        {
            result = NodeRunner.Editor.MappingImport.Import(dialog.SelectedPath, _state.ProjectsFolder, _state.Settings.MapVersion, _state.Settings.LastDlcPack);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"Le mapping n'a pas pu être importé :\n{ex.Message}", "Importer un mapping", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_state.OpenProject(result.Project.Folder) is { } error)
        {
            MessageBox.Show(this, $"Le projet importé n'a pas pu être ouvert :\n{error}", "Importer un mapping", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _ui.ShowProjectPanel();
        string skipped = result.Skipped.Count == 0 ? "" : $"\n\n{result.Skipped.Count} fichier(s) en double ignoré(s) (même nom) :\n" + string.Join("\n", result.Skipped.Take(10));
        MessageBox.Show(this,
            $"{result.Copied} fichier(s) copié(s) dans le projet « {result.Project.Name} » :\n{result.Project.Folder}\n\nLe panneau Projet liste les objets ajoutés, déplacés et supprimés.{skipped}",
            "Importer un mapping", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Before leaving the map shown (other project, closing it): unsaved changes are saved, left, or the action is
    // cancelled.
    private bool ConfirmLeaveChanges()
    {
        if (!_state.History.HasUnsavedChanges || _state.PendingSaveCount == 0)
            return true;
        string count = _state.PendingSaveText;
        if (_state.Project is not { } project)
        {
            return MessageBox.Show(this,
                $"Des modifications ne sont dans aucun projet ({count}) : elles seront perdues.\nContinuer ?",
                Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }
        var answer = MessageBox.Show(this,
            $"Des modifications du projet « {project.Name} » ne sont pas enregistrées ({count}).\nLes enregistrer d'abord ?",
            Program.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return answer == DialogResult.No || (answer == DialogResult.Yes && SaveMaps());
    }

    private void ExtractArchiveItem(ArchiveTree.Node node)
    {
        if (node.IsFile)
        {
            using var save = new SaveFileDialog
            {
                Title = $"Extraire {node.Name}",
                FileName = node.Name,
                Filter = "Tous les fichiers (*.*)|*.*",
                OverwritePrompt = true,
            };
            if (save.ShowDialog(this) == DialogResult.OK)
                _ui.ExtractArchiveItem(node, save.FileName);
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = $"Dossier où extraire « {node.Name} » ({node.FileCount} fichiers, un dossier de ce nom y sera créé).",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ui.ExtractArchiveItem(node, dialog.SelectedPath);
    }

    private void ChooseExportFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Dossier où créer la ressource FiveM (par exemple le dossier « resources » de ton serveur).",
            UseDescriptionForTitle = true,
            InitialDirectory = _state.ExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _state.ExportFolder = dialog.SelectedPath;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // A guest's changes belong to the host's project: nothing to save here.
        if (!e.Cancel && _state.Online.IsGuest)
        {
            _state.Online.Leave();
            return;
        }
        AskToSaveOnClose(e);
        if (!e.Cancel)
        {
            _state.Online.Leave();
            _state.Backup.Discard(); // Saved, or given up on purpose.
        }
    }

    private void AskToSaveOnClose(FormClosingEventArgs e)
    {
        if (e.Cancel || _deviceLost || !_state.History.HasUnsavedChanges || _state.PendingSaveCount == 0)
            return;
        var answer = MessageBox.Show(this,
            $"Des modifications ne sont pas enregistrées ({_state.PendingSaveText}).\nLes enregistrer avant de quitter ?",
            Program.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes && _state.Project is null)
        {
            // A project is needed first: the "New project" window saves, then closes NodeRunner.
            e.Cancel = true;
            _ui.ProjectWindow.Open(saveAfter: true, exitAfter: true);
            return;
        }
        if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveMaps()))
            e.Cancel = true;
    }

    private void OfferRecovery(AutoBackup.Recovery found)
    {
        string where = _state.Project is { } project ? $"du projet « {project.Name} »" : "faites sans projet";
        var list = found.Descriptions.Take(8).Select(d => $"  • {d}");
        string more = found.Descriptions.Count > 8 ? $"\n  ... et {found.Descriptions.Count - 8} autre(s)" : "";
        var answer = MessageBox.Show(this,
            $"Des modifications {where} n'ont pas été enregistrées (NodeRunner s'est peut-être fermé brusquement).\n"
            + $"Une copie de secours du {found.Date:dd/MM/yyyy à HH:mm} en contient {found.Messages.Count} :\n\n"
            + string.Join("\n", list) + more + "\n\nLes reprendre ? (Non = la copie de secours est supprimée)",
            Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            _state.Backup.Decline();
            return;
        }
        int applied = _state.Backup.Restore();
        _state.Notify(applied == found.Messages.Count
            ? $"{applied} modification(s) reprise(s) de la copie de secours (pas encore enregistrées : Ctrl+S)"
            : $"{applied} modification(s) sur {found.Messages.Count} reprise(s) : voir État > problèmes", warning: applied < found.Messages.Count);
    }

    private void BrowseForGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choisis le dossier d'installation de GTA V (celui qui contient GTA5.exe).",
            UseDescriptionForTitle = true,
            InitialDirectory = _state.Game?.FolderPath ?? "",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!_state.TrySetManualGameFolder(dialog.SelectedPath))
        {
            MessageBox.Show(this,
                "Ce dossier ne contient ni GTA5.exe ni GTA5_Enhanced.exe.",
                "Dossier invalide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

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
