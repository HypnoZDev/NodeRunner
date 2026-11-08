using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Ui;

namespace NodeRunner.App.Input;

// Forwards Windows Forms mouse and keyboard events to Dear ImGui.
internal sealed class ImGuiInput
{
    private readonly List<Control> _controls = new();
    private ImGuiMouseCursor _cursor = ImGuiMouseCursor.Arrow;

    // relativeTo: for a detached panel window, positions are sent in the coordinates of the main 3D surface.
    public void Attach(Control control, Control? relativeTo = null)
    {
        _controls.Add(control);
        control.Disposed += (_, _) => _controls.Remove(control);
        control.MouseMove += (_, e) =>
        {
            var p = relativeTo is null ? e.Location : relativeTo.PointToClient(control.PointToScreen(e.Location));
            Send(io => io.AddMousePosEvent(p.X / UiTheme.Scale, p.Y / UiTheme.Scale));
        };
        control.MouseDown += (_, e) => OnMouseButton(e.Button, true);
        control.MouseUp += (_, e) => OnMouseButton(e.Button, false);
        control.MouseWheel += (_, e) => Send(io => io.AddMouseWheelEvent(0f, e.Delta / 120f));
        control.MouseLeave += (_, _) => Send(io => io.AddMousePosEvent(-float.MaxValue, -float.MaxValue));
        control.KeyDown += (_, e) => OnKey(e, true);
        control.KeyUp += (_, e) => OnKey(e, false);
        control.KeyPress += (_, e) => Send(io => io.AddInputCharacter(e.KeyChar));
        control.LostFocus += (_, _) => Send(io => io.AddFocusEvent(false));
        control.GotFocus += (_, _) => Send(io => io.AddFocusEvent(true));
    }

    // Shows the mouse cursor ImGui asks for: text beam over the fields, arrows on the edge of a panel that can be
    // resized.
    public void UpdateCursor()
    {
        var cursor = ImGui.GetMouseCursor();
        if (cursor == _cursor)
            return;
        _cursor = cursor;
        var shown = cursor switch
        {
            ImGuiMouseCursor.TextInput => Cursors.IBeam,
            ImGuiMouseCursor.ResizeAll => Cursors.SizeAll,
            ImGuiMouseCursor.ResizeEW => Cursors.SizeWE,
            ImGuiMouseCursor.ResizeNS => Cursors.SizeNS,
            ImGuiMouseCursor.ResizeNESW => Cursors.SizeNESW,
            ImGuiMouseCursor.ResizeNWSE => Cursors.SizeNWSE,
            ImGuiMouseCursor.Hand => Cursors.Hand,
            ImGuiMouseCursor.NotAllowed => Cursors.No,
            _ => Cursors.Default,
        };
        foreach (var control in _controls)
            control.Cursor = shown;
    }

    // Forwards an event only while an ImGui context exists.
    private static void Send(Action<ImGuiIOPtr> action)
    {
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
            return;
        action(ImGui.GetIO());
    }

    private static void OnMouseButton(MouseButtons button, bool down)
    {
        int index = button switch
        {
            MouseButtons.Left => 0,
            MouseButtons.Right => 1,
            MouseButtons.Middle => 2,
            MouseButtons.XButton1 => 3,
            MouseButtons.XButton2 => 4,
            _ => -1,
        };
        if (index >= 0)
            Send(io => io.AddMouseButtonEvent(index, down));
    }

    private static void OnKey(KeyEventArgs e, bool down)
    {
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
            return;

        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, e.Control);
        io.AddKeyEvent(ImGuiKey.ModShift, e.Shift);
        io.AddKeyEvent(ImGuiKey.ModAlt, e.Alt);

        var key = MapKey(e.KeyCode);
        if (key != ImGuiKey.None)
            io.AddKeyEvent(key, down);
    }

    private static ImGuiKey MapKey(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z)
            return ImGuiKey.A + (key - Keys.A);
        if (key >= Keys.D0 && key <= Keys.D9)
            return ImGuiKey._0 + (key - Keys.D0);
        if (key >= Keys.F1 && key <= Keys.F12)
            return ImGuiKey.F1 + (key - Keys.F1);
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return ImGuiKey.Keypad0 + (key - Keys.NumPad0);

        return key switch
        {
            Keys.Tab => ImGuiKey.Tab,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home,
            Keys.End => ImGuiKey.End,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            Keys.Back => ImGuiKey.Backspace,
            Keys.Space => ImGuiKey.Space,
            Keys.Enter => ImGuiKey.Enter,
            Keys.Escape => ImGuiKey.Escape,
            Keys.ShiftKey or Keys.LShiftKey => ImGuiKey.LeftShift,
            Keys.RShiftKey => ImGuiKey.RightShift,
            Keys.ControlKey or Keys.LControlKey => ImGuiKey.LeftCtrl,
            Keys.RControlKey => ImGuiKey.RightCtrl,
            Keys.Menu or Keys.LMenu => ImGuiKey.LeftAlt,
            Keys.RMenu => ImGuiKey.RightAlt,
            _ => ImGuiKey.None,
        };
    }
}
