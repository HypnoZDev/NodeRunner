using System.Numerics;

namespace NodeRunner.Data.Paths;

/// <summary>
/// The content of one nodesN.ynd file: the nodes of one 512 x 512 m square of the map, and their links.
/// The map is cut into a 32 x 32 grid starting at (-8192, -8192); area N is column N % 32, row N / 32.
/// </summary>
public sealed class PathNodeArea
{
    public const int GridSize = 32;
    public const int AreaCount = GridSize * GridSize;
    public const float AreaSize = 512f;
    public const float WorldMin = -8192f;

    public required int AreaId { get; init; }

    /// <summary>Vehicle nodes first, then pedestrian nodes.</summary>
    public required IReadOnlyList<PathNode> Nodes { get; init; }

    public required IReadOnlyList<PathLink> Links { get; init; }

    public int VehicleNodeCount { get; init; }
    public int PedestrianNodeCount { get; init; }

    /// <summary>Outgoing links of <paramref name="node"/>.</summary>
    public IEnumerable<PathLink> LinksOf(PathNode node)
    {
        for (int i = 0; i < node.LinkCount; i++)
        {
            int index = node.FirstLink + i;
            if (index < Links.Count)
                yield return Links[index];
        }
    }

    /// <summary>Area number containing a world position, or -1 if outside the grid.</summary>
    public static int AreaIdAt(Vector2 position)
    {
        int x = (int)MathF.Floor((position.X - WorldMin) / AreaSize);
        int y = (int)MathF.Floor((position.Y - WorldMin) / AreaSize);
        return x is >= 0 and < GridSize && y is >= 0 and < GridSize ? y * GridSize + x : -1;
    }
}
