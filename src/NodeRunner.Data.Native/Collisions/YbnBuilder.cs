using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Collisions;

// Writes a complete collision file (.ybn, resource version 43) from bounds read from XML. The whole content goes into
// a single system page, as YbnReader.Edit does.
internal sealed class YbnBuilder
{
    private const int Version = 43;
    private const ulong SystemBase = 0x50000000;

    // Virtual table words found in the game's files for each bounds type.
    private static uint Vft(byte type) => type switch
    {
        XmlBound.Sphere => 1080221960,
        XmlBound.Capsule => 1080213112,
        XmlBound.Box => 1080221016,
        XmlBound.Geometry => 1080226408,
        XmlBound.GeometryBvh => 1080228536,
        XmlBound.Composite => 1080212136,
        XmlBound.Disc => 1080229960,
        XmlBound.Cylinder => 1080202872,
        _ => 1080221960,
    };

    private byte[] _data = new byte[0x4000];
    private int _length;
    private ulong _address = SystemBase;

    public static byte[] Build(XmlBound root)
    {
        var builder = new YbnBuilder();
        builder.Write(root, isRoot: true);

        int pageSize = 0x2000;
        while (pageSize < builder._length)
            pageSize *= 2;
        var content = new byte[pageSize];
        builder._data.AsSpan(0, builder._length).CopyTo(content);
        uint systemFlags = ((uint)(Version >> 4) & 0xF) << 28 | MetaDocument.PageFlags(1, pageSize);
        uint graphicsFlags = ((uint)Version & 0xF) << 28;
        return ResourceFile.Write(new ResourceHeader(Version, systemFlags, graphicsFlags), content);
    }

    // Bounds embedded in another resource (a drawable's collision), written for the address it will have there: no
    // page list. Its length does not depend on the address.
    internal static byte[] BuildEmbedded(XmlBound root, ulong address)
    {
        var builder = new YbnBuilder { _address = address };
        builder.Write(root, isRoot: false);
        return builder._data.AsSpan(0, builder._length).ToArray();
    }

    private int Alloc(int size)
    {
        int at = (_length + 15) & ~15;
        int end = at + Math.Max(size, 0);
        if (end > _data.Length)
            Array.Resize(ref _data, Math.Max(end, _data.Length * 2));
        _length = end;
        return at;
    }

    private ulong Pointer(int offset) => _address + (ulong)offset;

