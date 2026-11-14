using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Online: what the other participants have selected, outlined in their color, with their name above.
internal sealed class PresenceOverlay(AppState state)
{
    private const int MaxBoxes = 300;
    private static readonly int[] BoxEdges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];

    private Vector2 _origin, _size;
    private Matrix4x4 _viewProjection;

    public void Draw()
    {
        var peers = state.Online.Peers;
        var cameras = state.Online.PeerCameras;
        if (peers.Count == 0 && cameras.Count == 0 || state.World.Maps is not { } library)
            return;
        var (position, size) = DockLayout.SceneArea;
        _origin = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
        _size = new Vector2(MathF.Max(1f, MathF.Round(size.X)), MathF.Max(1f, MathF.Round(size.Y)));
        _viewProjection = state.Camera.ViewProjection(_size.X / _size.Y);

        var draw = ImGui.GetBackgroundDrawList();
        draw.PushClipRect(_origin, _origin + _size, true);
        foreach (var camera in cameras)
            DrawCamera(draw, camera);
        var screen = new Vector2[8];
        var visible = new bool[8];
        foreach (var peer in peers)
        {
            uint color = Widgets.Col(peer.Color);
            Vector2? label = null;
            int drawn = 0;
            // The main object (last) first: the name goes above it.
            for (int i = peer.Entities.Count - 1; i >= 0 && drawn < MaxBoxes; i--)
            {
                var entity = peer.Entities[i];
                if (entity.IsDeleted || library.GetArchetype(entity.ArchetypeHash) is not { } archetype)
                    continue;
                drawn++;
                var (min, max) = (archetype.BoundsMin, archetype.BoundsMax);
                var transform = entity.Transform;
                Vector2 top = new(0, float.MaxValue);
                bool any = false;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) != 0 ? max.X : min.X, (c & 2) != 0 ? max.Y : min.Y, (c & 4) != 0 ? max.Z : min.Z);
                    visible[c] = TryProject(Vector3.Transform(corner, transform), out screen[c]);
                    if (visible[c])
                    {
                        any = true;
                        if (screen[c].Y < top.Y)
                            top = screen[c];
                    }
                }
                for (int e = 0; e < BoxEdges.Length; e += 2)
                {
                    int a = BoxEdges[e], b = BoxEdges[e + 1];
                    if (visible[a] && visible[b])
                        draw.AddLine(screen[a], screen[b], color, 2.5f);
                }
                if (label is null && any && TryProject(Vector3.Transform((min + max) / 2, transform), out var center))
                    label = new Vector2(center.X, top.Y);
            }
            if (peer.Item is { } item && TryProject(item.Center, out var middle))
            {
                // Its size on screen, from a point at its radius beside it.
                float radius = TryProject(item.Center + state.Camera.Right * MathF.Max(item.Radius, 0.5f), out var side)
                    ? Math.Clamp(Vector2.Distance(middle, side), 8f, 400f) : 8f;
                draw.AddCircle(middle, radius, color, 48, 2.5f);
                draw.AddCircleFilled(middle, 3f, color);
                if (label is null)
                    DrawName(draw, $"{peer.Name} · {item.What}", middle - new Vector2(0, radius), color);
            }
            if (label is { } at)
                DrawName(draw, peer.Entities.Count > 1 ? $"{peer.Name} · {peer.Entities.Count}" : peer.Name, at, color);
        }
        draw.PopClipRect();
    }

    // A small pyramid pointing where the participant looks, with its name and distance.
    private void DrawCamera(ImDrawListPtr draw, Online.OnlineController.PeerCamera camera)
    {
        uint color = Widgets.Col(camera.Color);
        float cosPitch = MathF.Cos(camera.Pitch);
        var forward = new Vector3(cosPitch * MathF.Cos(camera.Yaw), cosPitch * MathF.Sin(camera.Yaw), MathF.Sin(camera.Pitch));
        var right = new Vector3(MathF.Sin(camera.Yaw), -MathF.Cos(camera.Yaw), 0f);
        var up = Vector3.Cross(right, forward);
        float distance = Vector3.Distance(state.Camera.Position, camera.Position);
        // Grows with distance so it stays visible from afar.
        float scale = Math.Clamp(distance * 0.03f, 1f, 40f);
        var apex = camera.Position;
        var center = apex + forward * 1.6f * scale;
        Span<Vector3> corners =
        [
            center + (right * 0.8f + up * 0.5f) * scale, center + (-right * 0.8f + up * 0.5f) * scale,
            center + (-right * 0.8f - up * 0.5f) * scale, center + (right * 0.8f - up * 0.5f) * scale,
        ];
        if (!TryProject(apex, out var a))
            return;
        Span<Vector2> screen = stackalloc Vector2[4];
        bool all = true;
        for (int i = 0; i < 4; i++)
            all &= TryProject(corners[i], out screen[i]);
        if (all)
        {
            for (int i = 0; i < 4; i++)
            {
                draw.AddLine(a, screen[i], color, 2f);
                draw.AddLine(screen[i], screen[(i + 1) % 4], color, 2f);
            }
        }
        draw.AddCircleFilled(a, 4f, color);
        string text = distance < 1000f ? $"{camera.Name} · {distance:0} m" : $"{camera.Name} · {distance / 1000f:0.0} km";
        DrawName(draw, text, a - new Vector2(0, 6f), color);
    }

    private void DrawName(ImDrawListPtr draw, string text, Vector2 at, uint color)
    {
        var textSize = ImGui.CalcTextSize(text);
        var padding = new Vector2(7f, 3f);
        var boxSize = textSize + padding * 2;
        var min = new Vector2(at.X - boxSize.X / 2, at.Y - boxSize.Y - 6f);
        // Kept inside the 3D view.
        min = Vector2.Clamp(min, _origin + new Vector2(2f), _origin + _size - boxSize - new Vector2(2f));
        draw.AddRectFilled(min, min + boxSize, color, 4f);
        draw.AddText(min + padding, 0xFF101010, text);
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
