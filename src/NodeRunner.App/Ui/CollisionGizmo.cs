using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// 3D handles on the selected collision surfaces (collision tool): an arrow per axis to move them along it, a dot in the
// middle to move them flat, a ring to turn them around the vertical. Ctrl = steps; Escape cancels. The corners shared
// with the surfaces around move too (they stay joined). One undo step when the mouse is released.
internal sealed class CollisionGizmo(AppState state, CollisionLayer layer)
{
    private const float HoverDistance = 10f;   // Pixels.
    private const float ArrowPixels = 80f;
    private const float RingPixels = 62f;
    private const int RingSegments = 48;

    private enum Part { None, Axis, Center, Ring }

    private readonly record struct Handle(Part Part, int Axis = 0);

    private sealed class Drag
    {
        public required Handle Handle;
        public required Vector3 Pivot;
        public required Dictionary<string, Matrix4x4> Placements;
        public float StartParameter;
        public Vector3 StartHit;
        public float StartAngle;
        public float LastAngle;
        public float Angle;
        public Vector2 StartMouse;
        public bool EdgeOn;              // Ring seen edge-on: the mouse slides sideways instead.
        public Matrix4x4 Move = Matrix4x4.Identity;
        public string Feedback = "";
    }

    private Drag? _drag;
    private Handle _hovered;

    // Per frame.
    private Matrix4x4 _viewProjection;
    private Vector2 _origin, _size;
    private Vector3 _rayOrigin, _rayDirection, _pivot;
    private float _unit;                      // World length of one pixel at the pivot.

