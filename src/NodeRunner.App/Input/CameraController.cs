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

    private readonly HashSet<ImGuiKey> _keysDown = new();
    private readonly List<ImGuiKey> _pressed = new(); // Key presses (with repeats) since the last Update.
    private bool _looking;
    private Point _lastMouse;
    private Vector2 _pendingLook;

    public float Speed { get; set; } = 50f;

    // Multiplies the mouse look speed (1 = default).
    public float Sensitivity { get; set; } = 1f;

    // Moving the mouse up looks down, as in a flight simulator.
    public bool InvertY { get; set; }

    // Keys of the camera actions (Paramètres > Raccourcis).
    public KeyMap? Keys { get; set; }

    public bool IsLooking => _looking;

    // Play mode moves the camera: the mouse buttons and wheel are left alone.
    public bool Paused { get; set; }

    public void Attach(Control control)
    {
        control.KeyDown += (_, e) =>
        {
            var key = ImGuiInput.MapKey(e.KeyCode);
            _keysDown.Add(key);
            _pressed.Add(key);
        };
        control.KeyUp += (_, e) => _keysDown.Remove(ImGuiInput.MapKey(e.KeyCode));
        control.LostFocus += (_, _) => { _keysDown.Clear(); _looking = false; };

        control.MouseDown += (_, e) =>
        {
            // Clicks on an ImGui window belong to the interface, not to the camera.
            if (e.Button == MouseButtons.Right && !ImGuiWantsMouse() && !Paused)
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
            if (ImGuiWantsMouse() || Paused)
                return;
            Speed =Math.Clamp(Speed * MathF.Pow(WheelSpeedFactor, e.Delta / 120f), MinSpeed, MaxSpeed);
        };
    }

    // keysOnView: the keyboard belongs to the 3D view (not to a panel such as the viewer).
    public void Update(Camera camera, float deltaTime, bool keysOnView = true)
    {
        float sensitivity = MouseSensitivity * Sensitivity;
        camera.Yaw -= _pendingLook.X * sensitivity;
        camera.Pitch -= _pendingLook.Y * sensitivity * (InvertY ? -1f : 1f);
        _pendingLook = Vector2.Zero;

        var pressed = _pressed.ToArray();
        _pressed.Clear();
        if (!keysOnView && !_looking || Keys is not { } keys)
            return;
        // Don't move while the user is typing in an ImGui text field.
        if (ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantTextInput)
            return;
        // Ctrl + key is a shortcut (Ctrl+Z: undo), not a move; Alt also (Ctrl+Alt = AltGr).
        if ((Control.ModifierKeys & (System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt)) != 0)
            return;

        foreach (var key in pressed)
        {
            if (keys.Uses(KeyAction.SpeedUp, key))
                Speed = Math.Clamp(Speed * WheelSpeedFactor, MinSpeed, MaxSpeed);
            else if (keys.Uses(KeyAction.SpeedDown, key))
                Speed = Math.Clamp(Speed / WheelSpeedFactor, MinSpeed, MaxSpeed);
        }

        var move = Vector3.Zero;
        if (Held(keys, KeyAction.MoveForward)) move += camera.Forward;
        if (Held(keys, KeyAction.MoveBackward)) move -= camera.Forward;
        if (Held(keys, KeyAction.MoveRight)) move += camera.Right;
        if (Held(keys, KeyAction.MoveLeft)) move -= camera.Right;
        if (Held(keys, KeyAction.MoveUp)) move += Vector3.UnitZ;
        if (Held(keys, KeyAction.MoveDown)) move -= Vector3.UnitZ;

        if (move == Vector3.Zero)
            return;

        float speed = Speed * (Control.ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift) ? FastMultiplier : 1f);
        camera.Position += Vector3.Normalize(move) * speed * deltaTime;
    }

    private bool Held(KeyMap keys, KeyAction action)
    {
        foreach (var chord in keys.Chords(action))
        {
            if (_keysDown.Contains(chord.Key))
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
