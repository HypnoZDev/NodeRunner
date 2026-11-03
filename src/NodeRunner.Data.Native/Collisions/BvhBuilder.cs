using System.Numerics;

namespace NodeRunner.Data.Native.Collisions;

/// <summary>
/// Builds the game's collision search tree (BVH): boxes split in two along their longest spread until a node holds
/// few enough items. Nodes are stored depth first; an inner node gives the number of nodes of its subtree (to skip
/// it), a leaf gives its first item and item count. The tree is also cut into "trees" of at most 127 nodes.
/// </summary>
internal static class BvhBuilder
{
    public const int MaxTreeNodes = 127;

    public readonly record struct Node(Vector3 Min, Vector3 Max, int ItemId, int ItemCount);

    public readonly record struct Tree(Vector3 Min, Vector3 Max, int FirstNode, int EndNode);

    /// <param name="Order">Item of each position: items are grouped by leaf, leaves in node order.</param>
    public sealed record Result(int[] Order, List<Node> Nodes, List<Tree> Trees);

    /// <param name="threshold">Most items in a leaf (4 for geometries, 1 for composites).</param>
    /// <param name="reorder">
    /// Whether leaves point into the reordered list (<see cref="Result.Order"/>); otherwise a leaf holds exactly
    /// one item and points to its original index (composites keep their children in place).
    /// </param>
    public static Result Build(IReadOnlyList<(Vector3 Min, Vector3 Max)> items, int threshold, bool reorder)
    {
        var root = BuildNode(Enumerable.Range(0, items.Count).ToList(), items, threshold);
        var order = new List<int>();
        var nodes = new List<Node>();
        var trees = new List<Tree>();
        Gather(root, items, nodes, order, reorder);
        GatherTrees(root, trees);
        return new Result(order.ToArray(), nodes, trees);
    }

    private sealed class BuildingNode
    {
        public List<int>? Items;
        public BuildingNode[]? Children;
        public Vector3 Min, Max;
        public int Index;
        public int TotalNodes;
    }

    private static BuildingNode BuildNode(List<int> items, IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, int threshold)
    {
        var node = new BuildingNode { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        foreach (int i in items)
        {
            node.Min = Vector3.Min(node.Min, boxes[i].Min);
            node.Max = Vector3.Max(node.Max, boxes[i].Max);
        }

        if (items.Count <= threshold)
        {
            node.Items = items;
            node.TotalNodes = 1;
            return node;
        }

        // Split at the median of the box centres along the axis where they spread most.
        Vector3 Center(int i) => (boxes[i].Min + boxes[i].Max) * 0.5f;
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach (int i in items)
        {
            low = Vector3.Min(low, Center(i));
            high = Vector3.Max(high, Center(i));
        }
        var spread = high - low;
        int axis = spread.X >= spread.Y && spread.X >= spread.Z ? 0 : spread.Y >= spread.Z ? 1 : 2;
        var sorted = items.OrderBy(i => Center(i)[axis]).ThenBy(i => i).ToList();
        int half = sorted.Count / 2;

        node.Children =
        [
            BuildNode(sorted[..half], boxes, threshold),
            BuildNode(sorted[half..], boxes, threshold),
        ];
        node.TotalNodes = 1 + node.Children[0].TotalNodes + node.Children[1].TotalNodes;
        return node;
    }

    private static void Gather(BuildingNode node, IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, List<Node> nodes,
        List<int> order, bool reorder)
    {
        node.Index = nodes.Count;
        if (node.Items is { } items)
        {
            int first = reorder ? order.Count : (items.Count > 0 ? items[0] : 0);
            order.AddRange(items);
            nodes.Add(new Node(node.Min, node.Max, first, items.Count));
            return;
        }
        nodes.Add(new Node(node.Min, node.Max, node.TotalNodes, 0));
        foreach (var child in node.Children!)
            Gather(child, boxes, nodes, order, reorder);
    }

    private static void GatherTrees(BuildingNode node, List<Tree> trees)
    {
        if (node.TotalNodes > MaxTreeNodes && node.Children is { } children)
        {
            foreach (var child in children)
                GatherTrees(child, trees);
            return;
        }
        trees.Add(new Tree(node.Min, node.Max, node.Index, node.Index + node.TotalNodes));
    }
}
