using System.Numerics;
using System.Buffers.Binary;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Scenarios;
using R = NodeRunner.Data.Native.Scenarios.ScenarioRegionReader;

namespace NodeRunner.Data.Native.Scenarios;

// Writes a region's points back into its file content (decompressed). Same points: values written in place, the rest
// of the file untouched. Points added or deleted: the region's own points are sorted again into the cells of its
// spatial grid (the game finds points through it), the grid is made again and the file laid out again; the points of
// its clusters keep their place.
public static class ScenarioRegionWriter
{
    // Points with this flag are seen from further away; the grid marks the cells holding one.
    private const uint ExtendedRangeFlag = 1u << 22;
    private const ushort ExtendedRangeCell = 0x8000;
    private const int MaxCells = 999;
    private const float FirstCellSize = 32f;

    // Grid fields (rage::spdGrid2D inside the region).
    private const int GridMinCellX = 12, GridMaxCellX = 16, GridMinCellY = 20, GridMaxCellY = 24, GridCellDimX = 44, GridCellDimY = 48;

    // The new content, and the system page flags to put in its resource header when it was laid out again (null: same
    // size and layout as before).
    public static (byte[] Content, uint? SystemPageFlags) Write(ReadOnlySpan<byte> content, ScenarioRegion region)
    {
        var meta = new MetaResource(content);
        int start = R.Region(meta);
        var offsets = R.PointOffsets(meta, start);
        int ownInFile = meta.ReadArray(start + R.RegionMyPoints, R.PointSize).Count;
        var known = ReadTypes(meta, start).ToHashSet();
        return region.Restructured || offsets.Count != region.Points.Count || ownInFile != region.OwnPointCount
               || region.Points.Any(p => !known.Contains(p.TypeHash))
            ? Rebuild(content, region, ownInFile)
            : (Patch(content, region), null);
    }

    public static byte[] Patch(ReadOnlySpan<byte> content, ScenarioRegion region)
    {
        var result = content.ToArray();
        var meta = new MetaResource(content);
        int start = R.Region(meta);
        var offsets = R.PointOffsets(meta, start);
        if (offsets.Count != region.Points.Count)
            throw new InvalidDataException($"{offsets.Count} points dans le fichier, {region.Points.Count} attendus");
        var fileTypes = ReadTypes(meta, start);
        var types = TypeIndices(fileTypes);
        var changes = ChainChangesOf(meta, offsets, region, fileTypes);
        for (int i = 0; i < offsets.Count; i++)
            WritePoint(result.AsSpan(offsets[i], R.PointSize), region.Points[i].Values, types);
        // Same points: chain nodes can only have moved, in place.
        var (nodes, nodeCount) = meta.ReadArray(start + R.RegionChainNodes, ChainNodeSize);
        for (int i = 0; i < nodeCount; i++)
            MoveNode(result.AsSpan(nodes + i * ChainNodeSize, ChainNodeSize), changes);
        return result;
    }

    // Where each of the region's own points goes: sorted by cell of the grid, the grid itself, and its cells' ends.
    public readonly record struct Grid(float CellSize, int MinX, int MinY, int MaxX, int MaxY, int[] Order, ushort[] CellEnds);

