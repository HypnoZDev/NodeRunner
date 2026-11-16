using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// 3D handles over the model workshop's image, on the chosen piece: an arrow per axis to move it along it, a dot to move
// it flat, a ring to turn it around the vertical. Ctrl = steps; Escape cancels. Works in the model's own space.
internal sealed class WorkshopGizmo
{
    private const float HoverDistance = 10f;   // Pixels.
    private const float ArrowPixels = 70f;
    private const float RingPixels = 54f;
    private const int RingSegments = 48;
    private const float MoveStep = 0.05f;      // Metres, with Ctrl.
    private const float TurnStep = 15f;        // Degrees, with Ctrl.

    private enum Part { None, Axis, Center, Ring }

    private readonly record struct Handle(Part Part, int Axis = 0);

    private sealed class Drag
    {
        public required Handle Handle;
        public required Vector3 Pivot;
        public float StartParameter;
        public Vector3 StartHit;
        public float LastAngle;
        public float Angle;
        public Vector2 StartMouse;
        public bool EdgeOn;
        public Matrix4x4 Move = Matrix4x4.Identity;
        public string Feedback = "";
    }

    private Drag? _drag;
    private Handle _hovered;

    // Per frame.
    private Matrix4x4 _viewProjection;
    private Vector2 _origin, _size;
    private Vector3 _offset, _rayOrigin, _rayDirection, _pivot;
    private float _unit;

    private static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    private static readonly Vector4[] AxisColors = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

    public bool IsHovered => _hovered.Part != Part.None;
    public bool IsDragging => _drag is not null;

    // The mouse button went up at the end of a drag in this frame (that release is not a click on the model).
    public bool JustReleased { get; private set; }

    // The move being dragged (model space), for a preview.
    public Matrix4x4? Preview => _drag?.Move;

    public void Cancel() => _drag = null;

