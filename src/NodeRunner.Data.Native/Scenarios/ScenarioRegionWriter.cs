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
        var types = TypeIndices(ReadTypes(meta, start));
        for (int i = 0; i < offsets.Count; i++)
            WritePoint(result.AsSpan(offsets[i], R.PointSize), region.Points[i].Values, types);
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
