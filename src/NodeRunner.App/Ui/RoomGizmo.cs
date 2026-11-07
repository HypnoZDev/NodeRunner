using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// 3D handles on the selected room or portal of an interior. Room: a square on each side to stretch it, a dot in the
// middle to move it flat, an arrow to raise it. Portal: a dot on each corner, a square on each edge, a dot in the
// middle to move it in its plane, an arrow to push it through the wall. Ctrl = steps of 0.25 m; Escape cancels.
internal sealed class RoomGizmo(AppState state)
{
    private const float HoverDistance = 10f;   // Pixels.
    private const float ArrowPixels = 55f;
    private float SnapStep => MathF.Max(state.Settings.SnapMove, 0.01f); // Metres, with Ctrl.
    private const float MinSize = 0.1f;

    private enum Part { None, Face, Center, Arrow, Corner, Edge }

    // Face: axis * 2 + (1 for the max side); Corner and Edge: corner number (an edge goes from it to the next one).
    private readonly record struct Handle(Part Part, int Index = 0);

    private sealed class Drag
    {
        public required RoomSelection Target;
        public required bool IsPortal;
        public required Handle Handle;
        public InteriorRoom? RoomStart;
        public InteriorPortal? PortalStart;
        public Vector3 Origin;         // Interior space: the grabbed handle.
        public Vector3 Direction;      // Along a line: its direction; in a plane: its normal.
        public float StartParameter;
        public Vector3 StartHit;
        public string Feedback = "";
    }

    private Drag? _drag;
    private Handle _hovered;

    // Per frame.
    private Matrix4x4 _viewProjection, _toWorld;
    private Vector2 _origin, _size;
    private Vector3 _rayOrigin, _rayDirection; // Interior space.

    public bool IsHovered => _hovered.Part != Part.None;

    public bool IsDragging => _drag is not null;

