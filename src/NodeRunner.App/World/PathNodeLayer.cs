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

    private readonly ColorMesh?[] _meshes = new ColorMesh?[Categories.Count];
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
        foreach (var area in network.Areas)
        {
            foreach (var node in area.Nodes)
            {
                foreach (var link in area.LinksOf(node))
                {
                    var target = network.GetNode(link.Target);
                    if (target is null || !ShouldDraw(network, node, target))
                        continue;

                    var category = Classify(node, target);
                    batches[(int)category].AddLine(node.Position + LineOffset, target.Position + LineOffset, Color(category));
                }
            }
        }

        for (int i = 0; i < batches.Length; i++)
        {
            _counts[i] = batches[i].LineCount;
            _meshes[i] = renderer.CreateMesh(batches[i]);
        }
    }

    public void Draw(ColorRenderer renderer, Matrix4x4 viewProjection, Vector3 cameraPosition, Func<PathCategory, bool> isVisible)
    {
        for (int i = 0; i < _meshes.Length; i++)
        {
            if (_meshes[i] is { } mesh && isVisible((PathCategory)i))
                renderer.Draw(mesh, viewProjection, cameraPosition);
        }
    }

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
            _counts[i] = 0;
        }
    }

    public void Dispose() => DisposeMeshes();
}