    private Span<byte> At(int o, int n) => _data.AsSpan(o, n);
    private void U8(int o, byte v) => _data[o] = v;
    private void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(At(o, 2), (ushort)v);
    private void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(At(o, 4), v);
    private void U64(int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(At(o, 8), v);
    private void F32(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(At(o, 4), v);
    private void Vec3(int o, Vector3 v) { F32(o, v.X); F32(o + 4, v.Y); F32(o + 8, v.Z); }
    private void Vec4(int o, Vector3 v, float w) { Vec3(o, v); F32(o + 12, w); }

    private static int BlockSize(byte type) => type switch
    {
        XmlBound.Composite => 0xB0,
        XmlBound.Geometry => 0x130,
        XmlBound.GeometryBvh => 0x150,
        XmlBound.Capsule or XmlBound.Disc or XmlBound.Cylinder => 0x80,
        _ => 0x70,
    };

    // Writes the bounds and returns its block offset and its box in its own space.
    private (int Offset, Vector3 Min, Vector3 Max) Write(XmlBound b, bool isRoot)
    {
        int o = Alloc(BlockSize(b.Type));
        if (isRoot)
        {
            int pages = Alloc(16 + 8);
            U8(pages + 8, 1); // one system page
            U64(o + 0x08, Pointer(pages));
        }
        U32(o, Vft(b.Type));
        U32(o + 0x04, 1);

        Vector3 min = b.BoxMin, max = b.BoxMax;
        if (b.Type == XmlBound.Composite)
            (min, max) = WriteComposite(o, b);
        else if (b.Type is XmlBound.Geometry or XmlBound.GeometryBvh)
            (min, max) = WriteGeometry(o, b);

        bool computed = b.Type is XmlBound.Composite or XmlBound.Geometry or XmlBound.GeometryBvh;
        var center = computed ? (min + max) * 0.5f : b.BoxCenter;
        U8(o + 0x10, b.Type);
        F32(o + 0x14, computed ? (max - center).Length() : b.SphereRadius);
        Vec3(o + 0x20, max);
        F32(o + 0x2C, b.Margin);
        Vec3(o + 0x30, min);
        U32(o + 0x3C, b.UnkType);
        Vec3(o + 0x40, center);
        U8(o + 0x4C, b.MaterialIndex);
        U8(o + 0x4D, b.ProceduralId);
        U8(o + 0x4E, (byte)((b.RoomId & 0x1F) | (b.PedDensity & 0x7) << 5));
        U8(o + 0x4F, b.UnkFlags);
        Vec3(o + 0x50, computed ? center : b.SphereCenter);
        U8(o + 0x5C, b.PolyFlags);
        U8(o + 0x5D, b.MaterialColourIndex);
        Vec3(o + 0x60, b.Inertia);
        F32(o + 0x6C, b.Volume);
        return (o, min, max);
    }

    private (Vector3 Min, Vector3 Max) WriteComposite(int o, XmlBound b)
    {
        int count = b.Children.Count;
        if (count > ushort.MaxValue)
            throw new InvalidDataException("Trop de morceaux de collision dans le composite.");
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (b.Children[i] is not { } child)
            {
                offsets[i] = -1;
                boxes.Add((Vector3.Zero, Vector3.Zero));
                continue;
            }
            var (at, cMin, cMax) = Write(child, isRoot: false);
            offsets[i] = at;
            var (tMin, tMax) = TransformBox(cMin, cMax, child.Transform);
            boxes.Add((tMin, tMax));
            min = Vector3.Min(min, tMin);
            max = Vector3.Max(max, tMax);
        }
        if (min.X > max.X)
            (min, max) = (b.BoxMin, b.BoxMax);
        if (count == 0)
            return (min, max);

        int children = Alloc(count * 8);
        int transforms = Alloc(count * 64);
        int childBoxes = Alloc(count * 32);
        int flags1 = Alloc(count * 8);
        int flags2 = Alloc(count * 8);
        for (int i = 0; i < count; i++)
        {
            var child = b.Children[i];
            if (offsets[i] >= 0)
                U64(children + i * 8, Pointer(offsets[i]));
            var m = child?.Transform ?? Matrix4x4.Identity;
            int t = transforms + i * 64;
            Vec4(t, new Vector3(m.M11, m.M12, m.M13), 0);
            Vec4(t + 16, new Vector3(m.M21, m.M22, m.M23), BitConverter.Int32BitsToSingle(1));
            Vec4(t + 32, new Vector3(m.M31, m.M32, m.M33), BitConverter.Int32BitsToSingle(1));
            Vec4(t + 48, new Vector3(m.M41, m.M42, m.M43), 0);
            Vec4(childBoxes + i * 32, boxes[i].Min, float.Epsilon);
            Vec4(childBoxes + i * 32 + 16, boxes[i].Max, child?.Margin ?? 0);
            U32(flags1 + i * 8, child?.Flags1 ?? 0);
            U32(flags1 + i * 8 + 4, child?.Flags2 ?? 0);
            U32(flags2 + i * 8, child?.Flags1 ?? 0);
            U32(flags2 + i * 8 + 4, child?.Flags2 ?? 0);
        }
        U64(o + 0x70, Pointer(children));
        U64(o + 0x78, Pointer(transforms));
        U64(o + 0x80, Pointer(transforms));
        U64(o + 0x88, Pointer(childBoxes));
        U64(o + 0x90, Pointer(flags1));
        U64(o + 0x98, Pointer(flags2));
        U16(o + 0xA0, count);
        U16(o + 0xA2, count);

        // The game's composites only have a search tree from 6 children on; one child per leaf, capacity 2n+1.
        if (count > 5)
        {
            var tree = BvhBuilder.Build(boxes, 1, reorder: false);
            U64(o + 0xA8, Pointer(WriteTree(tree, min, max, count * 2 + 1)));
        }
        return (min, max);
    }

    private (Vector3 Min, Vector3 Max) WriteGeometry(int o, XmlBound b)
    {
        var vertices = b.Vertices;
        if (vertices.Count > 0x7FFF)
            throw new InvalidDataException($"Trop de sommets dans une géométrie de collision ({vertices.Count}, 32 767 au plus).");
        if (b.Polygons.Count > 0x7FFF)
            throw new InvalidDataException($"Trop de polygones dans une géométrie de collision ({b.Polygons.Count}, 32 767 au plus).");
        if (b.Materials.Count > 255)
            throw new InvalidDataException("Trop de matériaux dans une géométrie de collision (255 au plus).");
        foreach (var p in b.Polygons)
        {
            if (p.Material < 0 || p.Material >= Math.Max(b.Materials.Count, 1))
                throw new InvalidDataException($"Polygone avec un matériau inexistant (m=\"{p.Material}\").");
            foreach (int v in p.Kind switch
            {
                XmlPolygon.Triangle => new[] { p.V1, p.V2, p.V3 },
                XmlPolygon.Sphere => [p.V1],
                XmlPolygon.Box => [p.V1, p.V2, p.V3, p.V4],
                _ => [p.V1, p.V2],
            })
            {
                if (v < 0 || v >= vertices.Count)
                    throw new InvalidDataException($"Polygone qui renvoie à un sommet inexistant ({v}).");
            }
        }

        var center = b.GeometryCenter;
        var polygons = b.Polygons.ToList();
        var boxes = polygons.Select(p => PolygonBox(p, vertices, center)).ToList();

        BvhBuilder.Result? tree = null;
        if (b.Type == XmlBound.GeometryBvh && polygons.Count > 0)
        {
            tree = BvhBuilder.Build(boxes, 4, reorder: true);
            polygons = tree.Order.Select(i => b.Polygons[i]).ToList();
            boxes = tree.Order.Select(i => boxes[i]).ToList();
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (bMin, bMax) in boxes)
        {
            min = Vector3.Min(min, bMin);
            max = Vector3.Max(max, bMax);
        }
        if (boxes.Count == 0)
            (min, max) = (b.BoxMin, b.BoxMax);

        // Vertices: 3 shorts each, times the quantum, around the geometry centre.
        var extent = new Vector3(1e-6f);
        foreach (var v in vertices)
            extent = Vector3.Max(extent, Vector3.Abs(v));
        var quantum = extent / 32767f;
        int vertexBlock = Alloc(vertices.Count * 6);
        for (int i = 0; i < vertices.Count; i++)
        {
            var q = Vector3.Clamp(Vector3.Round(vertices[i] / quantum), new Vector3(-32767), new Vector3(32767));
            U16(vertexBlock + i * 6, (short)q.X);
            U16(vertexBlock + i * 6 + 2, (short)q.Y);
            U16(vertexBlock + i * 6 + 4, (short)q.Z);
        }

        int polygonBlock = Alloc(polygons.Count * 16);
        var neighbours = TriangleNeighbours(polygons);
        for (int i = 0; i < polygons.Count; i++)
        {
            var p = polygons[i];
            int at = polygonBlock + i * 16;
            switch (p.Kind)
            {
                case XmlPolygon.Triangle:
                    var a = vertices[p.V1];
                    var area = Vector3.Cross(vertices[p.V2] - a, vertices[p.V3] - a).Length() * 0.5f;
                    F32(at, area);
                    U16(at + 4, p.V1 | ((p.VertexFlags & 1) != 0 ? 0x8000 : 0));
                    U16(at + 6, p.V2 | ((p.VertexFlags & 2) != 0 ? 0x8000 : 0));
                    U16(at + 8, p.V3 | ((p.VertexFlags & 4) != 0 ? 0x8000 : 0));
                    U16(at + 10, neighbours[i * 3]);
                    U16(at + 12, neighbours[i * 3 + 1]);
                    U16(at + 14, neighbours[i * 3 + 2]);
                    break;
                case XmlPolygon.Sphere:
                    U16(at + 2, p.V1);
                    F32(at + 4, p.Radius);
                    break;
                case XmlPolygon.Capsule:
                case XmlPolygon.Cylinder:
                    U16(at + 2, p.V1);
                    F32(at + 4, p.Radius);
                    U16(at + 8, p.V2);
                    break;
                case XmlPolygon.Box:
                    U16(at + 4, p.V1);
                    U16(at + 6, p.V2);
                    U16(at + 8, p.V3);
                    U16(at + 10, p.V4);
                    break;
            }
            // The polygon type sits in the low 3 bits of the first byte (the triangle area's low mantissa bits).
            _data[at] = (byte)((_data[at] & 0xF8) | p.Kind);
        }

        int materialCount = Math.Max(b.Materials.Count, 1);
        int materialBlock = Alloc(Math.Max(materialCount, 4) * 8);
        for (int i = 0; i < b.Materials.Count; i++)
        {
            U32(materialBlock + i * 8, b.Materials[i].Data1);
            U32(materialBlock + i * 8 + 4, b.Materials[i].Data2);
        }
        int polygonMaterials = Alloc(polygons.Count);
        for (int i = 0; i < polygons.Count; i++)
            _data[polygonMaterials + i] = (byte)polygons[i].Material;

        int materialColours = -1;
        if (b.MaterialColours is { Count: > 0 } mc)
        {
            materialColours = Alloc(Math.Min(mc.Count, 255) * 4);
            for (int i = 0; i < Math.Min(mc.Count, 255); i++)
                U32(materialColours + i * 4, mc[i]);
        }
        int vertexColours = -1;
        if (b.VertexColours is { } vc && vc.Count == vertices.Count && vc.Count > 0)
        {
            vertexColours = Alloc(vc.Count * 4);
            for (int i = 0; i < vc.Count; i++)
                U32(vertexColours + i * 4, vc[i]);
        }

        U16(o + 0x82, vertexColours >= 0 ? 1 : 0);
        U32(o + 0x84, (uint)vertices.Count);
        U64(o + 0x88, polygons.Count > 0 ? Pointer(polygonBlock) : 0);
        Vec3(o + 0x90, quantum);
        F32(o + 0x9C, b.UnkFloat1);
        Vec3(o + 0xA0, center);
        F32(o + 0xAC, b.UnkFloat2);
        U64(o + 0xB0, vertices.Count > 0 ? Pointer(vertexBlock) : 0);
        U64(o + 0xB8, vertexColours >= 0 ? Pointer(vertexColours) : 0);
        U32(o + 0xD0, (uint)vertices.Count);
        U32(o + 0xD4, (uint)polygons.Count);
        U64(o + 0xF0, Pointer(materialBlock));
        U64(o + 0xF8, materialColours >= 0 ? Pointer(materialColours) : 0);
        U64(o + 0x118, polygons.Count > 0 ? Pointer(polygonMaterials) : 0);
        U8(o + 0x120, (byte)Math.Min(b.Materials.Count, 255));
        U8(o + 0x121, (byte)(materialColours >= 0 ? Math.Min(b.MaterialColours!.Count, 255) : 0));

        if (b.Type == XmlBound.GeometryBvh)
        {
            U16(o + 0x140, 0xFFFF);
            if (tree is not null)
                U64(o + 0x130, Pointer(WriteTree(tree, min, max, tree.Nodes.Count)));
        }
        return (min, max);
    }

    // Search tree block (128 bytes) followed by its node and tree arrays.
    private int WriteTree(BvhBuilder.Result tree, Vector3 min, Vector3 max, int nodeCapacity)
    {
        var center = (min + max) * 0.5f;
        var quantum = Vector3.Max(Vector3.Max(max - center, center - min), new Vector3(1e-6f)) / 32767f;
        var inverse = Vector3.One / quantum;

        int block = Alloc(128);
        int nodes = Alloc(nodeCapacity * 16);
        int trees = Alloc(tree.Trees.Count * 16);

        void Box(int at, Vector3 bMin, Vector3 bMax)
        {
            var qMin = Vector3.Clamp((bMin - center) * inverse - Vector3.One, new Vector3(-32767), new Vector3(32767));
            var qMax = Vector3.Clamp((bMax - center) * inverse + Vector3.One, new Vector3(-32767), new Vector3(32767));
            U16(at, (short)MathF.Floor(qMin.X));
            U16(at + 2, (short)MathF.Floor(qMin.Y));
            U16(at + 4, (short)MathF.Floor(qMin.Z));
            U16(at + 6, (short)MathF.Ceiling(qMax.X));
            U16(at + 8, (short)MathF.Ceiling(qMax.Y));
            U16(at + 10, (short)MathF.Ceiling(qMax.Z));
        }

        for (int i = 0; i < nodeCapacity; i++)
        {
            int at = nodes + i * 16;
            if (i < tree.Nodes.Count)
            {
                var node = tree.Nodes[i];
                Box(at, node.Min, node.Max);
                U16(at + 12, node.ItemId);
                U16(at + 14, node.ItemCount);
            }
            else
            {
                U16(at + 12, 1); // empty filler node, as in the game's files
            }
        }
        for (int i = 0; i < tree.Trees.Count; i++)
        {
            var t = tree.Trees[i];
            int at = trees + i * 16;
            Box(at, t.Min, t.Max);
            U16(at + 12, t.FirstNode);
            U16(at + 14, t.EndNode);
        }

        U64(block, Pointer(nodes));
        U32(block + 0x08, (uint)tree.Nodes.Count);
        U32(block + 0x0C, (uint)nodeCapacity);
        Vec4(block + 0x20, min, float.NaN);
        Vec4(block + 0x30, max, float.NaN);
        Vec4(block + 0x40, center, float.NaN);
        Vec4(block + 0x50, inverse, float.NaN);
        Vec4(block + 0x60, quantum, float.NaN);
        U64(block + 0x70, tree.Trees.Count > 0 ? Pointer(trees) : 0);
        U16(block + 0x78, tree.Trees.Count);
        U16(block + 0x7A, tree.Trees.Count);
        return block;
    }

    // For each triangle edge (v1-v2, v2-v3, v3-v1): the other triangle sharing it, or 0xFFFF.
    private static int[] TriangleNeighbours(List<XmlPolygon> polygons)
    {
        var result = new int[polygons.Count * 3];
        Array.Fill(result, 0xFFFF);
        var edges = new Dictionary<(int, int), (int Polygon, int Edge, int Count)>();
        for (int i = 0; i < polygons.Count; i++)
        {
            var p = polygons[i];
            if (p.Kind != XmlPolygon.Triangle)
                continue;
            int[] v = [p.V1, p.V2, p.V3];
            for (int e = 0; e < 3; e++)
            {
                int a = v[e], b = v[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var first))
                {
                    edges[key] = (i, e, 1);
                    continue;
                }
                // Only the first two triangles on an edge are linked, as in the game's files.
                if (first.Count == 1)
                {
                    result[first.Polygon * 3 + first.Edge] = i;
                    result[i * 3 + e] = first.Polygon;
                    edges[key] = first with { Count = 2 };
                }
            }
        }
        return result;
    }

