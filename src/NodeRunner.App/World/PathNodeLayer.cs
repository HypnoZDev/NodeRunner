using System.Numerics;
using NodeRunner.Data.Paths;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Kinds of path segments, each drawn in its own colour and shown/hidden separately.
internal enum PathCategory
{
    Road,
    Highway,
    OffRoad,
    Tunnel,
    SwitchedOff,
    Pedestrian,
}

// Draws the road and pedestrian network as coloured lines, one GPU mesh per category.
internal sealed class PathNodeLayer : IDisposable
{
    // Lines are lifted slightly so they stay visible above road surfaces (once models are drawn).
    private static readonly Vector3 LineOffset = new(0f, 0f, 0.25f);

    public static readonly IReadOnlyList<PathCategory> Categories = Enum.GetValues<PathCategory>();

    // Width of one lane of a road surface, and of a footpath.
    private const float LaneWidth = 4.5f;
    private const float FootpathWidth = 2f;
    private const float SurfaceAlpha = 0.28f;
    private const float PointSize = 0.45f;

    private readonly ColorMesh?[] _meshes = new ColorMesh?[Categories.Count];
    private readonly ColorMesh?[] _surfaces = new ColorMesh?[Categories.Count];
    private readonly ColorMesh?[] _points = new ColorMesh?[Categories.Count];
    private readonly int[] _counts = new int[Categories.Count];
    private PathNetwork? _built;

    public static string Label(PathCategory category) => category switch
    {
        PathCategory.Road => "Routes",
        PathCategory.Highway => "Autoroutes",
        PathCategory.OffRoad => "Hors-piste",
        PathCategory.Tunnel => "Tunnels",
        PathCategory.SwitchedOff => "Sans trafic par défaut (ruelles, parkings...)",
        PathCategory.Pedestrian => "Chemins piétons",
        _ => category.ToString(),
    };

    public static Vector4 Color(PathCategory category) => category switch
    {
        PathCategory.Road => new(0.25f, 0.75f, 1f, 1f),
        PathCategory.Highway => new(1f, 0.6f, 0.1f, 1f),
        PathCategory.OffRoad => new(0.75f, 0.55f, 0.3f, 1f),
        PathCategory.Tunnel => new(0.65f, 0.65f, 0.7f, 1f),
        PathCategory.SwitchedOff => new(0.9f, 0.2f, 0.2f, 1f),
        PathCategory.Pedestrian => new(1f, 0.45f, 0.9f, 1f),
        _ => Vector4.One,
    };

    public static string LayerName(PathCategory category) => "Path." + category;

    public int Count(PathCategory category) => _counts[(int)category];