    public static Grid MakeGrid(IReadOnlyList<(System.Numerics.Vector3 Position, uint Flags)> points)
    {
        var min = new System.Numerics.Vector2(float.MaxValue);
        var max = new System.Numerics.Vector2(float.MinValue);
        foreach (var (p, _) in points)
        {
            min = System.Numerics.Vector2.Min(min, new(p.X, p.Y));
            max = System.Numerics.Vector2.Max(max, new(p.X, p.Y));
        }
        if (points.Count == 0)
            (min, max) = (default, default);

        // The smallest cells (32 m, doubled) giving at most 999 cells.
        float size = FirstCellSize;
        int minX, minY, maxX, maxY, width, height;
        while (true)
        {
            (minX, minY) = ((int)MathF.Floor(min.X / size), (int)MathF.Floor(min.Y / size));
            (maxX, maxY) = ((int)MathF.Floor(max.X / size), (int)MathF.Floor(max.Y / size));
            (width, height) = (maxX - minX + 1, maxY - minY + 1);
            if (width * height <= MaxCells)
                break;
            size *= 2f;
        }

        var cells = new List<int>?[width * height];
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i].Position;
            int x = Math.Clamp((int)MathF.Floor(p.X / size) - minX, 0, width - 1);
            int y = Math.Clamp((int)MathF.Floor(p.Y / size) - minY, 0, height - 1);
            (cells[x + y * width] ??= []).Add(i);
        }
        var order = new List<int>(points.Count);
        var ends = new ushort[cells.Length];
        for (int c = 0; c < cells.Length; c++)
        {
            bool extended = false;
            foreach (int i in cells[c] ?? [])
            {
                order.Add(i);
                extended |= (points[i].Flags & ExtendedRangeFlag) != 0;
            }
            ends[c] = (ushort)(order.Count | (extended ? ExtendedRangeCell : 0));
        }
        return new Grid(size, minX, minY, maxX, maxY, [.. order], ends);
    }

    private static (byte[], uint?) Rebuild(ReadOnlySpan<byte> content, ScenarioRegion region, int ownInFile)
    {
        if (region.OwnPointCount >= ExtendedRangeCell)
            throw new InvalidDataException($"trop de points ({region.OwnPointCount})");
        var meta = new MetaResource(content);
        int start = R.Region(meta);
        var offsets = R.PointOffsets(meta, start);
        if (offsets.Count - ownInFile != region.Points.Count - region.OwnPointCount)
            throw new InvalidDataException("les points des groupes (clusters) ont changé");

        // Types: those of the file, then the new ones.
        var types = ReadTypes(meta, start);
        var changes = ChainChangesOf(meta, offsets, region, types);
        foreach (var point in region.Points)
        {
            if (!types.Contains(point.TypeHash))
                types.Add(point.TypeHash);
        }
        if (types.Count > 255)
            throw new InvalidDataException("plus de 255 types de scénario dans la région");
        var indices = TypeIndices(types);

        // Cluster points: in place, as before.
        var patched = content.ToArray();
        for (int i = region.OwnPointCount; i < region.Points.Count; i++)
            WritePoint(patched.AsSpan(offsets[ownInFile + i - region.OwnPointCount], R.PointSize), region.Points[i].Values, indices);

        var own = region.Points.Take(region.OwnPointCount).ToList();
        var grid = MakeGrid(own.Select(p => (p.Position, p.Flags)).ToList());
        var points = new byte[own.Count * R.PointSize];
        for (int i = 0; i < grid.Order.Length; i++)
        {
            var point = own[grid.Order[i]];
            var target = points.AsSpan(i * R.PointSize, R.PointSize);
            point.Source?.AsSpan(0, R.PointSize).CopyTo(target);
            WritePoint(target, point.Values, indices);
        }
        var typeBytes = new byte[types.Count * 4];
        for (int i = 0; i < types.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(typeBytes.AsSpan(i * 4), types[i]);
        var cellBytes = new byte[grid.CellEnds.Length * 2];
        for (int i = 0; i < grid.CellEnds.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(cellBytes.AsSpan(i * 2), grid.CellEnds[i]);

        var document = MetaDocument.Read(patched);
        var root = document.Blocks[document.RootBlock].Data;
        // The region is the root block's only item.
        SetArray(document, root, R.RegionMyPoints, points, own.Count, R.PointStructure);
        SetArray(document, root, R.RegionTypeNames, typeBytes, types.Count, HashBlock);
        UpdateChains(document, root, changes);
        // Without points of its own, a region keeps its grid (the game's are like that).
        if (own.Count == 0)
        {
            var (kept, keptFlags) = document.Write();
            return (kept, keptFlags);
        }
        SetArray(document, root, R.RegionGridCells, cellBytes, grid.CellEnds.Length, UnsignedShortBlock);
        var g = root.AsSpan(R.RegionGrid);
        BinaryPrimitives.WriteInt32LittleEndian(g[GridMinCellX..], grid.MinX);
        BinaryPrimitives.WriteInt32LittleEndian(g[GridMaxCellX..], grid.MaxX);
        BinaryPrimitives.WriteInt32LittleEndian(g[GridMinCellY..], grid.MinY);
        BinaryPrimitives.WriteInt32LittleEndian(g[GridMaxCellY..], grid.MaxY);
        BinaryPrimitives.WriteSingleLittleEndian(g[GridCellDimX..], grid.CellSize);
        BinaryPrimitives.WriteSingleLittleEndian(g[GridCellDimY..], grid.CellSize);
        var (written, pageFlags) = document.Write();
        return (written, pageFlags);
    }

    // Block kinds of plain values (the type code stands for the structure).
    private const uint UnsignedShortBlock = 0x13;
    private const uint HashBlock = 0x4A;

    // Points an array field of the root at new items: in place of the old ones when they filled a block alone, else in
    // a new block (the old items stay unused).
    private static void SetArray(MetaDocument document, byte[] root, int field, byte[] items, int count, uint blockKind)
    {
        var span = root.AsSpan(field);
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(span);
        int oldCount = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        int block;
        if (count == 0)
            block = -1;
        else if (oldCount > 0 && MetaDocument.DecodePointer(pointer) is var (oldBlock, offset) && offset == 0
            && oldBlock >= 0 && oldBlock < document.Blocks.Count && oldBlock != document.RootBlock
            && document.Blocks[oldBlock].Data.Length * count == items.Length * oldCount)
        {
            block = oldBlock;
            document.Blocks[block].Data = items;
        }
        else
        {
            uint kind = oldCount > 0 && MetaDocument.DecodePointer(pointer).Block is var b && b >= 0 && b < document.Blocks.Count
                ? document.Blocks[b].Structure
                : blockKind;
            document.Blocks.Add(new MetaDocument.Block(kind, items));
            block = document.Blocks.Count - 1;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(span, block < 0 ? 0 : MetaDocument.Pointer(block, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)count);
    }

    // Chain nodes stand on points (same position, 99.6 % of the game's): what became of the file's points.
    private const int ChainNodeSize = 32, ChainEdgeSize = 8, ChainSize = 40;
    private const int NodeType = 20, NodeHasIncoming = 24, NodeHasOutgoing = 25, ChainEdgeIds = 8;

    private sealed record ChainChanges(Dictionary<Vector3, (Vector3 Position, uint OldType, uint NewType)> Moves, HashSet<Vector3> Gone)
    {
        public bool IsEmpty => Moves.Count == 0 && Gone.Count == 0;
    }

    private static Vector3 SourcePosition(ReadOnlySpan<byte> point) => new(
        BinaryPrimitives.ReadSingleLittleEndian(point[R.PointPositionAndDirection..]),
        BinaryPrimitives.ReadSingleLittleEndian(point[(R.PointPositionAndDirection + 4)..]),
        BinaryPrimitives.ReadSingleLittleEndian(point[(R.PointPositionAndDirection + 8)..]));

    private static ChainChanges ChainChangesOf(MetaResource meta, List<int> offsets, ScenarioRegion region, List<uint> fileTypes)
    {
        var kept = new HashSet<Vector3>();
        var moves = new Dictionary<Vector3, (Vector3, uint, uint)>();
        foreach (var point in region.Points)
        {
            if (!point.FromFile || point.Source is not { Length: >= R.PointSize } source)
                continue;
            var was = SourcePosition(source);
            kept.Add(was);
            int typeIndex = source[R.PointType];
            uint oldType = typeIndex < fileTypes.Count ? fileTypes[typeIndex] : 0;
            if (point.Position != was || point.TypeHash != oldType)
                moves.TryAdd(was, (point.Position, oldType, point.TypeHash));
        }
        var gone = new HashSet<Vector3>();
        foreach (int offset in offsets)
        {
            var position = SourcePosition(meta.Data.Bytes(offset, R.PointSize));
            if (!kept.Contains(position))
                gone.Add(position);
        }
        return new ChainChanges(moves, gone);
    }

    private static Vector3 NodePosition(ReadOnlySpan<byte> node) => new(
        BinaryPrimitives.ReadSingleLittleEndian(node), BinaryPrimitives.ReadSingleLittleEndian(node[4..]), BinaryPrimitives.ReadSingleLittleEndian(node[8..]));

    private static void MoveNode(Span<byte> node, ChainChanges changes)
    {
        if (!changes.Moves.TryGetValue(NodePosition(node), out var move))
            return;
        BinaryPrimitives.WriteSingleLittleEndian(node, move.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(node[4..], move.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(node[8..], move.Position.Z);
        if (BinaryPrimitives.ReadUInt32LittleEndian(node[NodeType..]) == move.OldType)
            BinaryPrimitives.WriteUInt32LittleEndian(node[NodeType..], move.NewType);
    }

    private static (byte[] Data, int Offset, int Count) RootArray(MetaDocument document, byte[] owner, int field, int itemSize)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(owner.AsSpan(field + 8));
        if (count == 0)
            return ([], 0, 0);
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(owner.AsSpan(field)));
        if (block < 0 || block >= document.Blocks.Count || offset + count * itemSize > document.Blocks[block].Data.Length)
            throw new InvalidDataException("chaînes de scénarios illisibles");
        return (document.Blocks[block].Data, offset, count);
    }

    // Nodes of moved points follow them; nodes of deleted points go with their edges, the chains losing those edges
    // (an empty chain goes too).
    private static void UpdateChains(MetaDocument document, byte[] root, ChainChanges changes)
    {
        if (changes.IsEmpty)
            return;
        var (nodeData, nodeAt, nodeCount) = RootArray(document, root, R.RegionChainNodes, ChainNodeSize);
        if (nodeCount == 0)
            return;
        var nodeMap = new int[nodeCount];
        var nodes = new List<byte[]>();
        for (int i = 0; i < nodeCount; i++)
        {
            var node = nodeData.AsSpan(nodeAt + i * ChainNodeSize, ChainNodeSize).ToArray();
            if (changes.Gone.Contains(NodePosition(node)))
            {
                nodeMap[i] = -1;
                continue;
            }
            MoveNode(node, changes);
            nodeMap[i] = nodes.Count;
            nodes.Add(node);
        }
        if (nodes.Count == nodeCount)
        {
            // Nothing removed: moved in place.
            for (int i = 0; i < nodeCount; i++)
                nodes[i].CopyTo(nodeData.AsSpan(nodeAt + i * ChainNodeSize));
            return;
        }

        var (edgeData, edgeAt, edgeCount) = RootArray(document, root, R.RegionChainEdges, ChainEdgeSize);
        var edgeMap = new int[edgeCount];
        var edges = new List<byte[]>();
        foreach (var node in nodes)
            node[NodeHasIncoming] = node[NodeHasOutgoing] = 0;
        for (int i = 0; i < edgeCount; i++)
        {
            var edge = edgeData.AsSpan(edgeAt + i * ChainEdgeSize, ChainEdgeSize).ToArray();
            int from = BinaryPrimitives.ReadUInt16LittleEndian(edge), to = BinaryPrimitives.ReadUInt16LittleEndian(edge.AsSpan(2));
            int newFrom = from < nodeCount ? nodeMap[from] : -1, newTo = to < nodeCount ? nodeMap[to] : -1;
            if (newFrom < 0 || newTo < 0)
            {
                edgeMap[i] = -1;
                continue;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(edge, (ushort)newFrom);
            BinaryPrimitives.WriteUInt16LittleEndian(edge.AsSpan(2), (ushort)newTo);
            nodes[newFrom][NodeHasOutgoing] = 1;
            nodes[newTo][NodeHasIncoming] = 1;
            edgeMap[i] = edges.Count;
            edges.Add(edge);
        }

        var (chainData, chainAt, chainCount) = RootArray(document, root, R.RegionChains, ChainSize);
        var chains = new List<(byte[] Chain, List<ushort> Ids)>();
        uint idBlockKind = UnsignedShortBlock;
        for (int i = 0; i < chainCount; i++)
        {
            var chain = chainData.AsSpan(chainAt + i * ChainSize, ChainSize).ToArray();
            var (ids, idAt, idCount) = RootArray(document, chain, ChainEdgeIds, 2);
            if (idCount > 0)
                idBlockKind = document.Blocks[MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(chain.AsSpan(ChainEdgeIds))).Block].Structure;
            var kept = new List<ushort>();
            for (int k = 0; k < idCount; k++)
            {
                int id = BinaryPrimitives.ReadUInt16LittleEndian(ids.AsSpan(idAt + k * 2));
                if (id < edgeCount && edgeMap[id] >= 0)
                    kept.Add((ushort)edgeMap[id]);
            }
            if (kept.Count > 0)
                chains.Add((chain, kept));
        }
        // Every chain's edge list, back to back in one new block.
        int total = chains.Sum(c => c.Ids.Count);
        int idBlock = -1;
        if (total > 0)
        {
            var idBytes = new byte[total * 2];
            int at = 0;
            foreach (var (_, ids) in chains)
            {
                foreach (ushort id in ids)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(idBytes.AsSpan(at), id);
                    at += 2;
                }
            }
            document.Blocks.Add(new MetaDocument.Block(idBlockKind, idBytes));
            idBlock = document.Blocks.Count - 1;
        }
        int next = 0;
        foreach (var (chain, ids) in chains)
        {
            var field = chain.AsSpan(ChainEdgeIds);
            BinaryPrimitives.WriteUInt64LittleEndian(field, MetaDocument.Pointer(idBlock, next * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(field[8..], (ushort)ids.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(field[10..], (ushort)ids.Count);
            next += ids.Count;
        }
        SetArray(document, root, R.RegionChainNodes, [.. nodes.SelectMany(n => n)], nodes.Count, 0);
        SetArray(document, root, R.RegionChainEdges, [.. edges.SelectMany(e => e)], edges.Count, 0);
        SetArray(document, root, R.RegionChains, [.. chains.SelectMany(c => c.Chain)], chains.Count, 0);
    }

    private static List<uint> ReadTypes(MetaResource meta, int region)
    {
        var (offset, count) = R.TypeList(meta, region);
        var types = new List<uint>(count);
        for (int i = 0; i < count; i++)
            types.Add(meta.Data.UInt32(offset + i * 4));
        return types;
    }

    private static Dictionary<uint, byte> TypeIndices(List<uint> types)
    {
        var indices = new Dictionary<uint, byte>();
        for (int i = types.Count - 1; i >= 0; i--)
            indices[types[i]] = (byte)i;
        return indices;
    }

    private static void WritePoint(Span<byte> target, ScenarioValues v, Dictionary<uint, byte> types)
    {
        if (!types.TryGetValue(v.TypeHash, out byte type))
            throw new InvalidDataException("type de scénario absent du fichier");
        target[R.PointType] = type;
        target[R.PointProbability] = v.Probability;
        target[R.PointAvailability] = v.Availability;
        target[R.PointStartHour] = v.StartHour;
        target[R.PointEndHour] = v.EndHour;
        target[R.PointRadius] = v.Radius;
        target[R.PointTimeTillPedLeaves] = v.TimeTillPedLeaves;
        BinaryPrimitives.WriteUInt32LittleEndian(target[R.PointFlags..], v.Flags);
        var p = target[R.PointPositionAndDirection..];
        BinaryPrimitives.WriteSingleLittleEndian(p, v.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(p[4..], v.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], v.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(p[12..], v.Heading);
    }
}