    // Once a frame while a piece is chosen. Returns the move (model space) when the mouse is released after a drag.
    public Matrix4x4? Update(Vector2 imageMin, Vector2 imageSize, Matrix4x4 viewProjection, Vector3 modelOffset,
        float fieldOfViewDegrees, Vector3 pivot, bool imageHovered)
    {
        _hovered = default;
        JustReleased = false;
        _origin = imageMin;
        _size = Vector2.Max(imageSize, Vector2.One);
        _viewProjection = viewProjection;
        _offset = modelOffset;
        _pivot = _drag?.Pivot ?? pivot;
        if (!SetUpFrame(fieldOfViewDegrees))
            return null;

        Matrix4x4? done = null;
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        if (_drag is { } drag)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                _drag = null;
            else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _drag = null;
                JustReleased = true;
                if (!drag.Move.IsIdentity)
                    done = drag.Move;
            }
            else
                UpdateDrag(drag, mouse, io.KeyCtrl);
        }
        else if (imageHovered)
        {
            _hovered = FindHovered(mouse);
            if (_hovered.Part != Part.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                BeginDrag(_hovered, mouse);
        }
        if (_drag is not null || _hovered.Part != Part.None)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        Draw(mouse);
        return done;
    }

    // --- Frame ---

    private bool SetUpFrame(float fieldOfViewDegrees)
    {
        var clip = Vector4.Transform(new Vector4(_pivot + _offset, 1f), _viewProjection);
        if (clip.W < 0.01f || !Matrix4x4.Invert(_viewProjection, out var inverse))
            return false;
        _unit = 2f * clip.W * MathF.Tan(fieldOfViewDegrees * MathF.PI / 360f) / _size.Y;

        // Reversed depth with an infinite far plane: 1 is the near plane.
        var at = (ImGui.GetIO().MousePos - _origin) / _size;
        float cx = at.X * 2f - 1f, cy = 1f - at.Y * 2f;
        var near = Vector4.Transform(new Vector4(cx, cy, 1f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(cx, cy, 0.5f, 1f), inverse);
        var from = new Vector3(near.X, near.Y, near.Z) / near.W;
        var to = new Vector3(far.X, far.Y, far.Z) / far.W;
        _rayOrigin = from - _offset;
        _rayDirection = Vector3.Normalize(to - from);
        return true;
    }

    private bool TryProject(Vector3 modelPoint, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(modelPoint + _offset, 1f), _viewProjection);
        if (clip.W < 0.01f)
        {
            screen = default;
            return false;
        }
        screen = _origin + new Vector2((clip.X / clip.W * 0.5f + 0.5f) * _size.X, (0.5f - clip.Y / clip.W * 0.5f) * _size.Y);
        return true;
    }

    private Vector3 Tip(int axis) => _pivot + Axes[axis] * (ArrowPixels * _unit);

    private Vector3 RingPoint(float angle) =>
        _pivot + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * (RingPixels * _unit);

    // --- Hovering ---

    private Handle FindHovered(Vector2 mouse)
    {
        if (!TryProject(_pivot, out var center))
            return default;
        if (Vector2.Distance(mouse, center) < 9f)
            return new Handle(Part.Center);
        var best = default(Handle);
        float bestDistance = HoverDistance;
        for (int a = 0; a < 3; a++)
        {
            if (!TryProject(Tip(a), out var tip))
                continue;
            float distance = GizmoMath.DistanceToSegment(mouse, center, tip);
            if (distance < bestDistance)
                (best, bestDistance) = (new Handle(Part.Axis, a), distance);
        }
        if (best.Part != Part.None)
            return best;
        for (int i = 0; i < RingSegments; i++)
        {
            if (!TryProject(RingPoint(MathF.Tau * i / RingSegments), out var s0)
                || !TryProject(RingPoint(MathF.Tau * (i + 1) / RingSegments), out var s1))
                continue;
            float distance = GizmoMath.DistanceToSegment(mouse, s0, s1);
            if (distance < bestDistance)
                (best, bestDistance) = (new Handle(Part.Ring), distance);
        }
        return best;
    }

    // --- Dragging ---

    private void BeginDrag(Handle handle, Vector2 mouse)
    {
        var drag = new Drag { Handle = handle, Pivot = _pivot, StartMouse = mouse };
        switch (handle.Part)
        {
            case Part.Axis:
                if (GizmoMath.ClosestAxisParameter(_pivot, Axes[handle.Axis], _rayOrigin, _rayDirection) is not { } t)
                    return;
                drag.StartParameter = t;
                break;
            case Part.Center:
                if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, _pivot, Vector3.UnitZ) is not { } hit)
                    return;
                drag.StartHit = hit;
                break;
            case Part.Ring:
                drag.EdgeOn = MathF.Abs(_rayDirection.Z) < 0.15f;
                if (!drag.EdgeOn && GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, _pivot, Vector3.UnitZ) is { } ringHit)
                    drag.LastAngle = MathF.Atan2(ringHit.Y - _pivot.Y, ringHit.X - _pivot.X);
                else
                    drag.EdgeOn = true;
                break;
        }
        _drag = drag;
    }

    private void UpdateDrag(Drag drag, Vector2 mouse, bool snap)
    {
        switch (drag.Handle.Part)
        {
            case Part.Axis:
                if (GizmoMath.ClosestAxisParameter(drag.Pivot, Axes[drag.Handle.Axis], _rayOrigin, _rayDirection) is not { } t)
                    return;
                float distance = t - drag.StartParameter;
                if (snap)
                    distance = GizmoMath.Snap(distance, MoveStep);
                drag.Move = Matrix4x4.CreateTranslation(Axes[drag.Handle.Axis] * distance);
                drag.Feedback = L($"Déplacement {distance:0.000} m");
                break;
            case Part.Center:
                if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, drag.Pivot, Vector3.UnitZ) is not { } hit)
                    return;
                var delta = hit - drag.StartHit;
                delta.Z = 0;
                if (snap)
                    delta = new Vector3(GizmoMath.Snap(delta.X, MoveStep), GizmoMath.Snap(delta.Y, MoveStep), 0);
                drag.Move = Matrix4x4.CreateTranslation(delta);
                drag.Feedback = L($"Déplacement {delta.Length():0.000} m");
                break;
            case Part.Ring:
                if (drag.EdgeOn)
                    drag.Angle = (mouse.X - drag.StartMouse.X) * 0.01f;
                else if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, drag.Pivot, Vector3.UnitZ) is { } ringHit)
                {
                    float angle = MathF.Atan2(ringHit.Y - drag.Pivot.Y, ringHit.X - drag.Pivot.X);
                    float turn = angle - drag.LastAngle;
                    if (turn > MathF.PI) turn -= MathF.Tau;
                    if (turn < -MathF.PI) turn += MathF.Tau;
                    drag.Angle += turn;
                    drag.LastAngle = angle;
                }
                float degrees = drag.Angle * 180f / MathF.PI;
                if (snap)
                    degrees = GizmoMath.Snap(degrees, TurnStep);
                drag.Move = Matrix4x4.CreateTranslation(-drag.Pivot) * Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(drag.Pivot);
                drag.Feedback = L($"Rotation {degrees:0.0}°");
                break;
        }
    }

    // --- Drawing ---

    private void Draw(Vector2 mouse)
    {
        var shift = _drag?.Move ?? Matrix4x4.Identity;
        var center3 = Vector3.Transform(_pivot, shift);
        if (!TryProject(center3, out var center))
            return;
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        var offset = center3 - _pivot;
        uint ringColor = Color(new Handle(Part.Ring), UiTheme.AxisZ);
        for (int i = 0; i < RingSegments; i++)
        {
            if (TryProject(RingPoint(MathF.Tau * i / RingSegments) + offset, out var s0)
                && TryProject(RingPoint(MathF.Tau * (i + 1) / RingSegments) + offset, out var s1))
            {
                draw.AddLine(s0, s1, 0x80000000, 4.5f);
                draw.AddLine(s0, s1, ringColor, 2f);
            }
        }
        for (int a = 0; a < 3; a++)
        {
            if (!TryProject(Tip(a) + offset, out var tip))
                continue;
            uint color = Color(new Handle(Part.Axis, a), AxisColors[a]);
            draw.AddLine(center, tip, 0x80000000, 5f);
            draw.AddLine(center, tip, color, 2.5f);
            var along = Vector2.Distance(center, tip) > 1f ? Vector2.Normalize(tip - center) : -Vector2.UnitY;
            var across = new Vector2(-along.Y, along.X);
            draw.AddTriangleFilled(tip + along * 12f, tip + across * 6f, tip - across * 6f, color);
        }
        uint dot = Color(new Handle(Part.Center), UiTheme.Accent);
        draw.AddCircleFilled(center, 7.5f, 0xC0000000);
        draw.AddCircleFilled(center, 6f, dot);
        draw.PopClipRect();

        if (_drag is { Feedback.Length: > 0 } drag)
        {
            var text = drag.Feedback;
            var at = mouse + new Vector2(16f, 12f);
            var pad = new Vector2(6f, 3f);
            var fore = ImGui.GetForegroundDrawList();
            fore.AddRectFilled(at - pad, at + ImGui.CalcTextSize(text) + pad, 0xD0101010, 4f);
            fore.AddText(at, 0xFFFFFFFF, text);
        }
    }

    private uint Color(Handle handle, Vector4 baseColor)
    {
        bool active = _drag?.Handle == handle || _hovered == handle;
        var color = active ? Vector4.Lerp(baseColor, Vector4.One, 0.5f) with { W = 1f } : baseColor with { W = 1f };
        if (_drag is not null && !active)
            color.W = 0.35f;
        return ImGui.GetColorU32(color);
    }
}
