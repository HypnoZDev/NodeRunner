using System.Numerics;
using ImGuiNET;
using NodeRunner.Render;

namespace NodeRunner.App.Input;

internal sealed class CameraController
{
    public const float MinSpeed = 1f;
    public const float MaxSpeed = 2000f;
    private const float FastMultiplier = 5f;
    private const float MouseSensitivity = 0.004f; // Radians per pixel.
    private const float WheelSpeedFactor = 1.25f;

    private readonly HashSet<Keys> _keysDown = new();
    private bool _looking;
    private Point _lastMouse;
    private Vector2 _pendingLook;

    public float Speed { get; set; } = 50f;

    public bool IsLooking => _looking;

    public void Attach(Control control)
    {
        control.KeyDown += (_, e) => _keysDown.Add(e.KeyCode);
        control.KeyUp += (_, e) => _keysDown.Remove(e.KeyCode);
        control.LostFocus += (_, _) => { _keysDown.Clear(); _looking = false; };

        control.MouseDown += (_, e) =>
        {
            // Clicks on an ImGui window belong to the interface, not to the camera.
            if (e.Button == MouseButtons.Right && !ImGuiWantsMouse())
            {
                _looking = true;
                _lastMouse = e.Location;
            }
        };
        control.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _looking = false;
        };
        control.MouseMove += (_, e) =>
        {
            if (!_looking)
                return;
            _pendingLook += new Vector2(e.X - _lastMouse.X, e.Y - _lastMouse.Y);
            _lastMouse = e.Location;
        };
        control.MouseWheel += (_, e) =>
        {
            if (ImGuiWantsMouse())
                return;
            Speed = Math.Clamp(Speed * MathF.Pow(WheelSpeedFactor, e.Delta / 120f), MinSpeed, MaxSpeed);
        };
    }

    public void Update(Camera camera, float deltaTime)
    {
        camera.Yaw -= _pendingLook.X * MouseSensitivity;
        camera.Pitch -= _pendingLook.Y * MouseSensitivity;
        _pendingLook = Vector2.Zero;

        // Don't move while the user is typing in an ImGui text field.
        if (ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantTextInput)
            return;
        // Ctrl + key is a shortcut (Ctrl+Z: undo), not a move.
        if (Control.ModifierKeys.HasFlag(Keys.Control))
            return;

        var move = Vector3.Zero;
        if (AnyDown(Keys.W, Keys.Z, Keys.Up)) move += camera.Forward;
        if (AnyDown(Keys.S, Keys.Down)) move -= camera.Forward;
        if (AnyDown(Keys.D, Keys.Right)) move += camera.Right;
        if (AnyDown(Keys.A, Keys.Q, Keys.Left)) move -= camera.Right;
        if (AnyDown(Keys.Space, Keys.E, Keys.PageUp)) move += Vector3.UnitZ;
        if (AnyDown(Keys.C, Keys.PageDown)) move -= Vector3.UnitZ;

        if (move == Vector3.Zero)
            return;

        float speed = Speed * (Control.ModifierKeys.HasFlag(Keys.Shift) ? FastMultiplier : 1f);
        camera.Position += Vector3.Normalize(move) * speed * deltaTime;
    }

    private bool AnyDown(params ReadOnlySpan<Keys> keys)
    {
        foreach (var key in keys)
        {
            if (_keysDown.Contains(key))
                return true;
        }
        return false;
    }

    private static bool ImGuiWantsMouse() =>
        ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantCaptureMouse;
}