    private static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    private static readonly Vector4[] AxisColors = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];

    public bool IsHovered => _hovered.Part != Part.None;

    public bool IsDragging => _drag is not null;

    public void Update(bool blocked)
    {
        _hovered = default;
        var selection = state.SelectedCollision;
        if (state.Tool != TransformTool.Collision || selection.Count == 0)
        {
            Cancel();
            return;
        }
        var frame = _drag is { } dragging ? (dragging.Pivot, dragging.Placements)
            : layer.SelectionFrame(selection, state.CollisionSelectionVersion, state.Camera.Position);
        if (frame is not var (pivot, placements) || !SetUpFrame(pivot))
            return;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        if (_drag is { } drag)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                Cancel();
            else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndDrag(drag);
            else
                UpdateDrag(drag, mouse, io.KeyCtrl);
        }
        else if (!blocked && !io.WantCaptureMouse && Inside(mouse))
        {
            _hovered = FindHovered(mouse);
            if (_hovered.Part != Part.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                BeginDrag(_hovered, pivot, placements, mouse);
        }
        if (_drag is not null || _hovered.Part != Part.None)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        Draw(mouse);
    }

    private void Cancel()
    {
        _drag = null;
        state.CollisionDragPreview = null;
    }

    // --- Frame ---

    private bool SetUpFrame(Vector3 pivot)
    {
        var (position, size) = DockLayout.SceneArea;
        _origin = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        _size = new Vector2(MathF.Max(1f, MathF.Round(size.X)), MathF.Max(1f, MathF.Round(size.Y)));
        var camera = state.Camera;
        _viewProjection = camera.ViewProjection(_size.X / _size.Y);
        _pivot = pivot;
        float depth = Vector3.Dot(pivot - camera.Position, camera.Forward);
        if (depth < camera.NearPlane * 2f || !Matrix4x4.Invert(_viewProjection, out var inverse))
            return false;
        _unit = 2f * depth * MathF.Tan(camera.FieldOfView * 0.5f) / _size.Y;

        // From the camera through the mouse on the near plane (depth 1 with the reversed-Z projection).
        var mouse = ImGui.GetIO().MousePos;
        var ndc = new Vector2((mouse.X - _origin.X) / _size.X * 2f - 1f, 1f - (mouse.Y - _origin.Y) / _size.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        _rayOrigin = camera.Position;
        _rayDirection = Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - camera.Position);
        return true;
    }

    private bool Inside(Vector2 p) => p.X >= _origin.X && p.Y >= _origin.Y && p.X < _origin.X + _size.X && p.Y < _origin.Y + _size.Y;

    private bool TryProject(Vector3 point, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), _viewProjection);
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

    private void BeginDrag(Handle handle, Vector3 pivot, Dictionary<string, Matrix4x4> placements, Vector2 mouse)
    {
        var drag = new Drag { Handle = handle, Pivot = pivot, Placements = placements, StartMouse = mouse };
        switch (handle.Part)
        {
            case Part.Axis:
                if (GizmoMath.ClosestAxisParameter(pivot, Axes[handle.Axis], _rayOrigin, _rayDirection) is not { } t)
                    return;
                drag.StartParameter = t;
                break;
            case Part.Center:
                if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, pivot, Vector3.UnitZ) is not { } hit)
                    return;
                drag.StartHit = hit;
                break;
            case Part.Ring:
                drag.EdgeOn = MathF.Abs(_rayDirection.Z) < 0.15f;
                if (!drag.EdgeOn && GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, pivot, Vector3.UnitZ) is { } ringHit)
                    drag.StartAngle = drag.LastAngle = MathF.Atan2(ringHit.Y - pivot.Y, ringHit.X - pivot.X);
                else
                    drag.EdgeOn = true;
                break;
        }
        _drag = drag;
    }

    private void UpdateDrag(Drag drag, Vector2 mouse, bool snap)
    {
        float step = MathF.Max(state.Settings.SnapMove, 0.01f);
        switch (drag.Handle.Part)
        {
            case Part.Axis:
                if (GizmoMath.ClosestAxisParameter(drag.Pivot, Axes[drag.Handle.Axis], _rayOrigin, _rayDirection) is not { } t)
                    return;
                float distance = t - drag.StartParameter;
                if (snap)
                    distance = GizmoMath.Snap(distance, step);
                drag.Move = Matrix4x4.CreateTranslation(Axes[drag.Handle.Axis] * distance);
                drag.Feedback = L($"Déplacement {distance:0.00} m");
                break;
            case Part.Center:
                if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, drag.Pivot, Vector3.UnitZ) is not { } hit)
                    return;
                var delta = hit - drag.StartHit;
                delta.Z = 0;
                if (snap)
                    delta = new Vector3(GizmoMath.Snap(delta.X, step), GizmoMath.Snap(delta.Y, step), 0);
                drag.Move = Matrix4x4.CreateTranslation(delta);
                drag.Feedback = L($"Déplacement {delta.Length():0.00} m");
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
                    degrees = GizmoMath.Snap(degrees, MathF.Max(state.Settings.SnapRotate, 0.1f));
                drag.Move = Matrix4x4.CreateTranslation(-drag.Pivot) * Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180f)
                    * Matrix4x4.CreateTranslation(drag.Pivot);
                drag.Feedback = L($"Rotation {degrees:0.0}°");
                break;
        }
        state.CollisionDragPreview = drag.Move;
    }

    private void EndDrag(Drag drag)
    {
        Cancel();
        if (drag.Move.IsIdentity)
            return;
        string description = drag.Handle.Part == Part.Ring
            ? L("tourner des surfaces de collision")
            : L("déplacer des surfaces de collision");
        state.MoveSelectedCollision(drag.Move, drag.Placements, description);
    }

    // --- Drawing ---

    private void Draw(Vector2 mouse)
    {
        // During a drag the handles follow the surfaces.
        var shift = _drag?.Move ?? Matrix4x4.Identity;
        var center3 = Vector3.Transform(_pivot, shift);
        if (!TryProject(center3, out var center))
            return;
        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);

        uint ringColor = Color(new Handle(Part.Ring), UiTheme.AxisZ);
        for (int i = 0; i < RingSegments; i++)
        {
            var offset = center3 - _pivot;
            if (TryProject(RingPoint(MathF.Tau * i / RingSegments) + offset, out var s0)
                && TryProject(RingPoint(MathF.Tau * (i + 1) / RingSegments) + offset, out var s1))
            {
                draw.AddLine(s0, s1, 0x80000000, 4.5f);
                draw.AddLine(s0, s1, ringColor, 2f);
            }
        }
        for (int a = 0; a < 3; a++)
        {
            if (!TryProject(Tip(a) + (center3 - _pivot), out var tip))
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
