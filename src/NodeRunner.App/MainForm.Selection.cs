using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Ui;
using NodeRunner.App.World;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
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
                else if (e.Clicks == 2 && (ModifierKeys & Keys.Control) == 0)
                    _doorClick = e.Location;
                else
                    _pickRequest = (e.Location, (ModifierKeys & Keys.Control) != 0);
            }
        };
        // Right click without moving the mouse (a drag turns the camera): context menu of the 3D view.
        _surface.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _rightDown = !ImGui.GetIO().WantCaptureMouse && !_ui.IsSearchOpen ? (e.Location, Environment.TickCount64) : null;
        };
        _surface.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _rightDown is var (at, time) && !IsDrag(at, e.Location) && Environment.TickCount64 - time < 500)
                _contextRequest = e.Location;
            if (e.Button == MouseButtons.Right)
                _rightDown = null;
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
            if (e.KeyCode == Keys.Escape && !ImGui.GetIO().WantCaptureKeyboard && _state.OpeningTool is not null)
            {
                _state.OpeningTool = null;
                return;
            }
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
            _state.Notify(picked.Count == 0 ? L("aucune surface de collision dans le rectangle") : L($"{_state.SelectedCollision.Count} surface(s) de collision sélectionnée(s)"), warning: picked.Count == 0);
    }

    private Point? _doorClick;

    // Double click on a door: opens or closes it in the view; on the selected object: its model in the workshop; on
    // anything else, an ordinary click.
    private void ToggleClickedDoor(Rectangle scene)
    {
        if (_doorClick is not { } click || _modelRenderer is null || _pickBuffer is null)
            return;
        _doorClick = null;
        if (!_state.IsOtherGame && _state.OpeningTool is null && scene.Contains(click) && _state.IsLayerVisible(ModelLayer.LayerName) && _state.World.Names is { } names)
        {
            var viewProjection = _state.Camera.ViewProjection((float)scene.Width / scene.Height);
            if (_modelLayer.Pick(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, click.X - scene.X, click.Y - scene.Y) is { } picked)
            {
                if (Doors.IsDoor(names.TryGet(picked.ArchetypeHash)))
                {
                    _state.Select(picked);
                    _state.ToggleDoors([picked]);
                    return;
                }
                if (picked == _state.Selection)
                {
                    _state.WorkshopRequest = picked;
                    return;
                }
            }
        }
        _pickRequest = (click, false);
    }

    private void PickClickedObject(Rectangle scene)
    {
        if (_pickRequest is not var (click, control) || _graphics is null || _modelRenderer is null || _pickBuffer is null)
            return;
        _pickRequest = null;
        if (!scene.Contains(click))
            return;

        var viewProjection = _state.Camera.ViewProjection((float)scene.Width / scene.Height);
        // Picking the wall of a building to open: the click goes to the tool.
        if (_state.OpeningTool is { } openingTool && !_state.IsOtherGame)
        {
            if (Matrix4x4.Invert(viewProjection, out var inverse))
            {
                float cx = (click.X - scene.X + 0.5f) / scene.Width * 2f - 1f, cy = 1f - (click.Y - scene.Y + 0.5f) / scene.Height * 2f;
                // Reversed depth with an infinite far plane: 1 is the near plane, 0 infinitely far.
                var near = Vector4.Transform(new Vector4(cx, cy, 1f, 1f), inverse);
                var far = Vector4.Transform(new Vector4(cx, cy, 0.5f, 1f), inverse);
                var from = new Vector3(near.X, near.Y, near.Z) / near.W;
                var direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - from);
                if (!openingTool.Click(from, direction, _state.GroundHeightAt))
                    _state.Notify(L("Cliquez sur un mur de ce bâtiment (une surface debout de son modèle)."), warning: true);
            }
            return;
        }
        // Other games than GTA V: one object at a time, read only.
        if (_state.IsOtherGame)
        {
            if (_state.IsLayerVisible(ModelLayer.LayerName))
                _state.Gta4Selection = _modelLayer.Pick(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, click.X - scene.X, click.Y - scene.Y);
            return;
        }
        // Road nodes first: a dot on the road is clicked on purpose, the road model is under it.
        var linkSource = _state.PathLinkSource;
        _state.PathLinkSource = null;
        if (!control && _state.World.PathNetwork is { } network
            && PathNodeLayer.PickNode(network, viewProjection, _state.Camera.Position, new Vector2(click.X - scene.X, click.Y - scene.Y),
                new Vector2(scene.Width, scene.Height), c => _state.IsLayerVisible(PathNodeLayer.LayerName(c)), 10f * UiTheme.Scale) is { } node)
        {
            if (linkSource is not null && linkSource != node)
                _state.LinkPathNodes(linkSource, node);
            _state.Select(null);
            _state.SelectedPathNode = node;
            return;
        }
        // Scenario points, while one of their layers is shown: thin posts, picked within a few pixels.
        if (!control && _state.World.Scenarios is { } scenarios
            && ScenarioLayer.Pick(scenarios, viewProjection, _state.Camera.Position, new Vector2(click.X - scene.X, click.Y - scene.Y),
                new Vector2(scene.Width, scene.Height), c => _state.IsLayerVisible(ScenarioLayer.LayerName(c))) is { } scenario)
        {
            _state.Select(null);
            _state.SelectedScenario = scenario;
            return;
        }
        // A ped, animal or vehicle of the life simulation: its point, unless a model of the map hides it there.
        if (!control && _state.ScenarioLife.Pick(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height,
                click.X - scene.X, click.Y - scene.Y, null) is { } life)
        {
            var wall = _state.IsLayerVisible(ModelLayer.LayerName)
                ? _modelLayer.PickHit(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, click.X - scene.X, click.Y - scene.Y, null)
                : null;
            var eye = _state.Camera.Position;
            if (wall is not { } w || Vector3.Distance(eye, w.Point) > Vector3.Distance(eye, life.Hit) - 0.05f)
            {
                _state.Select(null);
                _state.SelectedScenario = life.Point;
                return;
            }
        }
        // Lights next, while their layer is shown: small points, picked within a few pixels.
        if (!control && _state.IsLayerVisible(LodLightLayer.LayerName)
            && _lodLightLayer.Pick(viewProjection, new Vector2(click.X - scene.X, click.Y - scene.Y), new Vector2(scene.Width, scene.Height)) is { } light)
        {
            _state.Select(null);
            _state.SelectedLodLight = light;
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
        // Parking spots, while their layer is shown.
        if (!control && _state.IsLayerVisible(CarGeneratorLayer.LayerName)
            && _carGeneratorLayer.Pick(viewProjection, new Vector2(click.X - scene.X, click.Y - scene.Y), new Vector2(scene.Width, scene.Height)) is { } spot)
        {
            _state.Select(null);
            _state.SelectedCarGenerator = spot;
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
        // Timecycle zones, while their layer is shown: after the rooms, which they often hold.
        if (!control && _state.IsLayerVisible(TimecycleLayer.LayerName)
            && _timecycleLayer.Pick(viewProjection, new Vector2(click.X - scene.X, click.Y - scene.Y), new Vector2(scene.Width, scene.Height)) is { } zone)
        {
            _state.Select(null);
            _state.SelectedTimecycle = zone;
            return;
        }
        // Audio zones and sound sources, while their layers are shown.
        bool audioZones = _state.IsLayerVisible(AudioZoneLayer.ZonesLayerName), audioEmitters = _state.IsLayerVisible(AudioZoneLayer.EmittersLayerName);
        if (!control && (audioZones || audioEmitters)
            && _audioZoneLayer.Pick(viewProjection, new Vector2(click.X - scene.X, click.Y - scene.Y), new Vector2(scene.Width, scene.Height), audioZones, audioEmitters) is { } audio)
        {
            _state.Select(null);
            _state.SelectedAudio = audio;
            return;
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
            _state.Notify(L($"Recliquez au même endroit pour sélectionner {names?.Describe(overlay.Entity.ArchetypeHash) ?? L("l'objet")} ")
                + L("(décalque ou ombre posé dessus)."));
        }
        if (hit is { } chosen)
            _clickCycle.Add(chosen.Entity);
        _state.Select(hit?.Entity);
    }

    private (Point, long)? _rightDown;
    private Point? _contextRequest;

    // Right click: the object under the mouse joins the selection unless it is already in it, then the menu opens.
    private void PickContextObject(Rectangle scene)
    {
        if (_contextRequest is not { } click || _modelRenderer is null || _pickBuffer is null)
            return;
        _contextRequest = null;
        if (_state.IsOtherGame)
            return;
        if (!scene.Contains(click))
            return;
        if (_state.Tool != TransformTool.Collision && _state.IsLayerVisible(ModelLayer.LayerName))
        {
            var viewProjection = _state.Camera.ViewProjection((float)scene.Width / scene.Height);
            if (_modelLayer.PickHit(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, click.X - scene.X, click.Y - scene.Y, null) is { } hit
                && !_state.SelectedEntities.Contains(hit.Entity))
                _state.Select(hit.Entity);
        }
        _ui.OpenViewportMenu();
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
        var at = _state.PlacePoint ?? new Vector2(0.5f, 0.5f);
        _state.PlaceRequest = 0;
        _state.PasteRequest = false;
        _state.PlacePoint = null;
        var camera = _state.Camera;
        Vector3? ground = null;
        if (_state.IsLayerVisible(ModelLayer.LayerName))
        {
            var viewProjection = camera.ViewProjection((float)scene.Width / scene.Height);
            ground = _modelLayer.PickPoint(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height,
                Math.Clamp((int)(at.X * scene.Width), 0, scene.Width - 1), Math.Clamp((int)(at.Y * scene.Height), 0, scene.Height - 1));
        }
        if (ground is not { } point || Vector3.Distance(point, camera.Position) > 500f)
            point = camera.Position + camera.Forward * 15f;
        if (paste)
            _state.Paste(point);
        else
            _state.Place(archetype, point);
    }
}
