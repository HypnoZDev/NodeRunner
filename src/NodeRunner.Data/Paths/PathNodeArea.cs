using System.Numerics;

namespace NodeRunner.Data.Paths;

// The content of one nodesN.ynd file: the nodes of one 512 x 512 m square of the map, and their links.
public sealed class PathNodeArea
{
    public const int GridSize = 32;
    public const int AreaCount = GridSize * GridSize;
    public const float AreaSize = 512f;
    public const float WorldMin = -8192f;

    public required int AreaId { get; init; }

    public required IReadOnlyList<PathNode> Nodes { get; init; }

    public required IReadOnlyList<PathLink> Links { get; init; }

    public int VehicleNodeCount { get; init; }
    public int PedestrianNodeCount { get; init; }

    public IEnumerable<PathLink> LinksOf(PathNode node)
    {
        for (int i = 0; i < node.LinkCount; i++)
        {
            int index = node.FirstLink + i;
            if (index < Links.Count)
                yield return Links[index];
        }
    }

    public static int AreaIdAt(Vector2 position)
    {
        int x = (int)MathF.Floor((position.X - WorldMin) / AreaSize);
        int y = (int)MathF.Floor((position.Y - WorldMin) / AreaSize);
        return x is >= 0 and < GridSize && y is >= 0 and < GridSize ? y * GridSize + x : -1;
    }
}
