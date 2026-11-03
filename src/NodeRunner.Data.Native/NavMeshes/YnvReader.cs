using System.Numerics;
using NodeRunner.Data.NavMeshes;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.NavMeshes;

// reader for navmesh files (.ynv).
public sealed class YnvReader : INavMeshReader
{
    private const int HeaderSize = 0x170;

    // Header fields (after the 16-byte resource file base).
    private const int ContentFlagsOffset = 0x10;
    private const int BoundsSizeOffset = 0x60;
    private const int VerticesListOffset = 0x70;
    private const int IndicesListOffset = 0x80;
    private const int PolygonsListOffset = 0x118;
    private const int SectorTreeOffset = 0x120;
    private const int AreaIdOffset = 0x140;

    private const uint VehicleMeshFlag = 4;

    private const int VertexSize = 6;   // 3 x ushort, fraction of the tile bounds
    private const int IndexSize = 2;
    private const int PolygonSize = 48;

    // A "list" is split into parts: header { item count at 0x08, parts pointer at 0x10, part count at 0x20 },
    // each part { items pointer, item count } (16 bytes).
    private const int ListPartSize = 16;

    public NavMeshTile Read(ReadOnlySpan<byte> data)
    {
        var res = new ResourceData(data);
        if (res.Length < HeaderSize)
            throw new InvalidDataException("Fichier .ynv trop court.");

        var size = ReadVector3(res, BoundsSizeOffset);

        // Vertex positions are relative to the minimum corner stored at the root of the sector tree.
        int tree = res.ResolveArray(res.UInt64(SectorTreeOffset), 1, 32, "l'arbre de secteurs");
        var min = ReadVector3(res, tree);

        var vertices = new List<Vector3>();
        ReadList(res, VerticesListOffset, VertexSize, "les sommets", (r, o) =>
        {
            var fraction = new Vector3(r.UInt16(o), r.UInt16(o + 2), r.UInt16(o + 4)) / ushort.MaxValue;
            vertices.Add(min + fraction * size);
        });

        var indices = new List<ushort>();
        ReadList(res, IndicesListOffset, IndexSize, "les indices", (r, o) => indices.Add(r.UInt16(o)));

        var polygons = new List<NavMeshPolygon>();
        ReadList(res, PolygonsListOffset, PolygonSize, "les polygones", (r, o) => polygons.Add(new NavMeshPolygon(
            FirstIndex: r.UInt16(o + 4),
            IndexCount: r.UInt16(o + 2) >> 5,
            Flags0: r.UInt16(o),
            Flags1: r.UInt32(o + 0x24),
            Flags2: r.UInt32(o + 0x28))));

        return new NavMeshTile
        {
            AreaId = (int)res.UInt32(AreaIdOffset),
            BoundsMin = min,
            BoundsMax = min + size,
            Vertices = vertices,
            Indices = indices,
            Polygons = polygons,
            IsVehicleMesh = (res.UInt32(ContentFlagsOffset) & VehicleMeshFlag) != 0,
        };
    }

    private delegate void ItemReader(ResourceData data, int offset);

    private static void ReadList(ResourceData res, int pointerOffset, int itemSize, string what, ItemReader read)
    {
        ulong listPointer = res.UInt64(pointerOffset);
        if (listPointer == 0)
            return;

        int list = res.ResolveArray(listPointer, 1, 0x30, what);
        int partCount = (int)res.UInt32(list + 0x20);
        int parts = res.ResolveArray(res.UInt64(list + 0x10), partCount, ListPartSize, what);

        for (int p = 0; p < partCount; p++)
        {
            int part = parts + p * ListPartSize;
            int count = (int)res.UInt32(part + 8);
            int items = res.ResolveArray(res.UInt64(part), count, itemSize, what);
            for (int i = 0; i < count; i++)
                read(res, items + i * itemSize);
        }
    }

    private static Vector3 ReadVector3(ResourceData res, int offset) => new(
        BitConverter.UInt32BitsToSingle(res.UInt32(offset)),
        BitConverter.UInt32BitsToSingle(res.UInt32(offset + 4)),
        BitConverter.UInt32BitsToSingle(res.UInt32(offset + 8)));
}
