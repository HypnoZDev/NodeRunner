using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Hashing;

namespace NodeRunner.App.Ui;

// While the "Rooms and portals" layer is shown: the room or portal under the mouse in the 3D view is outlined, with
// its details next to the mouse. The selected room or portal is outlined in any case.
internal sealed class RoomHover(AppState state, RoomLayer layer)
{
    private static readonly int[] BoxEdges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];

    private Vector2 _origin, _size;
    private Matrix4x4 _viewProjection;

    public void Update(bool blocked)
    {
        if (!SetUpView())
            return;
        DrawSelected();
        DrawSelectedPortal();

        bool showRooms = state.IsLayerVisible(RoomLayer.LayerName(RoomLayerPart.Rooms));
        bool showInner = state.IsLayerVisible(RoomLayer.LayerName(RoomLayerPart.InnerPortals));
        bool showOutside = state.IsLayerVisible(RoomLayer.LayerName(RoomLayerPart.OutsidePortals));
        if (blocked || !showRooms && !showInner && !showOutside)
            return;
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || ImGui.IsMouseDown(ImGuiMouseButton.Right))
            return;
        var mouse = io.MousePos;
        if (mouse.X < _origin.X || mouse.Y < _origin.Y || mouse.X >= _origin.X + _size.X || mouse.Y >= _origin.Y + _size.Y
            || MouseRay(mouse) is not { } ray)
            return;

        var (room, portal) = layer.Pick(ray.Origin, ray.Direction, p => state.IsLayerVisible(RoomLayer.LayerName(p)));
        if (portal is not null)
            ShowPortal(portal);
        else if (room is not null)
            Show(room);
    }

    private bool SetUpView()
    {
        var (position, size) = DockLayout.SceneArea;
        _origin = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        _size = new Vector2(MathF.Max(1f, MathF.Round(size.X)), MathF.Max(1f, MathF.Round(size.Y)));
        _viewProjection = state.Camera.ViewProjection(_size.X / _size.Y);
        return Matrix4x4.Invert(_viewProjection, out _);
    }

    // The ray from the camera through a point of the 3D view (screen pixels).
    private (Vector3 Origin, Vector3 Direction)? MouseRay(Vector2 mouse)
    {
        if (!Matrix4x4.Invert(_viewProjection, out var inverse))
            return null;
        // From the camera through the mouse on the near plane (depth 1 with the reversed-Z projection).
        var ndc = new Vector2((mouse.X - _origin.X) / _size.X * 2f - 1f, 1f - (mouse.Y - _origin.Y) / _size.Y * 2f);
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        var camera = state.Camera.Position;
        return (camera, Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - camera));
    }

    // The selected room, from its current box (changes show while a field is dragged).
    private void DrawSelected()
    {
        if (state.SelectedRoom is not { } selected || state.World.Maps?.GetArchetype(selected.Interior.ArchetypeHash) is not { } archetype
            || selected.Index >= archetype.InteriorRooms.Count)
            return;
        var room = archetype.InteriorRooms[selected.Index];
        DrawBox(room.BoundsMin, room.BoundsMax, selected.Interior.Transform, Widgets.Col(UiTheme.Accent), 3f);
    }

    // The selected portal, from its current corners, with its first corner marked (corners are numbered from it).
    private void DrawSelectedPortal()
    {
        if (state.SelectedPortal is not { } selected || state.World.Maps?.GetArchetype(selected.Interior.ArchetypeHash) is not { } archetype
            || selected.Index >= archetype.InteriorPortals.Count)
            return;
        var corners = archetype.InteriorPortals[selected.Index].Corners;
        if (corners.Count < 2)
            return;
        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        uint color = Widgets.Col(UiTheme.Accent);
        var screen = new Vector2[corners.Count];
        var visible = new bool[corners.Count];
        for (int i = 0; i < corners.Count; i++)
            visible[i] = TryProject(Vector3.Transform(corners[i], selected.Interior.Transform), out screen[i]);
        for (int i = 0; i < corners.Count; i++)
        {
            int next = (i + 1) % corners.Count;
            if (visible[i] && visible[next])
                draw.AddLine(screen[i], screen[next], color, 3f);
        }
        for (int i = 0; i < corners.Count; i++)
        {
            if (!visible[i])
                continue;
            draw.AddCircleFilled(screen[i], 7f, Widgets.Col(UiTheme.Accent));
            var label = $"{i + 1}";
            draw.AddText(screen[i] - ImGui.CalcTextSize(label) / 2, 0xFF000000, label);
        }
        draw.PopClipRect();
    }

    private void DrawBox(Vector3 min, Vector3 max, Matrix4x4 transform, uint color, float thickness)
    {
        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        var screen = new Vector2[8];
        var visible = new bool[8];
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            visible[i] = TryProject(Vector3.Transform(corner, transform), out screen[i]);
        }
        for (int e = 0; e < BoxEdges.Length; e += 2)
        {
            int a = BoxEdges[e], b = BoxEdges[e + 1];
            if (visible[a] && visible[b])
                draw.AddLine(screen[a], screen[b], color, thickness);
        }
        draw.PopClipRect();
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
        ImGui.TextUnformatted(portal.LeadsOutside ? L($"Portail {portal.Index} vers l'extérieur") : L($"Portail {portal.Index} entre deux salles"));
        ImGui.TextColored(UiTheme.TextMuted, L($"Relie : {Side(portal.Portal.RoomFrom, portal.RoomFrom)}  et  {Side(portal.Portal.RoomTo, portal.RoomTo)}"));
        ImGui.TextColored(UiTheme.TextMuted, L($"Intérieur : {names.Describe(portal.Interior.ArchetypeHash)}"));
        if (portal.ObjectCount > 0)
            ImGui.TextColored(UiTheme.TextMuted, portal.ObjectCount == 1 ? L("1 objet attaché (porte...)") : L($"{portal.ObjectCount} objets attachés (portes...)"));
        ImGui.TextColored(UiTheme.TextFaint, L($"Drapeaux : 0x{portal.Portal.Flags:X}"));
        ImGui.TextColored(UiTheme.TextFaint, L("Clic : sélectionner le portail"));
        ImGui.EndTooltip();
    }

    private static string Side(int room, string name) => room == 0 ? L("l'extérieur") : L($"la salle {name}");

    private void Show(RoomBox room)
    {
        DrawBox(room.Room.BoundsMin, room.Room.BoundsMax, room.Transform, Widgets.Col(new Vector4(0.9f, 0.7f, 1f, 1f)), 2f);

        var names = state.World.Names ?? new HashNames();
        string name = room.Room.Name.Length > 0 ? room.Room.Name : L("(sans nom)");
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(L($"Salle {room.Index} : {name}"));
        ImGui.TextColored(UiTheme.TextMuted, L($"Intérieur : {names.Describe(room.Interior.ArchetypeHash)}"));
        ImGui.TextColored(UiTheme.TextMuted, L($"{room.PortalCount} portails · {room.ObjectCount} objets"));
        if (room.Room.HidesExterior)
            ImGui.TextColored(UiTheme.TextMuted, L("L'extérieur n'est pas dessiné depuis cette salle"));
        ImGui.TextColored(UiTheme.TextFaint, L("Clic : sélectionner la salle"));
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
