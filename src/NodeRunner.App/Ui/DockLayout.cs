using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Dockable panels around the 3D view: the whole window is a dock space whose central area is left empty (the 3D scene
// shows through it, clicks reach the scene).
internal static unsafe partial class DockLayout
{
    // Default layout: share of the width (or height) taken by each side.
    private const float LeftRatio = 0.21f;
    private const float RightRatio = 0.27f;
    private const float RightBottomRatio = 0.42f;
    private const float LeftBottomRatio = 0.48f;
    private const float CenterBottomRatio = 0.26f;

    public static (Vector2 Position, Vector2 Size) SceneArea { get; private set; }

    // Submits the dock space; call each frame before the panels. reset rebuilds the default layout: two panels
    // stacked on the left, two on the right, one under the 3D view.
    public static void Draw(bool reset, string leftTop, string leftBottom, string rightTop, string rightBottom, string bottom)
    {
        var viewport = ImGui.GetMainViewport();
        // Renamed when the panel window IDs change, so that the default layout is rebuilt.
        uint id = ImGui.GetID("NodeRunnerDockSpace7");
        if (reset || igDockBuilderGetNode(id) is null)
            BuildDefault(id, viewport.WorkSize, leftTop, leftBottom, rightTop, rightBottom, bottom);

        ImGui.DockSpaceOverViewport(id, viewport,
            ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.NoDockingOverCentralNode);
        SceneArea = FindSceneArea(id, viewport);
    }

    // A bar along the top of the 3D view (the tabs): the scene starts below it.
    public static void ReserveTop(float height)
    {
        var (position, size) = SceneArea;
        height = Math.Min(height, size.Y - 1f);
        SceneArea = (position + new Vector2(0, height), size - new Vector2(0, height));
    }

    private static void BuildDefault(uint id, Vector2 size, string leftTop, string leftBottom, string rightTop, string rightBottom, string bottom)
    {
        igDockBuilderRemoveNode(id);
        igDockBuilderAddNode(id, DockSpaceNodeFlag);
        igDockBuilderSetNodeSize(id, size);

        uint leftId, rest, rightId, center, rightBottomId, rightTopId, leftBottomId, leftTopId, bottomId, sceneId;
        igDockBuilderSplitNode(id, ImGuiDir.Left, LeftRatio, &leftId, &rest);
        igDockBuilderSplitNode(rest, ImGuiDir.Right, RightRatio / (1f - LeftRatio), &rightId, &center);
        igDockBuilderSplitNode(rightId, ImGuiDir.Down, RightBottomRatio, &rightBottomId, &rightTopId);
        igDockBuilderSplitNode(leftId, ImGuiDir.Down, LeftBottomRatio, &leftBottomId, &leftTopId);
        igDockBuilderSplitNode(center, ImGuiDir.Down, CenterBottomRatio, &bottomId, &sceneId);

        igDockBuilderDockWindow(leftTop, leftTopId);
        igDockBuilderDockWindow(leftBottom, leftBottomId);
        igDockBuilderDockWindow(rightTop, rightTopId);
        igDockBuilderDockWindow(rightBottom, rightBottomId);
        igDockBuilderDockWindow(bottom, bottomId);
        igDockBuilderFinish(id);
    }

    private static (Vector2, Vector2) FindSceneArea(uint id, ImGuiViewportPtr viewport)
    {
        // A NaN passes every "<" test below: a window placed from it gets no monitor, and ImGui stops on a zero DPI scale.
        var whole = Finite(viewport.WorkPos) && Finite(viewport.WorkSize) ? (viewport.WorkPos, viewport.WorkSize) : (Vector2.Zero, Vector2.Zero);
        byte* root = igDockBuilderGetNode(id);
        byte* central = igDockBuilderGetCentralNode(id);
        if (root is null || central is null || *(uint*)root != id)
            return whole;

        var rootPos = *(Vector2*)(root + NodePosOffset);
        var rootSize = *(Vector2*)(root + NodeSizeOffset);
        if (!Finite(rootPos) || !Finite(rootSize) || Vector2.Distance(rootPos, viewport.WorkPos) > 1f || Vector2.Distance(rootSize, viewport.WorkSize) > 1f)
            return whole;

        var pos = *(Vector2*)(central + NodePosOffset);
        var size = *(Vector2*)(central + NodeSizeOffset);
        if (!Finite(pos) || !Finite(size) || size.X < 1f || size.Y < 1f || pos.X < rootPos.X - 1f || pos.Y < rootPos.Y - 1f
            || pos.X + size.X > rootPos.X + rootSize.X + 1f || pos.Y + size.Y > rootPos.Y + rootSize.Y + 1f)
            return whole;
        return (pos, size);
    }

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    // Bar along an edge of the window (toolbar, status bar): the panels and the 3D view are laid out in what is left.
    public static bool BeginSideBar(string name, ImGuiDir edge, float size)
    {
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings;
        return igBeginViewportSideBar(name, ImGui.GetMainViewport().NativePtr, edge, size, flags) != 0;
    }

    [LibraryImport("cimgui", StringMarshalling = StringMarshalling.Utf8)]
    private static partial byte igBeginViewportSideBar(string name, ImGuiViewport* viewport, ImGuiDir dir, float size, ImGuiWindowFlags windowFlags);

    // --- Dock builder (imgui_internal.h), exported by cimgui but not wrapped by ImGui.NET ---

    private const ImGuiDockNodeFlags DockSpaceNodeFlag = (ImGuiDockNodeFlags)(1 << 10); // ImGuiDockNodeFlags_DockSpace
    private const int NodePosOffset = 72;  // ImGuiDockNode::Pos (after ID, 5 flag fields, 3 pointers, ImVector, TabBar)
    private const int NodeSizeOffset = 80; // ImGuiDockNode::Size

    [LibraryImport("cimgui")]
    private static partial byte* igDockBuilderGetNode(uint nodeId);

    [LibraryImport("cimgui")]
    private static partial byte* igDockBuilderGetCentralNode(uint nodeId);

    [LibraryImport("cimgui")]
    private static partial uint igDockBuilderAddNode(uint nodeId, ImGuiDockNodeFlags flags);

    [LibraryImport("cimgui")]
    private static partial void igDockBuilderRemoveNode(uint nodeId);

    // DllImport: the source-generated LibraryImport cannot pass a Vector2 by value.
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);

    [LibraryImport("cimgui")]
    private static partial uint igDockBuilderSplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatioForNodeAtDir, uint* outIdAtDir, uint* outIdAtOppositeDir);

    [LibraryImport("cimgui", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void igDockBuilderDockWindow(string windowName, uint nodeId);

    [LibraryImport("cimgui")]
    private static partial void igDockBuilderFinish(uint nodeId);
}
