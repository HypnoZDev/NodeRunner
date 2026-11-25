using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Ui;
using NodeRunner.App.World;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
    // --- Drawing new collision surfaces (CollisionDrawTool): a click puts a corner ---

    private static readonly Vector4 DrawCornerColor = new(0.3f, 0.85f, 1f, 1f);

    private (Vector2 Mouse, Vector3 Camera, float Yaw, float Pitch)? _drawHoverKey;
    private Vector3? _drawHover;

    // The ray from the camera through a point of the 3D view (point relative to the view, same units as size).
    private (Vector3 Origin, Vector3 Direction)? ViewRay(Vector2 point, Vector2 size)
    {
        var viewProjection = _state.Camera.ViewProjection(size.X / size.Y);
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        var ndc = new Vector2(point.X / size.X * 2f - 1f, 1f - point.Y / size.Y * 2f);
        // Reversed Z: depth 1 = near plane.
        var near = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        var camera = _state.Camera.Position;
        return (camera, Vector3.Normalize(new Vector3(near.X, near.Y, near.Z) / near.W - camera));
    }

    // A click of the collision tool while drawing: a corner instead of a selection.
    private bool AddDrawnCorner(Rectangle scene, Point click)
    {
        if (_state.CollisionDraw is null || !scene.Contains(click))
            return false;
        if (ViewRay(new Vector2(click.X - scene.X, click.Y - scene.Y), new Vector2(scene.Width, scene.Height)) is { } ray)
            _state.AddCollisionCorner(_collisionLayer, ray.Origin, ray.Direction);
        _drawHoverKey = null;
        return true;
    }

    // The corners placed so far, the edges between them and towards where the next click would put a corner.
    private void DrawCollisionDrawing()
    {
        if (_state.Tool != TransformTool.Collision)
            _state.CollisionDraw = null;
        if (_state.CollisionDraw is not { } tool)
            return;
        var (position, size) = DockLayout.SceneArea;
        if (size.X < 1f || size.Y < 1f)
            return;
        var viewProjection = _state.Camera.ViewProjection(size.X / size.Y);
        var io = ImGui.GetIO();
        var mouse = io.MousePos - position;
        bool inside = !io.WantCaptureMouse && mouse.X >= 0 && mouse.Y >= 0 && mouse.X < size.X && mouse.Y < size.Y;

        // The point under the mouse, worked out again only when the mouse or the camera moved.
        var camera = _state.Camera;
        var key = (mouse, camera.Position, camera.Yaw, camera.Pitch);
        if (!inside)
            _drawHover = null;
        else if (_drawHoverKey != key)
        {
            _drawHoverKey = key;
            _drawHover = ViewRay(mouse, size) is { } ray ? tool.Next(_collisionLayer, ray.Origin, ray.Direction) : null;
        }

        Vector2? Screen(Vector3 world)
        {
            var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
            if (clip.W <= 0.01f)
                return null;
            return position + new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);
        }

        var drawList = ImGui.GetForegroundDrawList();
        uint color = Widgets.Col(DrawCornerColor);
        var points = tool.Corners.Select(Screen).ToList();
        if (_drawHover is { } hover && tool.Corners.Count < tool.CornerCount)
            points.Add(Screen(hover));
        for (int i = 0; i + 1 < points.Count; i++)
        {
            if (points[i] is { } a && points[i + 1] is { } b)
                drawList.AddLine(a, b, color, 2f);
        }
        // The edge closing the shape once its last corner is under the mouse.
        if (points.Count == tool.CornerCount && points[0] is { } first && points[^1] is { } last)
            drawList.AddLine(last, first, Widgets.Col(DrawCornerColor with { W = 0.5f }), 1.5f);
        foreach (var p in points)
        {
            if (p is { } at)
                drawList.AddCircleFilled(at, 4.5f, color);
        }
        if (inside)
        {
            string hint = L($"Coin {tool.Corners.Count + 1} / {tool.CornerCount}");
            drawList.AddText(position + mouse + new Vector2(14, 10), Widgets.Col(new Vector4(1, 1, 1, 0.9f)), hint);
        }
    }
}
