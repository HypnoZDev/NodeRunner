using System.Numerics;

namespace NodeRunner.Data.Native.Collisions;

/// <summary>Removing polygons from a collision file, in place (see <see cref="YbnReader.RemovePolygons(byte[], IReadOnlySet{int})"/>).</summary>
internal sealed partial class BoundsFile
{
    // Search tree block (128 bytes): nodes list (pointer, count, capacity), boxes, then the trees list.
    private const int BvhNodesPointer = 0x00;
    private const int BvhNodesCount = 0x08;
    private const int BvhNodesCapacity = 0x0C;
    private const int BvhCenter = 0x40;
    private const int BvhQuantumInverse = 0x50;
    private const int BvhTreesPointer = 0x70;
    private const int BvhTreesCount = 0x78;
    private const int BvhTreesCapacity = 0x7A;

    /// <summary>
    /// Removes the polygons numbered in <paramref name="removed"/> from the bounds at <paramref name="o"/>.
    /// Returns true for a geometry left without polygons, which its composite then drops.
    /// </summary>
    public bool Remove(int o, IReadOnlySet<int> removed, ref int polygon) => U8(o + TypeOffset) switch
    {
        Composite => RemoveFromComposite(o, removed, ref polygon),
        Geometry or GeometryBvh => RemoveFromGeometry(o, removed, ref polygon),
        _ => false,
    };

    private bool RemoveFromComposite(int o, IReadOnlySet<int> removed, ref int polygon)
    {
        int count = U16(o + ChildrenCount1);
        int children = Resolve(U64(o + ChildrenPointer), count, 8);
        var kept = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int child = Resolve(U64(children + i * 8), 1, 0x70);
            if (child < 0 || !Remove(child, removed, ref polygon))
                kept.Add(i);
        }
        if (kept.Count == count)
            return false;

        // Every per-child array is packed the same way (once: the game's files share some of them, e.g. both
        // transform arrays).
        var packed = new HashSet<int>();
        foreach (var (pointer, size) in new[]
        {
            (ChildrenPointer, 8), (Transforms1Pointer, 64), (Transforms2Pointer, 64), (ChildBoxesPointer, 32),
            (ChildFlags1Pointer, 8), (ChildFlags2Pointer, 8),
        })
        {
            int array = Resolve(U64(o + pointer), count, size);
            if (array < 0 || !packed.Add(array))
                continue;
            for (int j = 0; j < kept.Count; j++)
                _data.AsSpan(array + kept[j] * size, size).CopyTo(_data.AsSpan(array + j * size, size));
            _data.AsSpan(array + kept.Count * size, (count - kept.Count) * size).Clear();
        }
        W16(o + ChildrenCount1, (ushort)kept.Count);
        W16(o + ChildrenCount2, (ushort)kept.Count);

