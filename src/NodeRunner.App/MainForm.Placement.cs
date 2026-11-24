using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.App.World;
using NodeRunner.Editor;
using NodeRunner.Render;

namespace NodeRunner.App;

// --- Placing a prop with the mouse: its model under the mouse, a click adds it, the wheel turns it ---
internal sealed partial class MainForm
{
    private static readonly Vector3 GhostTint = new(0.75f, 0.95f, 1.45f);
    private static readonly Vector4 GhostBoxColor = new(0.3f, 0.55f, 1f, 1f);
    private const float MaxPlaceDistance = 500f;

    private PickBuffer? _placeBuffer;
    private bool _placeClick;
    private Point? _placedAt;
    private (Point, Size, Vector3, float, float, float, int) _placeKey;
    private PlacementPose? _placeFound;
    private int _placeFrames;
    private const int PlaceRefreshFrames = 15;
    private const int PlacedMouseMove = 24; // pixels

    private void UpdatePlacement(Rectangle scene)
    {
        var placement = _state.Placing;
        if (!placement.IsActive)
        {
            _placeClick = false;
            _placedAt = null;
            return;
        }
        if (_state.Project is null || _state.IsOtherGame || _state.Play.IsActive || _state.MapCovered || _state.World.Maps is not { } library
            || _modelRenderer is null || _placeBuffer is null)
        {
            _state.StopPlacement();
            return;
        }
        placement.Update(_modelRenderer);

        var io = ImGui.GetIO();
        var mouse = _surface.PointToClient(Cursor.Position);
        bool overScene = !io.WantCaptureMouse && scene.Contains(mouse);
        if (overScene && io.MouseWheel != 0f)
            placement.Turn += MathF.Sign(io.MouseWheel) * (io.KeyShift ? SurfacePlacement.FineStep : SurfacePlacement.Step);
        if (KeysOnSceneView() && !io.WantTextInput && _state.Keys.Pressed(KeyAction.PlaceRotate))
            placement.Turn += SurfacePlacement.Step;
        placement.Turn = MathF.IEEERemainder(placement.Turn, 360f);

        bool click = _placeClick;
        _placeClick = false;
        placement.Pose = null;
        if (!overScene || !_state.IsLayerVisible(ModelLayer.LayerName))
            return;
        // Right after a click the ghost hides until the mouse moves away: it would stand on the prop just added.
        if (_placedAt is { } at)
        {
            if (Math.Abs(mouse.X - at.X) <= PlacedMouseMove && Math.Abs(mouse.Y - at.Y) <= PlacedMouseMove)
                return;
            _placedAt = null;
        }

        // Found again only when something changed (the surface is drawn again to find it), and now and then for the
        // models still loading.
        var camera = _state.Camera;
        var key = (mouse, scene.Size, camera.Position, camera.Yaw, camera.Pitch, placement.Turn, placement.Placed.Count);
        if (!click && key == _placeKey && ++_placeFrames < PlaceRefreshFrames)
        {
            placement.Pose = _placeFound;
            return;
        }
        _placeKey = key;
        _placeFrames = 0;
        _placeFound = null;

        var (min, max) = (placement.BoundsMin, placement.BoundsMax);
        if (SurfacePose(scene, mouse.X - scene.X, mouse.Y - scene.Y, min, max, placement.FloorYaw, placement.Turn) is not var (pose, _))
            return;
        placement.Pose = pose;
        _placeFound = pose;

        if (click && _state.PlaceAt(placement.ArchetypeHash, pose.Position, pose.Orientation) is { } added)
        {
            placement.Placed.AddRange(added);
            placement.Pose = null;
            _placedAt = mouse;
        }
    }

    // Where an object of box (min, max) goes for the surface at pixel (x, y) of the 3D view, and the object under it.
    private (PlacementPose Pose, Data.Maps.MapEntity Under)? SurfacePose(Rectangle scene, int x, int y, Vector3 min, Vector3 max, float floorYaw, float turn)
    {
        if (_modelRenderer is null || _placeBuffer is null)
            return null;
        var camera = _state.Camera;
        var viewProjection = camera.ViewProjection((float)scene.Width / scene.Height);
        if (_modelLayer.PickSurface(_modelRenderer, _placeBuffer, viewProjection, scene.Width, scene.Height, x, y, camera.Position)
                is not var (under, point, normal)
            || Vector3.Distance(point, camera.Position) > MaxPlaceDistance)
            return null;
        var pose = SurfacePlacement.Pose(point, normal, min, max, floorYaw, turn);
        if (pose.NeedsGround)
        {
            float ground = _state.GroundHeightAt(pose.Position) ?? point.Z;
            pose = pose with { Position = pose.Position with { Z = ground - min.Z } };
        }
        return (pose, under);
    }

    // A library picture let go over the 3D view: the prop goes on the surface under the mouse, front to the camera.
    private bool PlaceDropped(Rectangle scene, Vector2 at, uint archetypeHash)
    {
        if (_state.World.Maps?.GetArchetype(archetypeHash) is not { Kind: not Data.Maps.ArchetypeKind.Interior } archetype
            || !_state.IsLayerVisible(ModelLayer.LayerName))
            return false;
        var forward = _state.Camera.Forward;
        float yaw = MathF.Round(MathF.Atan2(forward.X, -forward.Y) * 180f / MathF.PI / SurfacePlacement.FineStep) * SurfacePlacement.FineStep;
        int x = Math.Clamp((int)(at.X * scene.Width), 0, scene.Width - 1), y = Math.Clamp((int)(at.Y * scene.Height), 0, scene.Height - 1);
        if (SurfacePose(scene, x, y, archetype.BoundsMin, archetype.BoundsMax, yaw, 0f) is not var (pose, _))
            return false;
        if (_state.PlaceAt(archetypeHash, pose.Position, pose.Orientation) is { } added)
            _state.SelectAll(added);
        return true;
    }

    // The prop being placed, where a click would put it: its model tinted and its box.
    private void DrawPlacementGhost(ColorRenderer lines, Matrix4x4 viewProjection)
    {
        if (_state.Placing is not { IsActive: true, Pose: { } pose } placement || _modelRenderer is null)
            return;
        var transform = Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(pose.Position);
        if (placement.Model is { } model)
        {
            _modelRenderer.Begin(ModelPass.Solid);
            _modelRenderer.Draw(model, transform, viewProjection, GhostTint);
            _modelRenderer.Begin(ModelPass.Translucent);
            _modelRenderer.Draw(model, transform, viewProjection, GhostTint);
        }
        lines.AddBox(placement.BoundsMin, placement.BoundsMax, transform, GhostBoxColor);
    }
}
