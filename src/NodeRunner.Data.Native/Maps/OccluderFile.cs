using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Reads the occluders of a map file, writes them back changed, and builds new occluder map files.
public sealed class OccluderFile : IOccluderReader, IOccluderWriter
{
    private const int MapBoxOccluders = 128;
    private const int MapOccludeModels = 144;
    private const int BoxSize = 16;
    private const int ModelSize = 64;

    // Fields of OccludeModel.
    private const int ModelMin = 0;
    private const int ModelMax = 16;
    private const int ModelDataSize = 32;
    private const int ModelData = 40;
    private const int ModelVertexBytes = 48;
    private const int ModelTriangleCount = 50;

    private const int TriangleCountFlag = 0x8000;

    private const uint BoxStructure = 0x3A282E1D; // CBoxOccluder.
    private const int MapName = 8;
    private const int MapContentFlags = 20;
    private const uint OccluderContentFlag = 32; // Content flags of every occluder map file of the game.

    public MapOccluders ReadOccluders(ReadOnlySpan<byte> map)
    {
        var document = Open(map);
        var mapData = document.Blocks[document.RootBlock].Data;

        var boxes = new List<OccluderBox>();
        var (boxBlock, boxOffset, boxCount) = Array(mapData, MapBoxOccluders);
        for (int i = 0; i < boxCount; i++)
            boxes.Add(ReadBox(document.Blocks[boxBlock].Data.AsSpan(boxOffset + i * BoxSize), i));

        var models = new List<OccluderModel>();
        var (modelBlock, modelOffset, modelCount) = Array(mapData, MapOccludeModels);
        for (int i = 0; i < modelCount; i++)
        {
            var (vertices, indices) = ReadModel(document, document.Blocks[modelBlock].Data.AsSpan(modelOffset + i * ModelSize));
            models.Add(new OccluderModel(i, vertices, indices));
        }
        return new MapOccluders(boxes, models);
    }

    public MapFileContent RemoveOccluders(ReadOnlySpan<byte> map, IReadOnlySet<int> boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> triangles) =>
        WriteOccluders(map, ReadOccluders(map).Boxes.Where(b => !boxes.Contains(b.Index)).ToList(), triangles);

    public MapFileContent WriteOccluders(ReadOnlySpan<byte> map, IReadOnlyList<OccluderBox> boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> triangles)
    {
        var document = Open(map);
        var mapData = document.Blocks[document.RootBlock].Data;
        var original = ReadOccluders(map).Boxes;

        // Boxes: the array is the only content of its block in the game's files, so the block is replaced.
        var (boxBlock, boxOffset, boxCount) = Array(mapData, MapBoxOccluders);
        var items = new byte[boxes.Count * BoxSize];
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var item = items.AsSpan(i * BoxSize, BoxSize);
            // Unchanged boxes keep their bytes (the angle read back is not always the stored sine and cosine).
            if (box.Index >= 0 && box.Index < boxCount && box == original[box.Index])
                document.Blocks[boxBlock].Data.AsSpan(boxOffset + box.Index * BoxSize, BoxSize).CopyTo(item);
            else
                WriteBox(item, box);
        }
        if (boxes.Count == 0)
        {
            if (boxBlock >= 0)
                document.Blocks[boxBlock].Data.AsSpan(boxOffset, boxCount * BoxSize).Clear();
            SetCount(mapData, MapBoxOccluders, boxBlock, boxOffset, 0);
        }
        else if (boxBlock >= 0)
        {
            var data = document.Blocks[boxBlock].Data;
            if (data.Length != boxOffset + boxCount * BoxSize)
                throw new InvalidDataException("Bloc des boîtes d'occlusion inattendu.");
            CheckBlockLength(boxOffset + items.Length);
            document.Blocks[boxBlock].Data = [.. data.AsSpan(0, boxOffset), .. items];
            SetCount(mapData, MapBoxOccluders, boxBlock, boxOffset, boxes.Count);
        }
        else
        {
            if (document.FindStructure(BoxStructure) is null)
                throw new InvalidDataException("Ce fichier ne décrit pas les boîtes d'occlusion.");
            CheckBlockLength(items.Length);
            document.Blocks.Add(new MetaDocument.Block(BoxStructure, items));
            SetCount(mapData, MapBoxOccluders, document.Blocks.Count - 1, 0, boxes.Count);
        }

        var (modelBlock, modelOffset, modelCount) = Array(mapData, MapOccludeModels);
        var bounds = Bounds.Empty;
        if (modelCount > 0)
        {
            var data = document.Blocks[modelBlock].Data;
            int kept = 0;
            for (int i = 0; i < modelCount; i++)
            {
                int item = modelOffset + i * ModelSize;
                if (triangles.TryGetValue(i, out var removed) && removed.Count > 0 && !Shorten(document, data.AsSpan(item, ModelSize), removed))
                    continue; // No triangle left: the mesh goes.
                if (kept != i)
                    data.AsSpan(item, ModelSize).CopyTo(data.AsSpan(modelOffset + kept * ModelSize));
                foreach (var v in ReadModel(document, data.AsSpan(modelOffset + kept * ModelSize, ModelSize)).Vertices)
                    bounds = bounds.Add(v);
                kept++;
            }
            data.AsSpan(modelOffset + kept * ModelSize, (modelCount - kept) * ModelSize).Clear();
            SetCount(mapData, MapOccludeModels, modelBlock, modelOffset, kept);
        }

