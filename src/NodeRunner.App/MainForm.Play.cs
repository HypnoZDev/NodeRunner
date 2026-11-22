using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;
using NodeRunner.App.World;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
    private const float PlayFarthestStart = 300f; // Metres: a point of the view farther than this is not a start.

    // Play asked for (button, key, menu): the character starts on the ground at the centre of the 3D view.
    private void StartRequestedPlay(Rectangle scene)
    {
        if (!_state.PlayRequest)
            return;
        _state.PlayRequest = false;
        if (!_state.Features.Play || _state.MapCovered || _state.Play.IsActive || _modelRenderer is null || _pickBuffer is null)
            return;
        var camera = _state.Camera;
        Vector3? hit = null;
        if (_state.IsLayerVisible(ModelLayer.LayerName))
        {
            var viewProjection = camera.ViewProjection((float)scene.Width / scene.Height);
            hit = _modelLayer.PickPoint(_modelRenderer, _pickBuffer, viewProjection, scene.Width, scene.Height, scene.Width / 2, scene.Height / 2);
        }
        if (hit is not { } point || Vector3.Distance(point, camera.Position) > PlayFarthestStart)
            point = camera.Position + camera.Forward * 8f;
        _state.Play.Start(_state.World, camera, point, _state.GroundHeightAt(point));
    }

    private void UpdatePlay(float step)
    {
        var play = _state.Play;
        // Another tab or game took the 3D view.
        if (_state.MapCovered || _state.IsOtherGame || _modelRenderer is null)
        {
            play.Stop(_state.Camera);
            ReleaseMouse();
            return;
        }
        var keys = _state.Keys;
        var io = ImGui.GetIO();
        var move = Vector2.Zero;
        if (!io.WantTextInput)
        {
            if (keys.Held(KeyAction.MoveForward)) move.Y += 1f;
            if (keys.Held(KeyAction.MoveBackward)) move.Y -= 1f;
            if (keys.Held(KeyAction.MoveRight)) move.X += 1f;
            if (keys.Held(KeyAction.MoveLeft)) move.X -= 1f;
        }
        if (move != Vector2.Zero)
            move = Vector2.Normalize(move);
        var input = new PlayInput(move, io.KeyShift, io.KeyAlt, ImGui.IsKeyPressed(ImGuiKey.Space, false), CaptureMouse(), io.MouseWheel);
        play.Update(_state.World, _state.CollisionEdits, _modelRenderer, _state.Camera, input, _state.Settings.InvertMouseY,
            _cameraController.Sensitivity, step);
    }

    private bool _mouseCaptured;

    // Play mode turns the camera with the mouse alone, as a game does: the pointer is hidden and brought back to the
    // middle of the window each frame. Released while another window is in front.
    private Vector2 CaptureMouse()
    {
        if (ActiveForm != this)
        {
            ReleaseMouse();
            return Vector2.Zero;
        }
        var bounds = _surface.RectangleToScreen(_surface.ClientRectangle);
        var centre = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        if (!_mouseCaptured)
        {
            _mouseCaptured = true;
            Cursor.Hide();
            Cursor.Clip = bounds;
            Cursor.Position = centre;
            return Vector2.Zero;
        }
        var at = Cursor.Position;
        Cursor.Position = centre;
        return new Vector2(at.X - centre.X, at.Y - centre.Y);
    }

    private void ReleaseMouse()
    {
        if (!_mouseCaptured)
            return;
        _mouseCaptured = false;
        Cursor.Clip = Rectangle.Empty;
        Cursor.Show();
    }
}
