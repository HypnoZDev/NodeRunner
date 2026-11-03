using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;

namespace NodeRunner.App.Ui;

/// <summary>
/// While the "Rooms and portals" layer is shown: the room or portal under the mouse in the 3D view is outlined, with its
/// details next to the mouse. A portal under the mouse wins (a small target, aimed at on purpose); otherwise the room
/// holding the camera, or else the first one the mouse ray enters.
/// </summary>
internal sealed class RoomHover(AppState state, RoomLayer layer)
{
    private static readonly int[] BoxEdges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];

    private Vector2 _origin, _size;
    private Matrix4x4 _viewProjection;

    /// <param name="blocked">Another element of the 3D view (handles, a popup...) has the mouse.</param>
    public void Update(bool blocked)
    {
        bool showRooms = state.IsLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms));
        bool showInner = state.IsLayerVisible(RoomLayer.LayerName(RoomLayerPart.InnerPortals));
        bool showOutside = state.IsLayerVisible(RoomLayer.LayerName(RoomLayerPart.OutsidePortals));
        if (blocked || !showRooms && !showInner && !showOutside)
            return;
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || ImGui.IsMouseDown(ImGuiMouseButton.Right))
            return;

        var (position, size) = DockLayout.SceneArea;
        _origin = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        _size = new Vector2(MathF.Max(1f, MathF.Round(size.X)), MathF.Max(1f, MathF.Round(size.Y)));
        var mouse = io.MousePos;
        if (mouse.X < _origin.X || mouse.Y < _origin.Y || mouse.X >= _origin.X + _size.X || mouse.Y >= _origin.Y + _size.Y)
            return;

        var camera = state.Camera;
        _viewProjection = camera.ViewProjection(_size.X / _size.Y);
        if (!Matrix4x4.Invert(_viewProjection, out var inverse))
            return;
        // Mouse ray: from the camera through the mouse on the near plane (depth 1 with the reversed-Z projection).
        var ndc = new Vector2((mouse.X - _origin.X) / _size.X * 2f - 1f, 1f - (mouse.Y - _origin.Y) / _size.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        var direction = Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - camera.Position);

        PortalBox? portal = null;
        float portalDistance = float.MaxValue;
        foreach (var candidate in layer.Portals)
        {
            if (!(candidate.LeadsOutside ? showOutside : showInner))
                continue;
            if (Hit(candidate.Corners, camera.Position, direction) is { } distance && distance < portalDistance)
            {
                portal = candidate;
                portalDistance = distance;
            }
        }
        if (portal is not null)
        {
            ShowPortal(portal);
            return;
        }
        if (!showRooms)
            return;

        RoomBox? best = null;
        float bestEntry = float.MaxValue;
        foreach (var room in layer.Rooms)
        {
            if (Entry(room, camera.Position, direction) is not { } entry)
                continue;
            if (entry < bestEntry - 0.01f || MathF.Abs(entry - bestEntry) <= 0.01f && room.Volume < best!.Volume)
            {
                best = room;
                bestEntry = entry;
            }
        }
        if (best is not null)
            Show(best);
    }

    /// <summary>Distance along the ray to the flat polygon <paramref name="corners"/> (a portal), null if it misses.</summary>
    private static float? Hit(Vector3[] corners, Vector3 origin, Vector3 direction)
    {
        for (int c = 1; c + 1 < corners.Length; c++)
        {
            // Möller-Trumbore on each triangle of the fan, both faces.
            var a = corners[0];
            var e1 = corners[c] - a;
            var e2 = corners[c + 1] - a;
            var p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-8f)
                continue;
            var t0 = origin - a;
            float u = Vector3.Dot(t0, p) / det;
            if (u < 0f || u > 1f)
                continue;
            var q = Vector3.Cross(t0, e1);
            float v = Vector3.Dot(direction, q) / det;
            if (v < 0f || u + v > 1f)
                continue;
            float t = Vector3.Dot(e2, q) / det;
            if (t > 0f)
                return t;
        }
        return null;
    }

    private void ShowPortal(PortalBox portal)
    {
        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        var screen = new Vector2[portal.Corners.Length];
        bool allVisible = true;
        for (int i = 0; i < screen.Length; i++)
            allVisible &= TryProject(portal.Corners[i], out screen[i]);
        if (allVisible)
        {
            var color = RoomLayer.Color(portal.LeadsOutside ? RoomLayerPart.OutsidePortals : RoomLayerPart.InnerPortals);
            draw.AddConvexPolyFilled(ref screen[0], screen.Length, Widgets.Col(color with { W = 0.3f }));
            draw.AddPolyline(ref screen[0], screen.Length, Widgets.Col(color with { W = 1f }), ImDrawFlags.Closed, 2.5f);
        }
        draw.PopClipRect();

        var names = state.World.Names ?? new HashNames();
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(portal.LeadsOutside ? $"Portail {portal.Index} vers l'extérieur" : $"Portail {portal.Index} entre deux salles");
        ImGui.TextColored(UiTheme.TextMuted, $"Relie : {Side(portal.Portal.RoomFrom, portal.RoomFrom)}  et  {Side(portal.Portal.RoomTo, portal.RoomTo)}");
        ImGui.TextColored(UiTheme.TextMuted, $"Intérieur : {names.Describe(portal.Interior.ArchetypeHash)}");
        if (portal.ObjectCount > 0)
            ImGui.TextColored(UiTheme.TextMuted, portal.ObjectCount == 1 ? "1 objet attaché (porte...)" : $"{portal.ObjectCount} objets attachés (portes...)");
        ImGui.TextColored(UiTheme.TextFaint, $"Drapeaux : 0x{portal.Portal.Flags:X}");
        ImGui.EndTooltip();
    }

    private static string Side(int room, string name) => room == 0 ? "l'extérieur" : $"la salle {name}";

    /// <summary>Distance along the ray at which it enters the room's box (0 when the camera is inside), null if it misses.</summary>
    private static float? Entry(RoomBox room, Vector3 origin, Vector3 direction)
    {
        if (!Matrix4x4.Invert(room.Transform, out var toRoom))
            return null;
        var o = Vector3.Transform(origin, toRoom);
        var d = Vector3.TransformNormal(direction, toRoom);
        float enter = 0f, exit = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = o[axis], da = d[axis], min = room.Room.BoundsMin[axis], max = room.Room.BoundsMax[axis];
            if (MathF.Abs(da) < 1e-6f)
            {
                if (oa < min || oa > max)
                    return null;
                continue;
            }
            float t1 = (min - oa) / da, t2 = (max - oa) / da;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            enter = MathF.Max(enter, t1);
            exit = MathF.Min(exit, t2);
            if (enter > exit)
                return null;
        }
        return enter;
    }

    private void Show(RoomBox room)
    {
        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        var min = room.Room.BoundsMin;
        var max = room.Room.BoundsMax;
        var screen = new Vector2[8];
        var visible = new bool[8];
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            visible[i] = TryProject(Vector3.Transform(corner, room.Transform), out screen[i]);
        }
        uint color = Widgets.Col(new Vector4(0.9f, 0.7f, 1f, 1f));
        for (int e = 0; e < BoxEdges.Length; e += 2)
        {
            int a = BoxEdges[e], b = BoxEdges[e + 1];
            if (visible[a] && visible[b])
                draw.AddLine(screen[a], screen[b], color, 2f);
        }
        draw.PopClipRect();

        var names = state.World.Names ?? new HashNames();
        string name = room.Room.Name.Length > 0 ? room.Room.Name : "(sans nom)";
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"Salle {room.Index} : {name}");
        ImGui.TextColored(UiTheme.TextMuted, $"Intérieur : {names.Describe(room.Interior.ArchetypeHash)}");
        ImGui.TextColored(UiTheme.TextMuted, $"{room.PortalCount} portails · {room.ObjectCount} objets");
        if (room.Room.HidesExterior)
            ImGui.TextColored(UiTheme.TextMuted, "L'extérieur n'est pas dessiné depuis cette salle");
        ImGui.EndTooltip();
    }

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
}
