using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

/// <summary>
/// Reads the occluders of a map file and removes some of them. Box occluders are 16 bytes of shorts (centre and size in
/// quarters of a metre, direction of the box's X axis as sine and cosine); occluder meshes are 64 bytes (bounds, then a
/// pointer to a data block holding the vertices, 3 floats each, followed by one byte per corner of each triangle).
/// Removal happens in place: kept items move to the front of their list, a shortened mesh is written over its old
/// data, so every other pointer of the file stays valid.
/// </summary>
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

    /// <summary>Set in the triangle count of every mesh of the game (meaning unknown), kept when writing.</summary>
    private const int TriangleCountFlag = 0x8000;

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

    public MapFileContent RemoveOccluders(ReadOnlySpan<byte> map, IReadOnlySet<int> boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> triangles)
    {
        var document = Open(map);
        var mapData = document.Blocks[document.RootBlock].Data;

        var (boxBlock, boxOffset, boxCount) = Array(mapData, MapBoxOccluders);
        if (boxCount > 0)
        {
            var data = document.Blocks[boxBlock].Data;
            int kept = 0;
            for (int i = 0; i < boxCount; i++)
            {
                if (boxes.Contains(i))
                    continue;
                if (kept != i)
                    data.AsSpan(boxOffset + i * BoxSize, BoxSize).CopyTo(data.AsSpan(boxOffset + kept * BoxSize));
                kept++;
            }
            data.AsSpan(boxOffset + kept * BoxSize, (boxCount - kept) * BoxSize).Clear();
            SetCount(mapData, MapBoxOccluders, boxBlock, boxOffset, kept);
        }

        var (modelBlock, modelOffset, modelCount) = Array(mapData, MapOccludeModels);
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
                kept++;
            }
            data.AsSpan(modelOffset + kept * ModelSize, (modelCount - kept) * ModelSize).Clear();
            SetCount(mapData, MapOccludeModels, modelBlock, modelOffset, kept);
        }

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

    /// <summary>Rewrites a mesh without the <paramref name="removed"/> triangles, over its old data. False if none is left.</summary>
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

    /// <summary>An array field of CMapData: block and offset of its first item, number of items.</summary>
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