    public void Update(bool blocked)
    {
        _hovered = default;
        var room = state.SelectedRoom;
        var portal = state.SelectedPortal;
        var target = room ?? portal;
        bool isPortal = room is null;
        if (_drag is { } current && (current.Target != target || current.IsPortal != isPortal))
            _drag = null;
        if (target is null || state.Tool == TransformTool.Collision || state.World.Maps is not { } library
            || library.GetArchetype(target.Interior.ArchetypeHash) is not { } archetype || !SetUpFrame(target.Interior))
            return;
        if (isPortal ? target.Index >= archetype.InteriorPortals.Count || archetype.InteriorPortals[target.Index].Corners.Count < 3
                     : target.Index < 1 || target.Index >= archetype.InteriorRooms.Count)
            return;

        var handles = isPortal ? PortalHandles(archetype.InteriorPortals[target.Index]) : RoomHandles(archetype.InteriorRooms[target.Index]);
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        if (_drag is { } drag)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                Restore(library, drag);
                _drag = null;
            }
            else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                EndDrag(library, archetype, drag);
            else
                UpdateDrag(library, archetype, drag, io.KeyCtrl);
        }
        else if (!blocked && !io.WantCaptureMouse && Inside(mouse))
        {
            _hovered = FindHovered(handles, mouse);
            if (_hovered.Part != Part.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                BeginDrag(target, isPortal, archetype, handles.First(h => h.Handle == _hovered));
        }
        if (_drag is not null || _hovered.Part != Part.None)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        Draw(handles, mouse);
    }

    // --- Frame ---

    private bool SetUpFrame(MapEntity interior)
    {
        var (position, size) = DockLayout.SceneArea;
        _origin = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        _size = new Vector2(MathF.Max(1f, MathF.Round(size.X)), MathF.Max(1f, MathF.Round(size.Y)));
        _viewProjection = state.Camera.ViewProjection(_size.X / _size.Y);
        _toWorld = interior.Transform;
        if (!Matrix4x4.Invert(_viewProjection, out var inverse) || !Matrix4x4.Invert(_toWorld, out var toInterior))
            return false;
        // From the camera through the mouse on the near plane (depth 1 with the reversed-Z projection).
        var mouse = ImGui.GetIO().MousePos;
        var ndc = new Vector2((mouse.X - _origin.X) / _size.X * 2f - 1f, 1f - (mouse.Y - _origin.Y) / _size.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        var camera = state.Camera.Position;
        var direction = Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - camera);
        _rayOrigin = Vector3.Transform(camera, toInterior);
        _rayDirection = Vector3.Normalize(Vector3.TransformNormal(direction, toInterior));
        return true;
    }

    private bool Inside(Vector2 p) => p.X >= _origin.X && p.Y >= _origin.Y && p.X < _origin.X + _size.X && p.Y < _origin.Y + _size.Y;

    // A point of the interior on the screen.
    private bool TryProject(Vector3 local, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(Vector3.Transform(local, _toWorld), 1f), _viewProjection);
        if (clip.W < 0.01f)
        {
            screen = default;
            return false;
        }
        screen = _origin + new Vector2((clip.X / clip.W * 0.5f + 0.5f) * _size.X, (0.5f - clip.Y / clip.W * 0.5f) * _size.Y);
        return true;
    }

    // --- Handles ---

    // A handle, where it is (interior space) and how it moves: along Direction (Line) or in the plane of normal Direction.
    private sealed record Place(Handle Handle, Vector3 Point, Vector3 Direction, bool Line, Vector4 Color);

    private List<Place> RoomHandles(InteriorRoom room)
    {
        var center = (room.BoundsMin + room.BoundsMax) / 2;
        var list = new List<Place>();
        Vector4[] colors = [UiTheme.AxisX, UiTheme.AxisY, UiTheme.AxisZ];
        for (int f = 0; f < 6; f++)
        {
            int axis = f / 2;
            var point = center;
            point[axis] = f % 2 == 1 ? room.BoundsMax[axis] : room.BoundsMin[axis];
            var direction = Vector3.Zero;
            direction[axis] = 1f;
            list.Add(new Place(new Handle(Part.Face, f), point, direction, true, colors[axis]));
        }
        list.Add(new Place(new Handle(Part.Center), center, Vector3.UnitZ, false, UiTheme.Accent));
        list.Add(new Place(new Handle(Part.Arrow), ArrowTip(center, Vector3.UnitZ), Vector3.UnitZ, true, UiTheme.AxisZ));
        return list;
    }

    private List<Place> PortalHandles(InteriorPortal portal)
    {
        var corners = portal.Corners;
        var center = corners.Aggregate(Vector3.Zero, (sum, c) => sum + c) / corners.Count;
        var normal = Normal(corners);
        var list = new List<Place>();
        for (int c = 0; c < corners.Count; c++)
        {
            var next = corners[(c + 1) % corners.Count];
            list.Add(new Place(new Handle(Part.Corner, c), corners[c], normal, false, UiTheme.Accent));
            var side = Vector3.Cross(normal, next - corners[c]);
            var outward = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : Vector3.UnitZ;
            var middle = (corners[c] + next) / 2;
            if (Vector3.Dot(outward, middle - center) < 0)
                outward = -outward;
            list.Add(new Place(new Handle(Part.Edge, c), middle, outward, true, UiTheme.Accent));
        }
        list.Add(new Place(new Handle(Part.Center), center, normal, false, UiTheme.Accent));
        list.Add(new Place(new Handle(Part.Arrow), ArrowTip(center, normal), normal, true, UiTheme.AxisZ));
        return list;
    }

    private static Vector3 Normal(IReadOnlyList<Vector3> corners)
    {
        // Newell's method: fine for any polygon, even slightly bent.
        var n = Vector3.Zero;
        for (int i = 0; i < corners.Count; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Count];
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    // The tip of an arrow of ArrowPixels on the screen from a point, along a direction of the interior.
    private Vector3 ArrowTip(Vector3 from, Vector3 direction)
    {
        if (!TryProject(from, out var a) || !TryProject(from + direction, out var b) || Vector2.Distance(a, b) < 0.5f)
            return from + direction;
        return from + direction * (ArrowPixels / Vector2.Distance(a, b));
    }

    private Handle FindHovered(List<Place> handles, Vector2 mouse)
    {
        var best = default(Handle);
        float bestDistance = HoverDistance;
        foreach (var place in handles)
        {
            if (!TryProject(place.Point, out var screen))
                continue;
            float distance = Vector2.Distance(mouse, screen);
            if (distance < bestDistance)
                (best, bestDistance) = (place.Handle, distance);
        }
        return best;
    }

    // --- Dragging ---

    private void BeginDrag(RoomSelection target, bool isPortal, Archetype archetype, Place place)
    {
        var drag = new Drag
        {
            Target = target,
            IsPortal = isPortal,
            Handle = place.Handle,
            RoomStart = isPortal ? null : archetype.InteriorRooms[target.Index],
            PortalStart = isPortal ? archetype.InteriorPortals[target.Index] : null,
            Origin = place.Point,
            Direction = place.Direction,
        };
        if (place.Line)
        {
            if (GizmoMath.ClosestAxisParameter(place.Point, place.Direction, _rayOrigin, _rayDirection) is not { } t)
                return;
            drag.StartParameter = t;
        }
        else
        {
            if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, place.Point, place.Direction) is not { } hit)
                return;
            drag.StartHit = hit;
        }
        _drag = drag;
    }

    // How far the mouse took the handle (interior space), null when the ray misses.
    private Vector3? Moved(Drag drag, bool snap)
    {
        bool line = drag.Handle.Part is Part.Face or Part.Arrow or Part.Edge;
        if (line)
        {
            if (GizmoMath.ClosestAxisParameter(drag.Origin, drag.Direction, _rayOrigin, _rayDirection) is not { } t)
                return null;
            float distance = t - drag.StartParameter;
            return drag.Direction * (snap ? GizmoMath.Snap(distance, SnapStep) : distance);
        }
        if (GizmoMath.IntersectPlane(_rayOrigin, _rayDirection, drag.Origin, drag.Direction) is not { } hit)
            return null;
        var delta = hit - drag.StartHit;
        return snap ? new Vector3(GizmoMath.Snap(delta.X, SnapStep), GizmoMath.Snap(delta.Y, SnapStep), GizmoMath.Snap(delta.Z, SnapStep)) : delta;
    }

    private void UpdateDrag(MapLibrary library, Archetype archetype, Drag drag, bool snap)
    {
        if (Moved(drag, snap) is not { } delta)
            return;
        uint hash = drag.Target.Interior.ArchetypeHash;
        if (drag.RoomStart is { } room)
        {
            var min = room.BoundsMin;
            var max = room.BoundsMax;
            if (drag.Handle.Part == Part.Face)
            {
                int axis = drag.Handle.Index / 2;
                if (drag.Handle.Index % 2 == 1)
                    max[axis] = MathF.Max(max[axis] + delta[axis], min[axis] + MinSize);
                else
                    min[axis] = MathF.Min(min[axis] + delta[axis], max[axis] - MinSize);
            }
            else
            {
                min += delta;
                max += delta;
            }
            library.SetRoom(hash, drag.Target.Index, room with { BoundsMin = min, BoundsMax = max });
            var size = max - min;
            drag.Feedback = drag.Handle.Part == Part.Face
                ? $"{size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m"
                : L($"Déplacement {delta.Length():0.00} m");
        }
        else if (drag.PortalStart is { } portal)
        {
            var corners = portal.Corners.ToArray();
            switch (drag.Handle.Part)
            {
                case Part.Corner:
                    corners[drag.Handle.Index] += delta;
                    break;
                case Part.Edge:
                    corners[drag.Handle.Index] += delta;
                    corners[(drag.Handle.Index + 1) % corners.Length] += delta;
                    break;
                default:
                    for (int c = 0; c < corners.Length; c++)
                        corners[c] += delta;
                    break;
            }
            library.SetPortal(hash, drag.Target.Index, portal with { Corners = corners });
            drag.Feedback = corners.Length == 4 && drag.Handle.Part is Part.Corner or Part.Edge
                ? $"{Vector3.Distance(corners[0], corners[1]):0.00} x {Vector3.Distance(corners[1], corners[2]):0.00} m"
                : L($"Déplacement {delta.Length():0.00} m");
        }
    }

    private void Restore(MapLibrary library, Drag drag)
    {
        uint hash = drag.Target.Interior.ArchetypeHash;
        if (drag.RoomStart is { } room)
            library.SetRoom(hash, drag.Target.Index, room);
        else if (drag.PortalStart is { } portal)
            library.SetPortal(hash, drag.Target.Index, portal);
    }

    private void EndDrag(MapLibrary library, Archetype archetype, Drag drag)
    {
        _drag = null;
        var interior = drag.Target.Interior;
        int index = drag.Target.Index;
        string verb = drag.Handle.Part is Part.Center or Part.Arrow ? L("Déplacer") : L("Redimensionner");
        if (drag.RoomStart is { } room && archetype.InteriorRooms[index] is var roomEnd && roomEnd != room)
            state.History.Add(new RoomEdit(library, interior, index, room, roomEnd, L($"{verb} la salle {index}")));
        else if (drag.PortalStart is { } portal && archetype.InteriorPortals[index] is var portalEnd && !portalEnd.SameAs(portal))
            state.History.Add(new PortalEdit(library, interior, index, portal, portalEnd, L($"{verb} le portail {index}")));
    }

    // --- Drawing ---

    private void Draw(List<Place> handles, Vector2 mouse)
    {
        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        var arrow = handles.Find(h => h.Handle.Part == Part.Arrow);
        var center = handles.Find(h => h.Handle.Part == Part.Center);
        if (arrow is not null && center is not null && TryProject(center.Point, out var from) && TryProject(arrow.Point, out var to))
        {
            uint color = Color(arrow);
            draw.AddLine(from, to, 0x80000000, 5f);
            draw.AddLine(from, to, color, 2.5f);
            var along = Vector2.Distance(from, to) > 1f ? Vector2.Normalize(to - from) : -Vector2.UnitY;
            var across = new Vector2(-along.Y, along.X);
            draw.AddTriangleFilled(to + along * 12f, to + across * 6f, to - across * 6f, color);
        }
        foreach (var place in handles)
        {
            if (place.Handle.Part == Part.Arrow || !TryProject(place.Point, out var screen))
                continue;
            uint color = Color(place);
            bool round = place.Handle.Part is Part.Center or Part.Corner;
            float radius = round ? 6f : 5f;
            if (round)
            {
                draw.AddCircleFilled(screen, radius + 1.5f, 0xC0000000);
                draw.AddCircleFilled(screen, radius, color);
            }
            else
            {
                draw.AddRectFilled(screen - new Vector2(radius + 1.5f), screen + new Vector2(radius + 1.5f), 0xC0000000);
                draw.AddRectFilled(screen - new Vector2(radius), screen + new Vector2(radius), color);
            }
        }
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

    private uint Color(Place place)
    {
        bool active = _drag?.Handle == place.Handle || _hovered == place.Handle;
        var color = active ? Vector4.Lerp(place.Color, Vector4.One, 0.5f) with { W = 1f } : place.Color with { W = 1f };
        if (_drag is not null && !active)
            color.W = 0.35f;
        return ImGui.GetColorU32(color);
    }
}