        foreach (var box in boxes)
            foreach (var corner in box.Corners())
                bounds = bounds.Add(corner);
        if (!bounds.IsEmpty)
        {
            GrowExtents(mapData, YmapReader.MapStreamingMin, YmapReader.MapStreamingMax, bounds);
            GrowExtents(mapData, YmapReader.MapEntitiesMin, YmapReader.MapEntitiesMax, bounds);
        }

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    public MapFileContent BuildOccluders(ReadOnlySpan<byte> template, string name, IReadOnlyList<OccluderBox> boxes)
    {
        var document = Open(template);
        if (document.FindStructure(BoxStructure) is null)
            throw new InvalidDataException("Le modèle ne décrit pas les boîtes d'occlusion.");
        if (boxes.Count == 0)
            throw new InvalidDataException("Aucune boîte d'occlusion à écrire.");
        var mapData = new byte[document.Blocks[document.RootBlock].Data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(MapName), Hashing.Joaat.Hash(name));
        BinaryPrimitives.WriteUInt32LittleEndian(mapData.AsSpan(MapContentFlags), OccluderContentFlag);

        var items = new byte[boxes.Count * BoxSize];
        var bounds = Bounds.Empty;
        for (int i = 0; i < boxes.Count; i++)
        {
            WriteBox(items.AsSpan(i * BoxSize, BoxSize), boxes[i]);
            foreach (var corner in boxes[i].Corners())
                bounds = bounds.Add(corner);
        }
        CheckBlockLength(items.Length);
        WriteVector3(mapData.AsSpan(YmapReader.MapStreamingMin), bounds.Min);
        WriteVector3(mapData.AsSpan(YmapReader.MapStreamingMax), bounds.Max);
        WriteVector3(mapData.AsSpan(YmapReader.MapEntitiesMin), bounds.Min);
        WriteVector3(mapData.AsSpan(YmapReader.MapEntitiesMax), bounds.Max);

        document.Blocks.Clear();
        document.Name = null; // As in the game's occluder files.
        document.Blocks.Add(new MetaDocument.Block(YmapReader.MapDataStructure, mapData));
        document.RootBlock = 0;
        document.Blocks.Add(new MetaDocument.Block(BoxStructure, items));
        SetCount(mapData, MapBoxOccluders, 1, 0, boxes.Count);

        var (content, pageFlags) = document.Write();
        return new MapFileContent(content, pageFlags);
    }