    private static (Vector3 Min, Vector3 Max) PolygonBox(XmlPolygon p, List<Vector3> vertices, Vector3 center)
    {
        Vector3 V(int i) => vertices[i] + center;
        var points = new List<Vector3>();
        float radius = 0;
        switch (p.Kind)
        {
            case XmlPolygon.Triangle:
                points.AddRange([V(p.V1), V(p.V2), V(p.V3)]);
                break;
            case XmlPolygon.Sphere:
                points.Add(V(p.V1));
                radius = p.Radius;
                break;
            case XmlPolygon.Capsule:
            case XmlPolygon.Cylinder:
                points.AddRange([V(p.V1), V(p.V2)]);
                radius = p.Radius;
                break;
            case XmlPolygon.Box:
                var corners = new[] { V(p.V1), V(p.V2), V(p.V3), V(p.V4) };
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
        return (min - new Vector3(radius), max + new Vector3(radius));
    }

    private static (Vector3 Min, Vector3 Max) TransformBox(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        if (m.IsIdentity)
            return (min, max);
        var tMin = new Vector3(float.MaxValue);
        var tMax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            var t = Vector3.Transform(corner, m);
            tMin = Vector3.Min(tMin, t);
            tMax = Vector3.Max(tMax, t);
        }
        return (tMin, tMax);
    }
}
