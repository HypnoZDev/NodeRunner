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

    // Multiplies the mouse look speed (1 = default).
    public float Sensitivity { get; set; } = 1f;

    // Moving the mouse up looks down, as in a flight simulator.
    public bool InvertY { get; set; }

    public MovementKeys MovementKeys { get; set; }

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
        float sensitivity = MouseSensitivity * Sensitivity;
        camera.Yaw -= _pendingLook.X * sensitivity;
        camera.Pitch -= _pendingLook.Y * sensitivity * (InvertY ? -1f : 1f);
        _pendingLook = Vector2.Zero;

        // Don't move while the user is typing in an ImGui text field.
        if (ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantTextInput)
            return;
        // Ctrl + key is a shortcut (Ctrl+Z: undo), not a move.
        if (Control.ModifierKeys.HasFlag(Keys.Control))
            return;

        var move = Vector3.Zero;
        // Key codes follow the keyboard layout: the forward key is Z on AZERTY, W on QWERTY.
        var (forward, left) = MovementKeys switch
        {
            MovementKeys.Zqsd => (Keys.Z, Keys.Q),
            MovementKeys.Wasd => (Keys.W, Keys.A),
            _ => (Keys.None, Keys.None),
        };
        bool both = forward == Keys.None;
        if (AnyDown(Keys.Up, forward) || both && AnyDown(Keys.W, Keys.Z)) move += camera.Forward;
        if (AnyDown(Keys.S, Keys.Down)) move -= camera.Forward;
        if (AnyDown(Keys.D, Keys.Right)) move += camera.Right;
        if (AnyDown(Keys.Left, left) || both && AnyDown(Keys.A, Keys.Q)) move -= camera.Right;
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

// Letters that move the camera: both sets at once, or only the one of the keyboard.
public enum MovementKeys
{
    Both,
    Zqsd,
    Wasd,
}
