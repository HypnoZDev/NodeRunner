using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Ui;
using NodeRunner.App.World;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
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

    // A left click on the 3D view leaves no ImGui window focused; otherwise the keys go to the
    // focused panel unless the mouse is over the 3D view.
    private bool KeysOnSceneView()
    {
        if (_state.WorkshopShown || _state.PedShown)
            return false;
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow))
            return true;
        if (ImGui.GetIO().WantCaptureMouse || !_surface.Focused)
            return false;
        return SceneViewport().Contains(_surface.PointToClient(Cursor.Position));
    }

    // The map's objects around the workshop's model, placed in the model's own space (null: not asked for).
    private List<(GpuModel Model, Matrix4x4 Transform)>? WorkshopSurroundings()
    {
        if (!(_state.WorkshopCoversMap || _state.WorkshopOpen && _state.WorkshopDetached) || !_state.WorkshopShowsMap || _state.WorkshopEntity is not { } entity
            || !Matrix4x4.Invert(entity.Transform, out var toModel))
            return null;
        float radius = _state.WorkshopViewer.Radius * 2f + WorkshopMapRadius;
        var around = _modelLayer.LoadedAround(entity.Position, radius, entity);
        for (int i = 0; i < around.Count; i++)
            around[i] = (around[i].Model, around[i].Transform * toModel);
        return around;
    }

    private const float WorkshopMapRadius = 60f; // Metres around the model, beyond its own size.

    // Starts loading when a game folder is found or changed, then builds the GPU data once it is ready.
    private void LoadWorld() =>
        _state.World.Load(_state.IsOtherGame ? null : _state.Game, _state.Settings.MapSelection, _state.Settings.HiddenScriptedMaps, _state.SaveFolder, _state.ExtraStreamFolders);

    private void DrawWorld(ColorRenderer lines, Rectangle scene)
    {
        if (_state.GameSwitchRequest is { } nextGame && !_state.Classic.IsBusy)
        {
            _state.GameSwitchRequest = null;
            SaveCamera();
            _state.Gta4Selection = null;
            _state.SwitchClassicGame(nextGame);
            RestoreCamera();
        }

        var camera = _state.Camera;
        var viewProjection = camera.ViewProjection((float)scene.Width / scene.Height);

        LoadWorld();

        // Sky behind everything, then opaque models, then the translucent navmesh, then lines.
        var settings = _state.Settings;
        float fogDistance = settings.Fog ? settings.FogDistance : 0f;
        var lighting = SceneLighting.ForHour(settings.TimeOfDay, fogDistance);
        if (_state.IsClassic)
            lighting = _classicLighting.Apply(lighting, _state.Classic.TimeCycle, settings.TimeOfDay, camera.Position);
        _modelRenderer!.Lighting = lighting;
        _modelRenderer.CameraPosition = camera.Position;
        _modelRenderer.Anisotropy = settings.Anisotropy;

        bool modelsEnabled = _state.IsLayerVisible(ModelLayer.LayerName);
        int hour = (int)settings.TimeOfDay % 24;
        if (_state.Other is { } other)
        {
            var library = other.Library;
            var models = other.Models;
            var textures = other.Textures;
            _modelLayer.Update(library, models, other.ModelReader,
                () => textures is null ? null : new TextureResolver(textures, other.TextureReader), _modelRenderer, camera.Position,
                settings.ViewDistanceMultiplier, hour, modelsEnabled);
            _modelLayer.Doors = null;
        }
        else
        {
            _modelLayer.Update(_state.World, _modelRenderer, camera.Position, settings.ViewDistanceMultiplier, hour, modelsEnabled);
            _state.OpenDoors.Update(_state.World.Maps);
            _modelLayer.Doors = _state.OpenDoors;
        }

        _modelLayer.Hidden = _state.IsClassic ? _state.Classic.HiddenRooms(_state.Gta4Selection, camera.Position) : _state.Cutscenes.Hidden;

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
            _modelLayer.Draw(_modelRenderer, viewProjection, SelectedForDrawing, camera.Position, fogDistance > 0f ? fogDistance : float.PositiveInfinity,
                () => DrawWater(viewProjection, lighting, scene), occlusionPreview ? _occlusionCuller : null);
        else
            DrawWater(viewProjection, lighting, scene);

        // The car of the selected parking spot.
        var previewSpot = _state.IsLayerVisible(CarGeneratorLayer.LayerName) ? _state.SelectedCarGenerator : null;
        _state.SpotPreview.Update(_state.World, _modelRenderer, previewSpot);
        if (previewSpot?.Spot is { } shownSpot)
            _state.SpotPreview.Draw(_modelRenderer, shownSpot, _carGeneratorLayer.GroundOf(_state.World, shownSpot), viewProjection);

        // The ped, animal or vehicle of the selected scenario point.
        var previewPoint = _state.SelectedScenario is { } point && _state.IsLayerVisible(ScenarioLayer.LayerName(point.Category)) ? point : null;
        _state.ScenarioClips.Update(_modelRenderer);
        _state.ScenarioPreview.Update(_state.World, _modelRenderer, _state.ScenarioClips, previewPoint);
        if (previewPoint is not null)
            _state.ScenarioPreview.Draw(_modelRenderer, previewPoint, viewProjection);

        // Peds, animals and vehicles on the points the game would use at this hour.
        _state.ScenarioLife.Update(_state.World, _modelRenderer, _state.ScenarioClips, camera.Position, hour, settings.MapVersion == NodeRunner.Data.FileSystem.MapVersion.Multiplayer,
            c => _state.IsLayerVisible(ScenarioLayer.LayerName(c)), settings.ScenarioLife && !_state.IsOtherGame && _state.Cutscenes.Cutscene is null);
        _state.ScenarioLife.Draw(_modelRenderer, viewProjection, previewPoint);

        // The cutscene chosen in the cutscene viewer, at its place.
        _state.Cutscenes.Update(_state.World, _modelRenderer);
        _state.Cutscenes.Draw(_modelRenderer, viewProjection);

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
        if (_state.IsClassic && _state.Classic.Game is var classicGame)
        {
            _classicCollisionLayer.Update(_state.Classic.Scene, _state.Classic.Files, classicGame, lines, camera.Position,
                _state.Settings.CollisionRadius, collisionsEnabled);
            _classicCollisionLayer.Draw(lines, viewProjection, camera.Position, s => _state.IsLayerVisible(CollisionLayer.LayerName(s)));
        }
        if (collisionTool)
            _collisionLayer.DrawSelection(lines, viewProjection, camera.Position, _state.SelectedCollision, _state.CollisionSelectionVersion,
                _state.CollisionDragPreview);
        if (_state.IsOtherGame)
            _pathNodeLayer.UpdateGta4(_state.IsGta4 ? _state.Gta4.PathAreas : null, lines);
        else
            _pathNodeLayer.Update(_state.World, lines, deferChanges: _ui.IsGizmoDragging || ImGui.IsAnyItemActive());
        _pathNodeLayer.Draw(lines, viewProjection, camera.Position, c => _state.IsLayerVisible(PathNodeLayer.LayerName(c)));
        bool entitiesEnabled = EntityBoxLayer.Categories.Any(c => _state.IsLayerVisible(EntityBoxLayer.LayerName(c)));
        if (_state.IsOtherGame)
        {
            var gta4Boxes = _state.IsGta4 ? _state.Gta4.MapBoxes : null;
            _entityBoxLayer.Update(gta4Boxes, gta4Boxes is null ? null : gta4Boxes.Around, 0, lines, camera.Position, _state.Settings.EntityRadius,
                entitiesEnabled);
            if (_state.IsGta4)
                ShowGta4Map(gta4Boxes);
            else
                ShowClassicMap(_state.Classic.Library);
        }
        else
        {
            _entityBoxLayer.Update(_state.World, lines, camera.Position, _state.Settings.EntityRadius, entitiesEnabled);
        }
        _entityBoxLayer.Draw(lines, viewProjection, camera.Position, c => _state.IsLayerVisible(EntityBoxLayer.LayerName(c)));
        bool roomsEnabled = RoomLayer.Parts.Any(p => _state.IsLayerVisible(RoomLayer.LayerName(p)));
        _roomLayer.Update(_state.World, lines, camera.Position, roomsEnabled);
        _roomLayer.Draw(lines, viewProjection, camera.Position, p => _state.IsLayerVisible(RoomLayer.LayerName(p)));
        bool occlusionEnabled = OcclusionLayer.Parts.Any(p => _state.IsLayerVisible(OcclusionLayer.LayerName(p)));
        _occlusionLayer.Update(_state.World, lines, camera.Position, occlusionEnabled);
        _occlusionLayer.Draw(lines, viewProjection, camera.Position, p => _state.IsLayerVisible(OcclusionLayer.LayerName(p)));
        bool carGeneratorsEnabled = _state.IsLayerVisible(CarGeneratorLayer.LayerName);
        _carGeneratorLayer.Update(_state.World, lines, camera.Position, carGeneratorsEnabled);
        if (carGeneratorsEnabled)
            _carGeneratorLayer.Draw(lines, viewProjection, camera.Position);
        bool timecycleEnabled = _state.IsLayerVisible(TimecycleLayer.LayerName);
        _timecycleLayer.Update(_state.World, lines, camera.Position, timecycleEnabled);
        if (timecycleEnabled)
            _timecycleLayer.Draw(lines, viewProjection, camera.Position);
        bool lodLightsEnabled = _state.IsLayerVisible(LodLightLayer.LayerName);
        _lodLightLayer.Update(_state.World, lines, camera.Position, lodLightsEnabled);
        if (lodLightsEnabled)
            _lodLightLayer.Draw(lines, viewProjection, camera.Position);

        _scenarioLayer.Update(_state.World, lines);
        _scenarioLayer.Draw(lines, viewProjection, camera.Position, c => _state.IsLayerVisible(ScenarioLayer.LayerName(c)));

        if (_state.Settings.ShowGrid)
        {
            var gridColor = _state.Settings.GridColor is [var r, var g, var b, var a] ? new Vector4(r, g, b, a) : WorldGrid.DefaultColor;
            float fadeDistance = WorldGrid.AddTo(lines, camera.Position, _state.Settings.GridSpacing, gridColor);
            lines.Flush(viewProjection, camera.Position, fadeDistance);
        }

        DrawSelectionBox(lines, viewProjection);
        if (_state.SelectedPathNode is { } selectedNode && _state.World.PathNetwork is { } pathNetwork)
            PathNodeLayer.DrawSelected(lines, pathNetwork, selectedNode, viewProjection, camera.Position);
        if (_state.SelectedScenario is { } selectedScenario)
            ScenarioLayer.DrawSelected(lines, selectedScenario, viewProjection, camera.Position);
        if (_state.SelectedOccluder is { } selectedOccluder)
            _occlusionLayer.DrawSelected(lines, selectedOccluder, viewProjection, camera.Position);
        if (_state.SelectedCarGenerator?.Spot is { } selectedSpot)
            _carGeneratorLayer.DrawSelected(lines, _state.World, selectedSpot, viewProjection, camera.Position);
        if (_state.SelectedTimecycle?.Zone is { } selectedZone)
            TimecycleLayer.DrawSelected(lines, selectedZone, viewProjection, camera.Position);
        if (_state.SelectedLodLight is { } selectedLight)
            LodLightLayer.DrawSelected(lines, selectedLight, viewProjection, camera.Position);
        DrawDeletionPreview(lines, viewProjection);
        DrawCollisionPreview(lines, viewProjection);
        DrawMarker(lines, viewProjection);
    }

    private bool _gta4CameraPending;

    // Once the GTA IV map is read, in a first GTA IV session: the camera is moved above the centre of Liberty City.
    private void ShowGta4Map(Gta4MapBoxes? boxes)
    {
        if (boxes is null || boxes.Count == 0 || !_gta4CameraPending)
            return;
        _gta4CameraPending = false;
        var center = (boxes.Min + boxes.Max) * 0.5f;
        _state.Camera.Position = new Vector3(center.X, center.Y - 400f, 350f);
        _state.Camera.LookAt(new Vector3(center.X, center.Y, 0f));
    }

    // A first session of a trilogy game starts above the middle of its city.
    private void ShowClassicMap(NodeRunner.Data.Maps.MapLibrary? library)
    {
        if (library is null || !_gta4CameraPending)
            return;
        _gta4CameraPending = false;
        var outside = library.Maps.Where(m => m.Entities.Count > 0).ToList();
        if (outside.Count == 0)
            return;
        var center = outside.Select(m => (m.EntitiesExtentsMin + m.EntitiesExtentsMax) * 0.5f).Aggregate((a, b) => a + b) / outside.Count;
        _state.Camera.Position = new Vector3(center.X, center.Y - 400f, 350f);
        _state.Camera.LookAt(new Vector3(center.X, center.Y, 0f));
    }

    // Sea, lakes and rivers, then the water meshes of the models (pools, fountains), over the solid models (whose
    // depth tells how much water is crossed).
    private void DrawWater(Matrix4x4 viewProjection, SceneLighting lighting, Rectangle scene)
    {
        var water = _state.Other is { } other ? other.Water : _state.World.Water;
        if (!ReferenceEquals(water, _waterShown))
        {
            _waterShown = water;
            _waterRenderer!.SetSurfaces(water?.Quads.SelectMany(q => q.Triangles()) ?? [], water?.OceanBounds ?? Vector4.Zero, water?.OceanHeight ?? 0f);
        }
        if (!_state.IsLayerVisible(WaterLayer.LayerName))
            return;
        // Wrapped every hour, so the time stays precise enough for the waves.
        float time = (float)(_clock.Elapsed.TotalSeconds % 3600.0);
        _waterRenderer!.Begin(viewProjection, _state.Camera, lighting, time, _graphics!.CopyDepth(),
            _state.Settings.WaterReflections ? _graphics.CopyColor() : null, new Vector4(scene.X, scene.Y, scene.Width, scene.Height));
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

    // The selected objects of the map shown (GTA V, or the one GTA IV object).
    private IReadOnlyList<Data.Maps.MapEntity> SelectedForDrawing =>
        !_state.IsOtherGame ? _state.SelectedEntities : _state.Gta4Selection is { } gta4 ? [gta4] : [];

    private void DrawSelectionBox(ColorRenderer lines, Matrix4x4 viewProjection)
    {
        var selected = SelectedForDrawing;
        if (selected.Count == 0 || _state.ShownMaps is not { } library)
            return;
        for (int i = 0; i < selected.Count; i++)
        {
            var entity = selected[i];
            if (library.GetArchetype(entity.ArchetypeHash) is { } archetype)
                lines.AddBox(archetype.BoundsMin, archetype.BoundsMax,
                    !_state.IsOtherGame && _state.OpenDoors.TryGetPose(entity, out var opened) ? opened * entity.Transform : entity.Transform,
                    i == selected.Count - 1 ? SelectionBoxColor : SecondarySelectionBoxColor);
        }
        // How the selected doors open.
        if (!_state.IsOtherGame && _state.World.Names is { } names)
        {
            foreach (var door in selected.Take(MaxDoorArcs))
            {
                if (library.GetArchetype(door.ArchetypeHash) is not { } type || !Doors.IsDoor(names.TryGet(door.ArchetypeHash)))
                    continue;
                var transform = door.Transform;
                var tuning = _state.World.DoorTunings?.OfModel(door.ArchetypeHash);
                var (min, max) = DoorShape.Swing(tuning);
                var shape = DoorShape.Of(type, names.TryGet(door.ArchetypeHash));
                // Where the door touches a wall or furniture: the part of the arc it cannot reach, in red. Checked for
                // the last selected door only (it reads the collision around it).
                if (shape.Kind == DoorKind.Hinged && door == selected[^1] && _state.OpenDoors.ClearanceOf(door, _state) is { Blocked: true } clear)
                {
                    foreach (var (from, to) in shape.Lines(clear.ReachedMin, clear.ReachedMax))
                        lines.AddLine(Vector3.Transform(from, transform), Vector3.Transform(to, transform), DoorArcColor);
                    foreach (var (wanted, reached) in new[] { (clear.WantedMin, clear.ReachedMin), (clear.WantedMax, clear.ReachedMax) })
                    {
                        if (!DoorClearance.IsBlocked(wanted, reached))
                            continue;
                        foreach (var (from, to) in shape.Lines(MathF.Min(wanted, reached), MathF.Max(wanted, reached)).Take(DoorShape.ArcSegments))
                            lines.AddLine(Vector3.Transform(from, transform), Vector3.Transform(to, transform), DoorBlockedColor);
                    }
                }
                else
                {
                    foreach (var (from, to) in shape.Lines(min, max))
                        lines.AddLine(Vector3.Transform(from, transform), Vector3.Transform(to, transform), DoorArcColor);
                }
                // Where vehicles or people make the door open by itself.
                if (tuning is { HasTriggerBox: true })
                    lines.AddBox(tuning.TriggerBoxMin, tuning.TriggerBoxMax, transform, DoorTriggerColor);
            }
        }
        // The opening about to be cut through a building, and the room behind it.
        if (!_state.IsOtherGame && _state.OpeningTool is { Current: { } opening } openingTool)
        {
            foreach (var (from, to) in opening.Edges())
                lines.AddLine(from, to, DoorBlockedColor);
            var plan = Editor.BuildingOpening.Make(opening, openingTool.RoomSize);
            var room = Matrix4x4.CreateFromQuaternion(plan.Orientation) * Matrix4x4.CreateTranslation(plan.Position);
            lines.AddBox(plan.Room.BoundsMin, plan.Room.BoundsMax, room, DoorArcColor);
        }
        lines.Flush(viewProjection, _state.Camera.Position, onTop: true);
    }

    private const int MaxDoorArcs = 40;
    private static readonly Vector4 DoorArcColor = new(0.35f, 1f, 0.55f, 0.9f);
    private static readonly Vector4 DoorTriggerColor = new(1f, 0.8f, 0.25f, 0.8f);
    private static readonly Vector4 DoorBlockedColor = new(1f, 0.3f, 0.25f, 0.95f);

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
}
