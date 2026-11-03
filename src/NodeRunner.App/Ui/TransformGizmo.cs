using System.Numerics;
using ImGuiNET;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// 3D handles on the selected objects, drawn over the 3D view (behind the panels): arrows and planes to move them,
// rings to turn them, cubes to scale them.
internal sealed class TransformGizmo(AppState state)
{
    private const float LengthPixels = 110f;   // Length of an arrow on the screen, whatever the distance.
    private const float RingRatio = 0.85f;     // Ring radius, relative to the arrow length.
    private const float HoverDistance = 7f;    // Pixels.
    private const int RingSegments = 72;
    private const float MoveStep = 0.5f;       // Metres, with Ctrl.
    private const float RotateStep = 15f;      // Degrees, with Ctrl.
    private const float ScaleStep = 0.1f;      // With Ctrl.
    private const float MinScale = 0.01f;

    private enum Kind { None, Axis, Plane, Ring, ScaleAxis, ScaleUniform }

    // A handle: its kind and its axis (for a plane, the axis perpendicular to it).
    private readonly record struct Handle(Kind Kind, int Axis = 0);

    private sealed class Drag
    {
        public required MapEntity[] Entities;   // The main one last.
        public required TransformTool Tool;
        public required Handle Handle;
        public required Placement[] Starts;
        public required Vector3 StartPivot;
        public required Vector2 StartMouse;
        public MapEntity Main => Entities[^1];
        public bool IsGroup => Entities.Length > 1;
        public float StartParameter;       // Move along an axis: position of the grabbed point on the axis.
        public Vector3 StartHit;           // Move in a plane: grabbed point in the plane.
        public float StartAngle;           // Turn: angle of the grabbed point on the ring.
        public float LastAngle;            // Turn, by following the mouse in the plane: last angle (to count whole turns).
        public float Angle;                // Turn: angle so far (radians).
        public bool FollowTangent;         // Turn, ring seen edge-on: the mouse slides along the ring instead.
        public Vector2 ScreenDirection;    // Turn (edge-on ring) or scale: direction of the drag on the screen.
        public float ScreenLength;         // Scale: length of the handle on the screen.
        public Vector3? LastDelta;         // Move following the ground: last move asked (the ground is looked up again only when it changes).
        public string Feedback = "";
    }

    private Drag? _drag;
    private Handle _hovered;

    // Per frame.
    private Matrix4x4 _viewProjection;
    private Vector2 _origin, _size;
    private Vector3 _cameraPosition, _viewDirection, _rayOrigin, _rayDirection, _pivot;
    private float _length;                  // World length of an arrow at the object's distance.
    private readonly Vector3[] _axes = new Vector3[3];

    private static readonly Vector4[] AxisColors = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
    private static readonly string[] AxisNames = ["X", "Y", "Z"];

    public bool IsHovered => _hovered.Kind != Kind.None;

    public bool IsDragging => _drag is not null;