        // The composite's own tree sorts its children by their boxes (one child per leaf, children not reordered).
        int bvh = Resolve(U64(o + CompositeBvhPointer), 1, 128);
        if (bvh >= 0)
        {
            int boxes = Resolve(U64(o + ChildBoxesPointer), kept.Count, 32);
            if (boxes < 0 && kept.Count > 0)
                throw new InvalidDataException("Boîtes des morceaux de collision absentes.");
            var items = Enumerable.Range(0, kept.Count).Select(j => (Vec3(boxes + j * 32), Vec3(boxes + j * 32 + 16))).ToList();
            WriteTree(bvh, items, threshold: 1, reorder: false);
        }
        return false;
    }

    private bool RemoveFromGeometry(int o, IReadOnlySet<int> removed, ref int polygon)
    {
        int count = (int)U32(o + PolygonsCount);
        int first = polygon;
        polygon += count;
        var kept = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (!removed.Contains(first + i))
                kept.Add(i);
        }
        if (kept.Count == count)
            return false;

        int polygons = Resolve(U64(o + PolygonsPointer), count, 16);
        int materials = Resolve(U64(o + PolygonMaterialsPointer), count, 1);
        int bvh = U8(o + TypeOffset) == GeometryBvh ? Resolve(U64(o + GeometryBvhPointer), 1, 128) : -1;

        // Order of the kept polygons: grouped by leaf of the rebuilt tree, or unchanged without tree.
        int[] order = Enumerable.Range(0, kept.Count).ToArray();
        if (bvh >= 0)
        {
            var boxes = kept.Select(i => PolygonBox(o, polygons + i * 16)).ToList();
            order = WriteTree(bvh, boxes, threshold: 4, reorder: true);
        }

        var oldPolygons = _data.AsSpan(polygons, count * 16).ToArray();
        var oldMaterials = materials >= 0 ? _data.AsSpan(materials, count).ToArray() : null;
        var newIndex = new int[count];
        Array.Fill(newIndex, -1);
        for (int j = 0; j < order.Length; j++)
            newIndex[kept[order[j]]] = j;

        for (int j = 0; j < order.Length; j++)
        {
            int source = kept[order[j]];
            int p = polygons + j * 16;
            oldPolygons.AsSpan(source * 16, 16).CopyTo(_data.AsSpan(p, 16));
            if ((oldPolygons[source * 16] & 7) == 0)
            {
                // Triangle neighbours: renumbered, none towards a removed polygon.
                for (int edge = 10; edge <= 14; edge += 2)
                {
                    int neighbour = U16(p + edge);
                    W16(p + edge, neighbour < count && newIndex[neighbour] >= 0 ? (ushort)newIndex[neighbour] : (ushort)0xFFFF);
                }
            }
            if (oldMaterials is not null)
                _data[materials + j] = oldMaterials[source];
        }
        _data.AsSpan(polygons + order.Length * 16, (count - order.Length) * 16).Clear();
        if (materials >= 0)
            _data.AsSpan(materials + order.Length, count - order.Length).Clear();
        W32(o + PolygonsCount, (uint)order.Length);

        if (bvh >= 0 && order.Length == 0)
        {
            W32(bvh + BvhNodesCount, 0);
            W16(bvh + BvhTreesCount, (ushort)0);
        }
        return order.Length == 0;
    }

    /// <summary>Box of a polygon in the geometry's space (vertices plus the geometry centre), as the tree stores it.</summary>
    private (Vector3 Min, Vector3 Max) PolygonBox(int o, int p)
    {
        int vertexCount = (int)U32(o + VerticesCount);
        int vertices = Resolve(U64(o + VerticesPointer), vertexCount, 6);
        var quantum = Vec3(o + QuantumOffset);
        var center = Vec3(o + CenterOffset);
        Vector3 V(int index) => (index >= 0 && index < vertexCount ? Vertex(vertices, index) * quantum : Vector3.Zero) + center;

        var points = new List<Vector3>();
        float radius = 0;
        switch (U8(p) & 7)
        {
            case 0:
                points.AddRange([V(U16(p + 4) & 0x7FFF), V(U16(p + 6) & 0x7FFF), V(U16(p + 8) & 0x7FFF)]);
                break;
            case 1:
                points.Add(V(U16(p + 2)));
                radius = F32(p + 4);
                break;
            case 2:
            case 4:
                points.AddRange([V(U16(p + 2)), V(U16(p + 8))]);
                radius = F32(p + 4);
                break;
            case 3:
                var corners = new[] { V(I16(p + 4)), V(I16(p + 6)), V(I16(p + 8)), V(I16(p + 10)) };
                var middle = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
                points.AddRange(corners);
                points.AddRange(corners.Select(c => 2 * middle - c));
                break;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return points.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (min - new Vector3(radius), max + new Vector3(radius));
    }

    /// <summary>
    /// Rebuilds a search tree in its own arrays (the kept items need fewer nodes than before; a coarser tree is built
    /// if not). Boxes are quantized with the tree's existing centre and scale. Returns the item order.
    /// </summary>
    private int[] WriteTree(int bvh, List<(Vector3 Min, Vector3 Max)> items, int threshold, bool reorder)
    {
        int nodeCapacity = (int)U32(bvh + BvhNodesCapacity);
        int treeCapacity = U16(bvh + BvhTreesCapacity);
        int nodes = Resolve(U64(bvh + BvhNodesPointer), nodeCapacity, 16);
        int trees = Resolve(U64(bvh + BvhTreesPointer), treeCapacity, 16);
        if (items.Count == 0 || nodes < 0)
        {
            W32(bvh + BvhNodesCount, 0);
            W16(bvh + BvhTreesCount, (ushort)0);
            return [];
        }

        BvhBuilder.Result? tree = null;
        for (int t = threshold; t <= (reorder ? 32 : threshold); t *= 2)
        {
            var candidate = BvhBuilder.Build(items, t, reorder);
            if (candidate.Nodes.Count <= nodeCapacity && (trees >= 0 ? candidate.Trees.Count <= treeCapacity : true))
            {
                tree = candidate;
                break;
            }
        }
        if (tree is null)
            throw new InvalidDataException("L'arbre de recherche de la collision ne tient pas dans le fichier.");

        var center = Vec3(bvh + BvhCenter);
        var inverse = Vec3(bvh + BvhQuantumInverse);
        void Box(int at, Vector3 min, Vector3 max)
        {
            // One step of margin each way: the stored scale and its inverse do not round exactly alike.
            var qMin = Vector3.Clamp((min - center) * inverse - Vector3.One, new Vector3(-32767), new Vector3(32767));
            var qMax = Vector3.Clamp((max - center) * inverse + Vector3.One, new Vector3(-32767), new Vector3(32767));
            W16(at, (short)MathF.Floor(qMin.X));
            W16(at + 2, (short)MathF.Floor(qMin.Y));
            W16(at + 4, (short)MathF.Floor(qMin.Z));
            W16(at + 6, (short)MathF.Ceiling(qMax.X));
            W16(at + 8, (short)MathF.Ceiling(qMax.Y));
            W16(at + 10, (short)MathF.Ceiling(qMax.Z));
        }

        for (int i = 0; i < nodeCapacity; i++)
        {
            int at = nodes + i * 16;
            _data.AsSpan(at, 16).Clear();
            if (i < tree.Nodes.Count)
            {
                var node = tree.Nodes[i];
                Box(at, node.Min, node.Max);
                W16(at + 12, (short)node.ItemId);
                W16(at + 14, (short)node.ItemCount);
            }
            else
            {
                W16(at + 12, (short)1); // empty filler node, as in the game's files
            }
        }
        W32(bvh + BvhNodesCount, (uint)tree.Nodes.Count);

        if (trees >= 0)
        {
            for (int i = 0; i < treeCapacity; i++)
            {
                int at = trees + i * 16;
                _data.AsSpan(at, 16).Clear();
                if (i < tree.Trees.Count)
                {
                    var t = tree.Trees[i];
                    Box(at, t.Min, t.Max);
                    W16(at + 12, (short)t.FirstNode);
                    W16(at + 14, (short)t.EndNode);
                }
            }
            W16(bvh + BvhTreesCount, (ushort)tree.Trees.Count);
        }
        return tree.Order;
    }
}
