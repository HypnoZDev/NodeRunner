using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using NodeRunner.App.Configuration;
using NodeRunner.App.Input;
using NodeRunner.App.Ui;
using NodeRunner.App.World;
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
    private BackdropBlur? _backdropBlur;
    private Rectangle _backdropArea; // Pixels of the 3D view blurred for the interface being built.
    private bool _backdropReady;
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
    private readonly ClassicCollisionLayer _classicCollisionLayer = new();
    private readonly EntityBoxLayer _entityBoxLayer = new();
    private readonly RoomLayer _roomLayer = new();
    private readonly OcclusionLayer _occlusionLayer = new();
    private readonly CarGeneratorLayer _carGeneratorLayer = new();
    private readonly FolderWatch _folderWatch = new();
    private readonly TimecycleLayer _timecycleLayer = new();
    private readonly LodLightLayer _lodLightLayer = new();
    private readonly AudioZoneLayer _audioZoneLayer = new();
    private readonly OcclusionCuller _occlusionCuller = new();
    private readonly ModelLayer _modelLayer = new();
    private readonly ClassicLighting _classicLighting = new();
    private WindowAlpha? _windowAlpha;
    private readonly bool _backdrop;
    private bool? _backdropShown;
    private IntPtr _imguiContext;
    private double _lastFrameTime;

    public MainForm(AppState state)
    {
        _state = state;
        _ui = new MainUi(state, _cameraController, _pathNodeLayer, _scenarioLayer, _navMeshLayer, _collisionLayer, _entityBoxLayer, _roomLayer, _occlusionLayer, _occlusionCuller, _modelLayer, _carGeneratorLayer, _timecycleLayer, _lodLightLayer, _audioZoneLayer);

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

        // Mapping folders, OpenIV packages (.oiv), .zip archives, map files or their XML exports dropped on the window are
        // added to the open project or imported as a new one.
        _surface.AllowDrop = true;
        _surface.DragEnter += (_, e) => e.Effect = DroppedPaths(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        _surface.DragDrop += (_, e) => _droppedPaths = DroppedPaths(e); // Handled on the next frame, not inside Explorer's drag loop.
    }

    private string[]? _droppedPaths;

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && paths.All(NodeRunner.Editor.MappingImport.CanImport) ? paths : null;

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

    private uint GameAccent() => UiTheme.GameAccent(_state.ActiveGame, _state.Gta4.Episode, _state.Settings.AccentColor);

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
        UiTheme.SetAccent(GameAccent());
        UiTheme.Apply();

        _graphics = new GraphicsDevice(_surface.Handle, _surface.ClientSize.Width, _surface.ClientSize.Height, _backdrop, _state.Settings.GraphicsCard);
        if (_graphics.IsTransparent)
            _windowAlpha = new WindowAlpha(_graphics.Device, _graphics.Context);
        _state.TransparentWindow = _graphics.IsTransparent;
        _imguiRenderer = new ImGuiRenderer(_graphics.Device, _graphics.Context);
        _state.SoftwareRendering = _graphics.IsSoftware;
        if (!_graphics.IsSoftware)
        {
            // Only a look: without it the glass stays a plain tint.
            try
            {
                _backdropBlur = new BackdropBlur(_graphics.Device, _graphics.Context);
            }
            catch (Exception e)
            {
                Diagnostics.ErrorLog.Add(L("Carte graphique"), e.Message);
            }
        }
        _lineRenderer = new ColorRenderer(_graphics.Device, _graphics.Context);
        _modelRenderer = new ModelRenderer(_graphics.Device, _graphics.Context);
        _skyRenderer = new SkyRenderer(_graphics.Device, _graphics.Context);
        _waterRenderer = new WaterRenderer(_graphics.Device, _graphics.Context);
        _pickBuffer = new PickBuffer(_graphics.Device, _graphics.Context);
        _placeBuffer = new PickBuffer(_graphics.Device, _graphics.Context);
        var graphics = _graphics;
        // Most of the video memory for the models, the rest for the window, shadows and other layers.
        // A fixed size chosen in the settings never goes above 90 % of what Windows grants (no crash on a small card).
        _modelLayer.MemoryBudget = () =>
        {
            long budget = graphics.VideoMemoryBudget();
            long chosen = (long)_state.Settings.ModelMemoryMb << 20;
            if (chosen > 0)
                return budget > 0 ? Math.Min(chosen, budget * 9 / 10) : chosen;
            return budget > 0 ? budget * 7 / 10 : long.MaxValue;
        };
        _state.ModelMemoryInfo = () => (_modelLayer.GpuBytes, _modelLayer.MemoryLimit);
        _state.AdapterName = _graphics.IsSoftware ? L($"{_graphics.AdapterName} (rendu logiciel, lent)") : _graphics.AdapterName;
        if (_graphics.IsSoftware)
            Diagnostics.ErrorLog.Add(L("Carte graphique"), L("Aucune carte graphique compatible DirectX 11 : affichage en rendu logiciel (lent). ")
                + L("Installe ou mets à jour le pilote de ta carte graphique."));

        if (_state.Settings.DetachableWindows)
            PlatformWindows.Initialize(this, _surface, _input, _graphics, _imguiRenderer);

        RestoreCamera();
        _surface.Resize += (_, _) => _graphics?.Resize(_surface.ClientSize.Width, _surface.ClientSize.Height);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.Idle -= OnApplicationIdle;
        PlatformWindows.Shutdown();
        // The free camera is kept, not the one following the character.
        _state.Play.Stop(_state.Camera);
        ReleaseMouse();
        SaveCamera();
        _state.World.Dispose();
        _state.Sound.Dispose();
        _pathNodeLayer.Dispose();
        _scenarioLayer.Dispose();
        _navMeshLayer.Dispose();
        _collisionLayer.Dispose();
        _classicCollisionLayer.Dispose();
        _entityBoxLayer.Dispose();
        _roomLayer.Dispose();
        _occlusionLayer.Dispose();
        _carGeneratorLayer.Dispose();
        _folderWatch.Dispose();
        _timecycleLayer.Dispose();
        _lodLightLayer.Dispose();
        _audioZoneLayer.Dispose();
        DisposeCollisionPreview();
        _modelLayer.Dispose();
        _state.Viewer.Dispose();
        _state.WorkshopViewer.Dispose();
        _state.PedViewer.Dispose();
        _state.Cutscenes.Dispose();
        _state.Recordings.Dispose();
        _state.SpotPreview.Dispose();
        _state.ScenarioPreview.Dispose();
        _state.ScenarioLife.Dispose();
        _state.ScenarioClips.Dispose();
        _state.Play.Dispose();
        _modelRenderer?.Dispose();
        _modelRenderer = null;
        _skyRenderer?.Dispose();
        _skyRenderer = null;
        _waterRenderer?.Dispose();
        _waterRenderer = null;
        _shadowMaps?.Dispose();
        _shadowMaps = null;
        _pickBuffer?.Dispose();
        _placeBuffer?.Dispose();
        _state.Placing.Dispose();
        _pickBuffer = null;
        _windowAlpha?.Dispose();
        _windowAlpha = null;
        _imguiRenderer?.SetTexture(OverlayStyle.BackdropTexture, null);
        _backdropBlur?.Dispose();
        _backdropBlur = null;
        _backdropReady = false;
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
        // In the background (another window has the focus): fewer images per second (setting, 0 = no slowing down).
        if (ActiveForm is null && _state.Settings.BackgroundFps > 0
            && 1.0 / _state.Settings.BackgroundFps - (_clock.Elapsed.TotalSeconds - _lastFrameTime) is var backgroundWait && backgroundWait > 0)
        {
            Thread.Sleep(Math.Min((int)Math.Ceiling(backgroundWait * 1000), 50)); // Short steps: the window reacts at once when it is activated.
            return;
        }
        // Images per second limit chosen by the user: sleep most of the wait, the last 2 ms by coming back here.
        if (_state.Settings.MaxFps > 0 && 1.0 / _state.Settings.MaxFps - (_clock.Elapsed.TotalSeconds - _lastFrameTime) is var wait && wait > 0)
        {
            if (wait > 0.002)
                PreciseSleep.For(wait - 0.002);
            else
                Thread.Yield();
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

    private bool _deviceLost;

    // The GPU was taken away: every GPU resource would have to be rebuilt, so the changes are saved and NodeRunner
    // starts again.
    private void OnDeviceLost()
    {
        if (_deviceLost)
            return;
        _deviceLost = true;
        string reason = _graphics!.LostReason;
        Diagnostics.ErrorLog.Add(L("Carte graphique"), L($"Carte graphique perdue : {reason}."));
        BeginInvoke(() => RestartAfterDeviceLost(reason));
    }

    private void RestartAfterDeviceLost(string reason)
    {
        string text = L($"La carte graphique ne répond plus ({reason}).\n")
            + L("Cela arrive quand son pilote plante, redémarre ou se met à jour. NodeRunner doit redémarrer pour réafficher la carte.");
        if (_state.History.HasUnsavedChanges && _state.PendingSaveCount > 0)
        {
            string count = _state.PendingSaveText;
            if (_state.Project is null)
            {
                // No window can be drawn anymore to name a project: the changes go into a new one.
                string name = L($"Récupération {DateTime.Now:yyyy-MM-dd HH-mm}");
                string folder = Path.Combine(_state.ProjectsFolder, name);
                if (_state.CreateProject(folder, name) is { } error)
                    text += L($"\n\nLes modifications ({count}) n'ont pas pu être enregistrées : {error}");
                else if (SaveMaps())
                    text += L($"\n\nLes modifications ({count}) ont été enregistrées dans le nouveau projet « {name} » :\n{folder}");
                else
                    text += L($"\n\nLes modifications ({count}) n'ont pas pu être enregistrées.");
            }
            else if (SaveMaps())
                text += L($"\n\nLes modifications du projet « {_state.Project.Name} » ({count}) ont été enregistrées.");
            else
                text += L($"\n\nLes modifications ({count}) n'ont pas pu être enregistrées.");
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
        _cameraController.Keys = _state.Keys;
        _cameraController.Paused = _state.Play.IsActive;
        _cameraController.WheelTaken = _state.Placing.IsActive;
        if (_state.Play.IsActive)
            UpdatePlay(Math.Min(deltaTime, 0.1f));
        else
        {
            ReleaseMouse();
            _cameraController.Update(_state.Camera, Math.Min(deltaTime, 0.1f), KeysOnSceneView() && !_state.IsCapturingKey);
        }

        UiTheme.ApplyPending();
        UiTheme.FollowAccent(GameAccent(), deltaTime);
        bool frosted = _backdropBlur is not null && _state.Settings.FrostedGlass && !_state.MapCovered &&_backdropArea.Width > 0;
        OverlayStyle.BackdropAvailable = frosted && _backdropReady;
        OverlayStyle.BackdropMin = new Vector2(_backdropArea.Left, _backdropArea.Top) / UiTheme.Scale;
        OverlayStyle.BackdropMax = new Vector2(_backdropArea.Right, _backdropArea.Bottom) / UiTheme.Scale;
        OverlayStyle.BackdropUsed = false;
        PlatformWindows.NewFrame();
        ImGui.NewFrame();
        _ui.Draw();
        DrawCollisionRectangle();
        ImGui.Render();
        _input.UpdateCursor();

        // The viewer's image first (it binds its own target), for the interface drawn below.
        _state.Viewer.ClassicAmbient = ClassicLighting.ViewerAmbientOf(_state.Classic.Game);
        _state.Viewer.Render(_modelRenderer!, imguiRenderer, graphics, _lineRenderer);
        _state.WorkshopViewer.Surroundings = WorkshopSurroundings();
        _state.WorkshopViewer.Render(_modelRenderer!, imguiRenderer, graphics, _lineRenderer);
        _state.PedViewer.Render(_modelRenderer!, imguiRenderer, graphics, _lineRenderer);
        _state.Thumbnails.Render(_modelRenderer!, imguiRenderer, graphics, _state.World);
        var scene = SceneViewport();
        ToggleClickedDoor(scene);
        PickClickedObject(scene);
        PickContextObject(scene);
        PickCollision(scene);
        PlaceRequestedObject(scene);
        UpdatePlacement(scene);
        StartRequestedPlay(scene);
        graphics.BeginFrame(BackgroundColor);
        graphics.SetViewport(scene.X, scene.Y, scene.Width, scene.Height);
        // A tab covers the 3D view: only the map's loading goes on.
        if (_state.MapCovered)
            LoadWorld();
        else
            DrawWorld(_lineRenderer!, scene);
        // The interface was built for last frame's area: blur that one (the same unless the panels just moved).
        if (frosted && (OverlayStyle.BackdropUsed || !_backdropReady))
        {
            _backdropBlur!.Render(graphics, _backdropArea.X, _backdropArea.Y, _backdropArea.Width, _backdropArea.Height);
            imguiRenderer.SetTexture(OverlayStyle.BackdropTexture, _backdropBlur.View);
            _backdropReady = _backdropBlur.View is not null;
        }
        _backdropArea = scene;
        float opacity = 1f - Math.Clamp(_state.Settings.Transparency, 0f, MaxTransparency);
        _windowAlpha?.MakeOpaque(scene.X, scene.Y, scene.Width, scene.Height);
        imguiRenderer.Render(ImGui.GetDrawData());
        if (_windowAlpha is not null && opacity < 1f)
            _windowAlpha.Fade(graphics.Width, graphics.Height, scene.X, scene.Y, scene.Width, scene.Height, opacity);
        ShowBackdrop(_windowAlpha is not null && opacity < 1f);
        graphics.Present(_state.Settings.VSync);
        PlatformWindows.RenderWindows();

        HandleUiRequests();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string title = _state.Project is { } project
            ? $"{project.Name}{(_state.ExtraProjects.Count > 0 ? " + " + string.Join(" + ", _state.ExtraProjects.Select(p => p.Name)) : "")} — {Program.Name}"
            : Program.Name;
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

    private void RestoreCamera()
    {
        var camera = _state.Camera;
        // Each game keeps its own camera; a first session of another game starts above its city (ShowGta4Map,
        // ShowClassicMap).
        var saved = _state.IsGta4 ? _state.Settings.Gta4Camera
            : _state.IsClassic ? _state.Settings.ClassicCameras.GetValueOrDefault(_state.ActiveGame)
            : _state.Settings.Camera;
        _gta4CameraPending = _state.IsOtherGame && saved is null;
        if (saved is not null)
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
        var saved = new CameraSettings
        {
            X = camera.Position.X,
            Y = camera.Position.Y,
            Z = camera.Position.Z,
            Yaw = camera.Yaw,
            Pitch = camera.Pitch,
            Speed = _cameraController.Speed,
        };
        if (_state.IsGta4)
            _state.Settings.Gta4Camera = saved;
        else if (_state.IsClassic)
            _state.Settings.ClassicCameras[_state.ActiveGame] = saved;
        else
            _state.Settings.Camera = saved;
        _state.SaveSettings();
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
            L($"Des modifications ne sont pas enregistrées ({_state.PendingSaveText}).\nLes enregistrer avant de quitter ?"),
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
        string where = _state.Project is { } project ? L($"du projet « {project.Name} »") : L("faites sans projet");
        var list = found.Descriptions.Take(8).Select(d => $"  • {d}");
        string more = found.Descriptions.Count > 8 ? L($"\n  ... et {found.Descriptions.Count - 8} autre(s)") : "";
        var answer = MessageBox.Show(this,
            L($"Des modifications {where} n'ont pas été enregistrées (NodeRunner s'est peut-être fermé brusquement).\n")
            + L($"Une copie de secours du {found.Date:dd/MM/yyyy à HH:mm} en contient {found.Messages.Count} :\n\n")
            + string.Join("\n", list) + more + L("\n\nLes reprendre ? (Non = la copie de secours est supprimée)"),
            Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            _state.Backup.Decline();
            return;
        }
        int applied = _state.Backup.Restore();
        _state.Notify(applied == found.Messages.Count
            ? L($"{applied} modification(s) reprise(s) de la copie de secours (pas encore enregistrées : Ctrl+S)")
            : L($"{applied} modification(s) sur {found.Messages.Count} reprise(s) : voir État > problèmes"), warning: applied < found.Messages.Count);
    }

    private void BrowseForGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = L("Choisis le dossier d'installation de GTA V (celui qui contient GTA5.exe)."),
            UseDescriptionForTitle = true,
            InitialDirectory = _state.Game?.FolderPath ?? "",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!_state.TrySetManualGameFolder(dialog.SelectedPath))
        {
            MessageBox.Show(this,
                L("Ce dossier ne contient ni GTA5.exe ni GTA5_Enhanced.exe."),
                L("Dossier invalide"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

}