    // Handles input and draws the handles.
    public void Update(bool blocked)
    {
        _hovered = default;
        var entities = state.EditableSelection;
        var tool = state.Tool;
        if (_drag is { } current && (!current.Entities.SequenceEqual(entities) || current.Tool != tool))
            EndDrag(commit: current.Entities.SequenceEqual(entities));

        if (entities.Count == 0 || tool == TransformTool.Select || (tool == TransformTool.Scale && entities.Any(e => e.IsInterior)))
            return;
        if (!SetUpFrame(entities, tool))
            return;

        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        if (_drag is { } drag)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                RestoreStart(drag);
                _drag = null;
            }
            else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndDrag(commit: true);
            else
                UpdateDrag(drag, mouse, io.KeyCtrl);
        }
        else if (!blocked && !io.WantCaptureMouse && InsideScene(mouse))
        {
            _hovered = FindHovered(tool, mouse);
            if (_hovered.Kind != Kind.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                BeginDrag(entities, tool, _hovered, mouse);
        }

        if (_drag is not null || _hovered.Kind != Kind.None)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);        Draw(tool, mouse);
    }

    // --- Frame set-up: camera, projection, mouse ray, size of the handles ---

    private bool SetUpFrame(IReadOnlyList<MapEntity> entities, TransformTool tool)
    {
        // Same whole-pixel rectangle as the 3D image (MainForm.SceneViewport).
        var (position, size) = DockLayout.SceneArea;
        _origin = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        _size = new Vector2(MathF.Max(1f, MathF.Round(size.X)), MathF.Max(1f, MathF.Round(size.Y)));

        var camera = state.Camera;
        _cameraPosition = camera.Position;
        _viewDirection = camera.Forward;
        _viewProjection = camera.ViewProjection(_size.X / _size.Y);
        _pivot = entities.Count == 1 ? entities[0].Position : GroupTransform.Pivot(entities.Select(e => e.Position));

        float depth = Vector3.Dot(_pivot - _cameraPosition, _viewDirection);
        if (depth < camera.NearPlane * 2f || !Matrix4x4.Invert(_viewProjection, out var inverse))
            return false;
        _length = LengthPixels * 2f * depth * MathF.Tan(camera.FieldOfView * 0.5f) / _size.Y;

        if (tool == TransformTool.Scale && entities is [var entity])
        {
            _axes[0] = Vector3.Transform(Vector3.UnitX, entity.Orientation);
            _axes[1] = Vector3.Transform(Vector3.UnitY, entity.Orientation);
            _axes[2] = Vector3.Transform(Vector3.UnitZ, entity.Orientation);
        }
        else
        {
            _axes[0] = Vector3.UnitX;
            _axes[1] = Vector3.UnitY;
            _axes[2] = Vector3.UnitZ;
        }

        // Mouse ray: from the camera through the mouse on the near plane (depth 1 with the reversed-Z projection).
        var mouse = ImGui.GetIO().MousePos;
        var ndc = new Vector2((mouse.X - _origin.X) / _size.X * 2f - 1f, 1f - (mouse.Y - _origin.Y) / _size.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        _rayOrigin = _cameraPosition;
        _rayDirection = Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - _cameraPosition);
        return true;
    }

    private bool InsideScene(Vector2 p) =>
        p.X >= _origin.X && p.Y >= _origin.Y && p.X < _origin.X + _size.X && p.Y < _origin.Y + _size.Y;

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

    // An axis pointing (almost) at the camera is hidden: it would be a dot, and dragging along it would jump.
    private bool AxisUsable(int axis) => MathF.Abs(Vector3.Dot(_axes[axis], _viewDirection)) < 0.97f;

    private bool PlaneUsable(int normal) => MathF.Abs(Vector3.Dot(_axes[normal], _viewDirection)) > 0.2f;

    private (Vector3 U, Vector3 V) PlaneAxes(int normal) => (_axes[(normal + 1) % 3], _axes[(normal + 2) % 3]);

    private Vector3[] PlaneCorners(int normal)
    {
        var (u, v) = PlaneAxes(normal);
        float a = 0.22f * _length, b = 0.42f * _length;
        return [_pivot + u * a + v * a, _pivot + u * b + v * a, _pivot + u * b + v * b, _pivot + u * a + v * b];
    }

    private Vector3 RingPoint(int axis, float angle)
    {
        var (u, v) = GizmoMath.PlaneBasis(_axes[axis]);
        return _pivot + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * (_length * RingRatio);
    }

    private bool FacesCamera(Vector3 point) => Vector3.Dot(point - _pivot, _cameraPosition - _pivot) > -0.05f * _length;

    // --- Hovering ---

    private Handle FindHovered(TransformTool tool, Vector2 mouse)
    {
        if (!TryProject(_pivot, out var center))
            return default;
        return tool switch
        {
            TransformTool.Move => FindMoveHandle(mouse, center),
            TransformTool.Rotate => FindRing(mouse),
            TransformTool.Scale => FindScaleHandle(mouse, center),
            _ => default,
        };
    }

    private Handle FindMoveHandle(Vector2 mouse, Vector2 center)
    {
        var best = default(Handle);
        float bestDistance = HoverDistance;
        for (int a = 0; a < 3; a++)
        {
            if (!AxisUsable(a) || !TryProject(_pivot + _axes[a] * _length, out var tip))
                continue;
            float distance = GizmoMath.DistanceToSegment(mouse, center, tip + Vector2.Normalize(tip - center) * 12f);
            if (distance < bestDistance)
                (best, bestDistance) = (new Handle(Kind.Axis, a), distance);
        }
        if (best.Kind != Kind.None)
            return best;

        for (int n = 0; n < 3; n++)
        {
            if (!PlaneUsable(n) || !TryProjectAll(PlaneCorners(n), out var c))
                continue;
            if (GizmoMath.InsideQuad(mouse, c[0], c[1], c[2], c[3]))
                return new Handle(Kind.Plane, n);
        }
        return default;
    }

    private Handle FindRing(Vector2 mouse)
    {
        var best = default(Handle);
        float bestDistance = HoverDistance;
        for (int a = 0; a < 3; a++)
        {
            for (int i = 0; i < RingSegments; i++)
            {
                var p0 = RingPoint(a, MathF.Tau * i / RingSegments);
                var p1 = RingPoint(a, MathF.Tau * (i + 1) / RingSegments);
                if (!FacesCamera((p0 + p1) * 0.5f) || !TryProject(p0, out var s0) || !TryProject(p1, out var s1))
                    continue;
                float distance = GizmoMath.DistanceToSegment(mouse, s0, s1);
                if (distance < bestDistance)
                    (best, bestDistance) = (new Handle(Kind.Ring, a), distance);
            }
        }
        return best;
    }

    private Handle FindScaleHandle(Vector2 mouse, Vector2 center)
    {
        if (Vector2.Distance(mouse, center) < 9f)
            return new Handle(Kind.ScaleUniform);
        var best = default(Handle);
        float bestDistance = HoverDistance + 2f;
        for (int a = 0; a < 3; a++)
        {
            if (!AxisUsable(a) || !TryProject(_pivot + _axes[a] * _length, out var tip))
                continue;
            float distance = GizmoMath.DistanceToSegment(mouse, center, tip);
            if (distance < bestDistance)
                (best, bestDistance) = (new Handle(Kind.ScaleAxis, a), distance);
        }
        return best;
    }

    private bool TryProjectAll(Vector3[] points, out Vector2[] screen)
    {
        screen = new Vector2[points.Length];
        for (int i = 0; i < points.Length; i++)
            if (!TryProject(points[i], out screen[i]))
                return false;
        return true;
    }

    // --- Dragging ---

    private void BeginDrag(IReadOnlyList<MapEntity> entities, TransformTool tool, Handle handle, Vector2 mouse)
    {
        var drag = new Drag
        {
            Entities = [.. entities],
            Tool = tool,
            Handle = handle,
            Starts = [.. entities.Select(Placement.Of)],
            StartPivot = _pivot,
            StartMouse = mouse,
        };
        TryProject(_pivot, out var center);
        switch (handle.Kind)
        {
            case Kind.Axis:
                if (GizmoMath.ClosestAxisParameter(_pivot, _axes[handle.Axis], _rayOrigin, _rayDirection) is not { } t)
                    return;
                drag.StartParameter = t;
                break;
            case Kind.Plane:
                if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, _pivot, _axes[handle.Axis]) is not { } hit)
                    return;
                drag.StartHit = hit;
                break;
            case Kind.Ring:
                var axis = _axes[handle.Axis];
                drag.FollowTangent = MathF.Abs(Vector3.Dot(axis, _viewDirection)) < 0.3f;
                if (!drag.FollowTangent && GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, _pivot, axis) is { } ringHit)
                    drag.StartAngle = drag.LastAngle = GizmoMath.AngleAround(ringHit - _pivot, axis);
                else
                {
                    // Ring seen edge-on: slide the mouse along the ring where it was grabbed.
                    drag.FollowTangent = true;
                    drag.StartAngle = ClosestRingAngle(handle.Axis, mouse);
                    TryProject(RingPoint(handle.Axis, drag.StartAngle), out var s0);
                    TryProject(RingPoint(handle.Axis, drag.StartAngle + 0.05f), out var s1);
                    drag.ScreenDirection = (s1 - s0).LengthSquared() > 1e-6f ? Vector2.Normalize(s1 - s0) : Vector2.UnitX;
                }
                break;
            case Kind.ScaleAxis:
                TryProject(_pivot + _axes[handle.Axis] * _length, out var tip);
                drag.ScreenLength = MathF.Max(20f, Vector2.Distance(center, tip));
                drag.ScreenDirection = Vector2.Normalize(tip - center);
                break;
        }
        _drag = drag;
    }

    private float ClosestRingAngle(int axis, Vector2 mouse)
    {
        float bestAngle = 0f, bestDistance = float.MaxValue;
        for (int i = 0; i < RingSegments; i++)
        {
            float angle = MathF.Tau * i / RingSegments;
            var point = RingPoint(axis, angle);
            if (!FacesCamera(point) || !TryProject(point, out var screen))
                continue;
            float distance = Vector2.Distance(screen, mouse);
            if (distance < bestDistance)
                (bestAngle, bestDistance) = (angle, distance);
        }
        return bestAngle;
    }

    private void UpdateDrag(Drag drag, Vector2 mouse, bool snap)
    {
        var pivot = drag.StartPivot;
        int a = drag.Handle.Axis;
        switch (drag.Handle.Kind)
        {
            case Kind.Axis:
            {
                if (GizmoMath.ClosestAxisParameter(pivot, _axes[a], _rayOrigin, _rayDirection) is not { } t)
                    return;
                float delta = t - drag.StartParameter;
                if (snap) delta = GizmoMath.Snap(delta, MoveStep);
                MoveBy(drag, _axes[a] * delta);
                drag.Feedback = $"{AxisNames[a]}  {delta:+0.00;-0.00;0.00} m" + GroundFeedback(drag);
                break;
            }
            case Kind.Plane:
            {
                // Grazing angle: the point in the plane would fly away.
                if (MathF.Abs(Vector3.Dot(_rayDirection, _axes[a])) < 0.03f
                    || GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, pivot, _axes[a]) is not { } hit)
                    return;
                var delta = hit - drag.StartHit;
                if (snap) delta = new Vector3(GizmoMath.Snap(delta.X, MoveStep), GizmoMath.Snap(delta.Y, MoveStep), GizmoMath.Snap(delta.Z, MoveStep));
                MoveBy(drag, delta);
                var (u, v) = (( a + 1) % 3, (a + 2) % 3);
                drag.Feedback = $"{AxisNames[u]}  {delta[u]:+0.00;-0.00;0.00} m    {AxisNames[v]}  {delta[v]:+0.00;-0.00;0.00} m" + GroundFeedback(drag);
                break;
            }
            case Kind.Ring:
            {
                var axis = _axes[a];
                if (drag.FollowTangent)
                    drag.Angle = Vector2.Dot(mouse - drag.StartMouse, drag.ScreenDirection) / (LengthPixels * RingRatio);
                else if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, pivot, axis) is { } hit)
                {
                    float angle = GizmoMath.AngleAround(hit - pivot, axis);
                    drag.Angle += GizmoMath.WrapAngle(angle - drag.LastAngle);
                    drag.LastAngle = angle;
                }
                float degrees = drag.Angle * 180f / MathF.PI;
                if (snap) degrees = GizmoMath.Snap(degrees, RotateStep);
                var turn = Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);
                for (int i = 0; i < drag.Entities.Length; i++)
                    GroupTransform.Rotate(drag.Starts[i], pivot, turn).ApplyTo(drag.Entities[i]);
                drag.Feedback = $"{AxisNames[a]}  {degrees:+0.0;-0.0;0.0}°";
                break;
            }
            case Kind.ScaleAxis:
            case Kind.ScaleUniform:
            {
                var moved = mouse - drag.StartMouse;
                float factor = drag.Handle.Kind == Kind.ScaleUniform
                    ? 1f + (moved.X - moved.Y) / (2f * LengthPixels) // Right or up: bigger.
                    : 1f + Vector2.Dot(moved, drag.ScreenDirection) / drag.ScreenLength;
                bool changeHorizontal = drag.Handle.Kind == Kind.ScaleUniform || a < 2;
                bool changeVertical = drag.Handle.Kind == Kind.ScaleUniform || a == 2;
                if (drag.IsGroup)
                {
                    // The whole group: spacing and sizes multiplied by the same factor.
                    factor = ScaleValue(factor, snap);
                    for (int i = 0; i < drag.Entities.Length; i++)
                        GroupTransform.Scale(drag.Starts[i], pivot, changeHorizontal ? factor : 1f, changeVertical ? factor : 1f).ApplyTo(drag.Entities[i]);
                    string which = drag.Handle.Kind == Kind.ScaleUniform ? "" : changeVertical ? " verticale" : " horizontale";
                    drag.Feedback = $"Échelle{which} du groupe  x{factor:0.00}";
                    break;
                }
                var start = drag.Starts[0];
                var entity = drag.Entities[0];
                float horizontal = start.Scale.X, vertical = start.Scale.Z;
                if (changeHorizontal) horizontal = ScaleValue(start.Scale.X * factor, snap);
                if (changeVertical) vertical = ScaleValue(start.Scale.Z * factor, snap);
                entity.SetPlacement(start.Position, start.Orientation, new Vector3(horizontal, horizontal, vertical));
                drag.Feedback = drag.Handle.Kind == Kind.ScaleUniform ? $"Échelle  {horizontal:0.000} / {vertical:0.000}"
                    : changeVertical ? $"Échelle verticale  {vertical:0.000}" : $"Échelle horizontale  {horizontal:0.000}";
                break;
            }
        }
    }

    private bool FollowsGround(Drag drag) =>
        state.Settings.FollowGround
        && (drag.Handle.Kind == Kind.Axis && drag.Handle.Axis < 2 || drag.Handle.Kind == Kind.Plane && drag.Handle.Axis == 2);

    // Moves the dragged objects by delta; following the ground, the height of each is then taken from the collision
    // below it.
    private void MoveBy(Drag drag, Vector3 delta)
    {
        bool followGround = FollowsGround(drag);
        if (followGround && drag.LastDelta == delta)
            return;
        drag.LastDelta = delta;
        for (int i = 0; i < drag.Entities.Length; i++)
        {
            var entity = drag.Entities[i];
            var start = drag.Starts[i];
            var target = start.Position + delta;
            if (!followGround)
            {
                entity.SetPlacement(target, start.Orientation, start.Scale);
                continue;
            }
            entity.SetPlacement(target with { Z = entity.Position.Z }, start.Orientation, start.Scale);
            if (state.GroundPosition(entity, state.World.Maps?.GetArchetype(entity.ArchetypeHash)) is { } ground)
                entity.SetPlacement(ground, start.Orientation, start.Scale);
        }
    }

    private string GroundFeedback(Drag drag) =>
        FollowsGround(drag) ? $"    Z  {drag.Main.Position.Z - drag.Starts[^1].Position.Z:+0.00;-0.00;0.00} m (sol)" : "";

    private static void RestoreStart(Drag drag)
    {
        for (int i = 0; i < drag.Entities.Length; i++)
            drag.Starts[i].ApplyTo(drag.Entities[i]);
    }

    private static float ScaleValue(float value, bool snap) => MathF.Max(MinScale, snap ? GizmoMath.Snap(value, ScaleStep) : value);

    private void EndDrag(bool commit)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        var ends = drag.Entities.Select(Placement.Of).ToArray();
        if (!commit || ends.SequenceEqual(drag.Starts))
            return;
        string name = drag.IsGroup ? $"{drag.Entities.Length} objets"
            : state.World.Names?.Describe(drag.Main.ArchetypeHash) ?? $"0x{drag.Main.ArchetypeHash:X8}";
        string verb = drag.Tool switch
        {
            TransformTool.Rotate => "Tourner",
            TransformTool.Scale => "Redimensionner",
            _ => "Déplacer",
        };
        state.History.Add(drag.IsGroup
            ? new GroupPlacementEdit(drag.Entities, drag.Starts, ends, $"{verb} {name}")
            : new PlacementEdit(drag.Main, drag.Starts[0], ends[0], $"{verb} {name}"));
    }

    // --- Drawing ---

    private static uint Color(Vector4 color, float alpha = 1f) => ImGui.GetColorU32(color with { W = color.W * alpha });

    private static Vector4 Highlight(Vector4 color) => Vector4.Lerp(color, Vector4.One, 0.45f) with { W = 1f };

    private const uint Shadow = 0x80000000; // Black at 50 % (ImGui colors are ABGR).

    private Vector4 HandleColor(Handle handle, Vector4 baseColor) =>
        handle == _hovered || handle == _drag?.Handle ? Highlight(baseColor) : baseColor;

    private bool Shown(Handle handle) => _drag is null || _drag.Handle == handle;

    private void Draw(TransformTool tool, Vector2 mouse)
    {
        if (!TryProject(_pivot, out var center))
            return;
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(_origin, _origin + _size, false);

        switch (tool)
        {
            case TransformTool.Move: DrawMove(drawList, center); break;
            case TransformTool.Rotate: DrawRotate(drawList); break;
            case TransformTool.Scale: DrawScale(drawList, center); break;
        }
        drawList.AddCircleFilled(center, 3f, Shadow);
        drawList.AddCircleFilled(center, 2f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f)));

        if (_drag is { Feedback.Length: > 0 } drag)
            DrawFeedback(drawList, mouse + new Vector2(18f, 16f), drag.Feedback);
        drawList.PopClipRect();
    }

    private void Line(ImDrawListPtr drawList, Vector2 a, Vector2 b, uint color, float thickness)
    {
        drawList.AddLine(a, b, Shadow, thickness + 2f);
        drawList.AddLine(a, b, color, thickness);
    }

    private void DrawMove(ImDrawListPtr drawList, Vector2 center)
    {
        // Planes first (under the arrows): small squares, colored like the axis perpendicular to them.
        for (int n = 0; n < 3; n++)
        {
            var handle = new Handle(Kind.Plane, n);
            if (!Shown(handle) || !PlaneUsable(n) || !TryProjectAll(PlaneCorners(n), out var c))
                continue;
            var color = HandleColor(handle, AxisColors[n]);
            bool lit = handle == _hovered || handle == _drag?.Handle;
            drawList.AddQuadFilled(c[0], c[1], c[2], c[3], Color(color, lit ? 0.55f : 0.28f));
            drawList.AddQuad(c[0], c[1], c[2], c[3], Color(color, 0.9f), 1f);
        }

        for (int a = 0; a < 3; a++)
        {
            var handle = new Handle(Kind.Axis, a);
            if (!Shown(handle) || !AxisUsable(a) || !TryProject(_pivot + _axes[a] * _length, out var tip))
                continue;
            if (_drag?.Handle == handle)
                DrawGuide(drawList, _drag.StartPivot, _axes[a], AxisColors[a]);
            uint color = Color(HandleColor(handle, AxisColors[a]));
            var direction = Vector2.Normalize(tip - center);
            var normal = new Vector2(-direction.Y, direction.X);
            Line(drawList, center, tip, color, 2.5f);
            var point = tip + direction * 13f;
            drawList.AddTriangleFilled(point + direction * 1.5f, tip + normal * 6.5f, tip - normal * 6.5f, Shadow);
            drawList.AddTriangleFilled(point, tip + normal * 5.5f, tip - normal * 5.5f, color);
        }
    }

    private void DrawGuide(ImDrawListPtr drawList, Vector3 start, Vector3 axis, Vector4 color)
    {
        float reach = _length * 25f;
        if (TryProject(start - axis * reach, out var a) && TryProject(start + axis * reach, out var b))
            drawList.AddLine(a, b, Color(color, 0.35f), 1f);
    }

    private void DrawRotate(ImDrawListPtr drawList)
    {
        // The ring facing the camera most is drawn last (on top).
        foreach (int a in new[] { 0, 1, 2 }.OrderBy(i => MathF.Abs(Vector3.Dot(_axes[i], _viewDirection))))
        {
            var handle = new Handle(Kind.Ring, a);
            if (!Shown(handle))
                continue;
            var color = HandleColor(handle, AxisColors[a]);
            for (int i = 0; i < RingSegments; i++)
            {
                var p0 = RingPoint(a, MathF.Tau * i / RingSegments);
                var p1 = RingPoint(a, MathF.Tau * (i + 1) / RingSegments);
                if (!TryProject(p0, out var s0) || !TryProject(p1, out var s1))
                    continue;
                if (FacesCamera((p0 + p1) * 0.5f))
                    Line(drawList, s0, s1, Color(color), 2.5f);
                else
                    drawList.AddLine(s0, s1, Color(color, 0.22f), 1.5f);
            }
        }

        // Swept angle while turning.
        if (_drag is { Handle.Kind: Kind.Ring } drag && TryProject(_pivot, out var center))
        {
            float degrees = drag.Angle * 180f / MathF.PI;
            if (ImGui.GetIO().KeyCtrl) degrees = GizmoMath.Snap(degrees, RotateStep);
            float sweep = Math.Clamp(degrees * MathF.PI / 180f, -MathF.Tau, MathF.Tau);
            int steps = Math.Max(2, (int)(MathF.Abs(sweep) / MathF.Tau * RingSegments));
            uint fill = Color(AxisColors[drag.Handle.Axis], 0.18f);
            Vector2? previous = null;
            for (int i = 0; i <= steps; i++)
            {
                if (!TryProject(RingPoint(drag.Handle.Axis, drag.StartAngle + sweep * i / steps), out var s))
                    continue;
                if (previous is { } p)
                    drawList.AddTriangleFilled(center, p, s, fill);
                previous = s;
            }
            if (TryProject(RingPoint(drag.Handle.Axis, drag.StartAngle), out var from))
                drawList.AddLine(center, from, Color(Vector4.One, 0.5f), 1f);
            if (previous is { } to)
                drawList.AddLine(center, to, Color(Vector4.One, 0.9f), 1.5f);
        }
    }

    private void DrawScale(ImDrawListPtr drawList, Vector2 center)
    {
        for (int a = 0; a < 3; a++)
        {
            var handle = new Handle(Kind.ScaleAxis, a);
            // X and Y change the same horizontal scale: both light up together.
            bool lit = _hovered.Kind == Kind.ScaleAxis && SameScale(_hovered.Axis, a)
                || _drag?.Handle.Kind == Kind.ScaleAxis && SameScale(_drag.Handle.Axis, a)
                || _hovered.Kind == Kind.ScaleUniform || _drag?.Handle.Kind == Kind.ScaleUniform;
            bool shown = _drag is null || lit;
            if (!shown || !AxisUsable(a) || !TryProject(_pivot + _axes[a] * _length, out var tip))
                continue;
            uint color = Color(lit ? Highlight(AxisColors[a]) : AxisColors[a]);
            Line(drawList, center, tip, color, 2.5f);
            drawList.AddRectFilled(tip - new Vector2(6f), tip + new Vector2(6f), Shadow, 2f);
            drawList.AddRectFilled(tip - new Vector2(5f), tip + new Vector2(5f), color, 2f);
        }

        var uniform = new Handle(Kind.ScaleUniform);
        if (Shown(uniform))
        {
            uint color = Color(HandleColor(uniform, new Vector4(0.85f, 0.86f, 0.9f, 1f)));
            drawList.AddRectFilled(center - new Vector2(7f), center + new Vector2(7f), Shadow, 2f);
            drawList.AddRect(center - new Vector2(6f), center + new Vector2(6f), color, 2f, ImDrawFlags.None, 2f);
        }
    }

    private static bool SameScale(int a, int b) => (a < 2) == (b < 2);

    private static void DrawFeedback(ImDrawListPtr drawList, Vector2 position, string text)
    {
        var size = ImGui.CalcTextSize(text);
        var padding = new Vector2(7f, 4f);
        drawList.AddRectFilled(position, position + size + padding * 2f, ImGui.GetColorU32(new Vector4(0.09f, 0.10f, 0.12f, 0.92f)), 4f);
        drawList.AddText(position + padding, ImGui.GetColorU32(new Vector4(0.92f, 0.93f, 0.96f, 1f)), text);
    }
}