    private static MetaDocument Open(ReadOnlySpan<byte> map)
    {
        var document = MetaDocument.Read(map);
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count || document.Blocks[root].Structure != YmapReader.MapDataStructure)
            throw new InvalidDataException("Ce fichier n'est pas un fichier de carte (.ymap).");
        return document;
    }

    private static OccluderBox ReadBox(ReadOnlySpan<byte> item, int index)
    {
        Span<float> s = stackalloc float[8];
        for (int k = 0; k < 8; k++)
            s[k] = BinaryPrimitives.ReadInt16LittleEndian(item[(k * 2)..]);
        var center = new Vector3(s[0], s[1], s[2]) / 4f;
        var size = new Vector3(s[4], s[5], s[6]) / 4f;
        // Direction of the box's X axis: (iSinZ, iCosZ), names as in the game's description.
        float angle = MathF.Atan2(s[3], s[7]);
        return new OccluderBox(index, center, size, angle);
    }

    private static (Vector3[] Vertices, byte[] Indices) ReadModel(MetaDocument document, ReadOnlySpan<byte> item)
    {
        int dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(item[ModelDataSize..]);
        int vertexBytes = BinaryPrimitives.ReadUInt16LittleEndian(item[ModelVertexBytes..]);
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(item[ModelData..]));
        if (block < 0 || block >= document.Blocks.Count || dataSize < vertexBytes || offset + dataSize > document.Blocks[block].Data.Length)
            throw new InvalidDataException("Maillage d'occlusion invalide.");
        var data = document.Blocks[block].Data.AsSpan(offset, dataSize);

        var vertices = new Vector3[vertexBytes / 12];
        for (int k = 0; k < vertices.Length; k++)
            vertices[k] = ReadVector3(data[(k * 12)..]);
        var indices = data[vertexBytes..].ToArray();
        indices = indices[..(indices.Length / 3 * 3)];
        if (indices.Any(i => i >= vertices.Length))
            throw new InvalidDataException("Maillage d'occlusion invalide (sommet hors liste).");
        return (vertices, indices);
    }

    private static bool Shorten(MetaDocument document, Span<byte> item, IReadOnlySet<int> removed)
    {
        var (vertices, indices) = ReadModel(document, item);
        var newVertices = new List<Vector3>();
        var newIndices = new List<byte>();
        var renumbered = new Dictionary<int, byte>();
        for (int t = 0; t < indices.Length / 3; t++)
        {
            if (removed.Contains(t))
                continue;
            for (int k = 0; k < 3; k++)
            {
                int old = indices[t * 3 + k];
                if (!renumbered.TryGetValue(old, out byte number))
                {
                    number = (byte)newVertices.Count;
                    renumbered[old] = number;
                    newVertices.Add(vertices[old]);
                }
                newIndices.Add(number);
            }
        }
        if (newIndices.Count == 0)
            return false;

        // Fewer vertices and corners than before: fits in the old data.
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(item[ModelData..]));
        int oldSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(item[ModelDataSize..]);
        var data = document.Blocks[block].Data.AsSpan(offset, oldSize);
        data.Clear();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int k = 0; k < newVertices.Count; k++)
        {
            WriteVector3(data[(k * 12)..], newVertices[k]);
            min = Vector3.Min(min, newVertices[k]);
            max = Vector3.Max(max, newVertices[k]);
        }
        int vertexBytes = newVertices.Count * 12;
        for (int k = 0; k < newIndices.Count; k++)
            data[vertexBytes + k] = newIndices[k];

        // Bounds; the 4th float after each holds min.X / max.X in the game's files.
        WriteVector3(item[ModelMin..], min);
        BinaryPrimitives.WriteSingleLittleEndian(item[(ModelMin + 12)..], min.X);
        WriteVector3(item[ModelMax..], max);
        BinaryPrimitives.WriteSingleLittleEndian(item[(ModelMax + 12)..], max.X);
        BinaryPrimitives.WriteUInt32LittleEndian(item[ModelDataSize..], (uint)(vertexBytes + newIndices.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(item[ModelVertexBytes..], (ushort)vertexBytes);
        int flag = BinaryPrimitives.ReadUInt16LittleEndian(item[ModelTriangleCount..]) & TriangleCountFlag;
        BinaryPrimitives.WriteUInt16LittleEndian(item[ModelTriangleCount..], (ushort)(newIndices.Count / 3 | flag));
        return true;
    }

    private static (int Block, int Offset, int Count) Array(byte[] mapData, int field)
    {
        var (block, offset) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(mapData.AsSpan(field)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(mapData.AsSpan(field + 8));
        return block < 0 ? (-1, 0, 0) : (block, offset, count);
    }

    private static void SetCount(byte[] mapData, int field, int block, int offset, int count)
    {
        var span = mapData.AsSpan(field);
        BinaryPrimitives.WriteUInt64LittleEndian(span, count == 0 ? 0 : MetaDocument.Pointer(block, offset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)count);  // Count.
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)count); // Capacity.
    }

    // Centre and size in quarters of a metre, then sine and cosine of the angle times 16384 (OccluderBox units).
    private static void WriteBox(Span<byte> item, OccluderBox box)
    {
        static short Value(float v) => v is >= short.MinValue and <= short.MaxValue
            ? (short)MathF.Round(v)
            : throw new InvalidDataException("Boîte d'occlusion hors des limites de la carte.");
        float p = 1f / OccluderBox.PositionUnit;
        Span<short> s =
        [
            Value(box.Center.X * p), Value(box.Center.Y * p), Value(box.Center.Z * p), Value(MathF.Sin(box.Angle) * OccluderBox.AngleUnit),
            Value(box.Size.X * p), Value(box.Size.Y * p), Value(box.Size.Z * p), Value(MathF.Cos(box.Angle) * OccluderBox.AngleUnit),
        ];
        for (int k = 0; k < 8; k++)
            BinaryPrimitives.WriteInt16LittleEndian(item[(k * 2)..], s[k]);
    }

    private static void CheckBlockLength(int length)
    {
        if (length > MetaDocument.MaxBlockLength)
            throw new InvalidDataException($"Trop de boîtes d'occlusion dans un fichier ({MetaDocument.MaxBlockLength / BoxSize} au plus).");
    }

    private static void GrowExtents(byte[] mapData, int minField, int maxField, Bounds bounds)
    {
        var min = ReadVector3(mapData.AsSpan(minField));
        var max = ReadVector3(mapData.AsSpan(maxField));
        bool empty = min == Vector3.Zero && max == Vector3.Zero;
        WriteVector3(mapData.AsSpan(minField), empty ? bounds.Min : Vector3.Min(min, bounds.Min));
        WriteVector3(mapData.AsSpan(maxField), empty ? bounds.Max : Vector3.Max(max, bounds.Max));
    }

    private readonly record struct Bounds(Vector3 Min, Vector3 Max)
    {
        public static Bounds Empty => new(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        public bool IsEmpty => Min.X > Max.X;
        public Bounds Add(Vector3 p) => new(Vector3.Min(Min, p), Vector3.Max(Max, p));
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data),
        BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(data[8..]));

    private static void WriteVector3(Span<byte> data, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(data, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(data[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data[8..], v.Z);
    }
}