    public void Update(WorldLoader loader, ColorRenderer renderer)
    {
        var network = loader.PathNetwork;
        if (network == _built)
            return;

        _built = network;
        DisposeMeshes();
        if (network is null)
            return;

        var batches = Categories.Select(_ => new LineBatch()).ToArray();
        var surfaces = Categories.Select(_ => new TriangleBatch()).ToArray();
        var points = Categories.Select(_ => new TriangleBatch()).ToArray();
        foreach (var area in network.Areas)
        {
            foreach (var node in area.Nodes)
            {
                var nodeCategory = Classify(node, node);
                AddPoint(points[(int)nodeCategory], node.Position + LineOffset, Brighter(Color(nodeCategory)));
                foreach (var link in area.LinksOf(node))
                {
                    var target = network.GetNode(link.Target);
                    if (target is null || !ShouldDraw(network, node, target))
                        continue;

                    var category = Classify(node, target);
                    var color = Color(category);
                    batches[(int)category].AddLine(node.Position + LineOffset, target.Position + LineOffset, color * new Vector4(1, 1, 1, 0.8f));
                    float width = node.IsPedestrian || target.IsPedestrian ? FootpathWidth
                        : Math.Max(1, link.LanesForward + link.LanesBackward) * LaneWidth;
                    AddStrip(surfaces[(int)category], node.Position + SurfaceOffset, target.Position + SurfaceOffset, width, color with { W = SurfaceAlpha });
                }
            }
        }

        for (int i = 0; i < batches.Length; i++)
        {
            _counts[i] = batches[i].LineCount;
            _meshes[i] = renderer.CreateMesh(batches[i]);
            _surfaces[i] = renderer.CreateMesh(surfaces[i]);
            _points[i] = renderer.CreateMesh(points[i]);
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<PathCategory, bool> isVisible)
    {
        // Surfaces first, then the centre lines and the nodes over them.
        foreach (var meshes in new[] { _surfaces, _meshes, _points })
        {
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] is { } mesh && isVisible((PathCategory)i))
                    renderer.Draw(mesh, viewProjection, cameraPosition);
            }
        }
    }

    private static readonly Vector3 SurfaceOffset = new(0f, 0f, 0.15f);

    // Flat band of the given width along a link, both faces (seen from above and below).
    private static void AddStrip(TriangleBatch batch, Vector3 a, Vector3 b, float width, Vector4 color)
    {
        var along = new Vector2(b.X - a.X, b.Y - a.Y);
        if (along.LengthSquared() < 1e-4f)
            return;
        along = Vector2.Normalize(along);
        var side = new Vector3(-along.Y, along.X, 0f) * (width * 0.5f);
        batch.AddTriangle(a - side, a + side, b + side, color);
        batch.AddTriangle(a - side, b + side, b - side, color);
        batch.AddTriangle(b + side, a + side, a - side, color);
        batch.AddTriangle(b - side, b + side, a - side, color);
    }

    // Small flat diamond on the node, both faces.
    private static void AddPoint(TriangleBatch batch, Vector3 p, Vector4 color)
    {
        var x = new Vector3(PointSize, 0, 0);
        var y = new Vector3(0, PointSize, 0);
        batch.AddTriangle(p - x, p - y, p + x, color);
        batch.AddTriangle(p - x, p + x, p + y, color);
        batch.AddTriangle(p + x, p - y, p - x, color);
        batch.AddTriangle(p + y, p + x, p - x, color);
    }

    private static Vector4 Brighter(Vector4 c) => new(c.X + (1 - c.X) * 0.5f, c.Y + (1 - c.Y) * 0.5f, c.Z + (1 - c.Z) * 0.5f, 1f);

    // The visible node closest to the mouse on screen (within maxPixels), among those near the camera.
    public static PathNode? PickNode(PathNetwork network, Matrix4x4 viewProjection, Vector3 cameraPosition, Vector2 mouse,
        Vector2 viewSize, Func<PathCategory, bool> isVisible, float maxPixels = 10f, float maxDistance = 600f)
    {
        PathNode? best = null;
        float bestScore = maxPixels * maxPixels;
        var camera2D = new Vector2(cameraPosition.X, cameraPosition.Y);
        foreach (var area in network.Areas)
        {
            foreach (var node in area.Nodes)
            {
                var p = node.Position + LineOffset;
                if (Vector2.DistanceSquared(new Vector2(p.X, p.Y), camera2D) > maxDistance * maxDistance || !isVisible(Classify(node, node)))
                    continue;
                var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
                if (clip.W <= 0.1f)
                    continue;
                var screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * viewSize.X, (0.5f - clip.Y / clip.W * 0.5f) * viewSize.Y);
                float d = Vector2.DistanceSquared(screen, mouse);
                if (d < bestScore)
                {
                    bestScore = d;
                    best = node;
                }
            }
        }
        return best;
    }

    // The selected node and its links, drawn over everything.
    public static void DrawSelected(ColorRenderer lines, PathNetwork network, PathNode node, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        var color = new Vector4(1f, 0.75f, 0.1f, 1f);
        var linkColor = new Vector4(1f, 0.9f, 0.4f, 1f);
        var p = node.Position + LineOffset;
        lines.AddBox(p - new Vector3(0.6f), p + new Vector3(0.6f), Matrix4x4.Identity, color);
        lines.AddLine(p, p + new Vector3(0, 0, 3f), color);
        if (network.GetArea(node.Id.AreaId) is { } area)
        {
            foreach (var link in area.LinksOf(node))
            {
                if (network.GetNode(link.Target) is { } target)
                    lines.AddLine(p, target.Position + LineOffset, linkColor);
            }
        }
        lines.Flush(viewProjection, cameraPosition, onTop: true);
    }

    public static string CategoryOf(PathNode node) => Label(Classify(node, node));

    // Most connections are stored in both directions (A->B and B->A): draw them only once.
    private static bool ShouldDraw(PathNetwork network, PathNode node, PathNode target)
    {
        if (Compare(node.Id, target.Id) < 0)
            return true;

        var targetArea = network.GetArea(target.Id.AreaId);
        bool hasReverse = targetArea is not null && targetArea.LinksOf(target).Any(l => l.Target == node.Id);
        return !hasReverse;
    }

    private static int Compare(PathNodeId a, PathNodeId b) =>
        a.AreaId != b.AreaId ? a.AreaId.CompareTo(b.AreaId) : a.NodeId.CompareTo(b.NodeId);

    private static PathCategory Classify(PathNode a, PathNode b)
    {
        if (a.IsPedestrian || b.IsPedestrian) return PathCategory.Pedestrian;
        if (a.IsSwitchedOff || b.IsSwitchedOff) return PathCategory.SwitchedOff;
        if (a.IsTunnel && b.IsTunnel) return PathCategory.Tunnel;
        if (a.IsHighway && b.IsHighway) return PathCategory.Highway;
        if (a.IsOffRoad || b.IsOffRoad) return PathCategory.OffRoad;
        return PathCategory.Road;
    }

    private void DisposeMeshes()
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            _meshes[i]?.Dispose();
            _meshes[i] = null;
            _surfaces[i]?.Dispose();
            _surfaces[i] = null;
            _points[i]?.Dispose();
            _points[i] = null;
            _counts[i] = 0;
        }
    }

    public void Dispose() => DisposeMeshes();
}
