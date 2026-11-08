using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Collisions;

namespace NodeRunner.Data.Native.Collisions;

// Moving polygons of a collision file, in place: the vertices are moved in floats (exactly as CollisionMove.MoveVertices
// does on the mesh read), then each changed geometry is quantized again around its new box (centre in the middle,
// step = half size / 32767, as in the game's files); boxes are updated and search trees rebuilt by Remove.
internal sealed partial class BoundsFile
{
    private const int ShrunkVerticesPointer = 0x78;
    private const int ShrunkVerticesCount = 0x84;
    private const int BoxCenterOffset = 0x40;
    private const int BvhBoxMin = 0x20;
    private const int BvhBoxMax = 0x30;
    private const int BvhQuantum = 0x60;
    private const float QuantizedRange = 32767f;

    // Geometries whose search tree must be rebuilt even if no polygon is removed.
    private readonly HashSet<int> _rebuild = [];

    private sealed class Part
    {
        public int Offset;
        public Matrix4x4 Placement;
        public int FirstPolygon;
        public int PolygonCount;
        public int Polygons;
        public int Vertices;
        public int Shrunk = -1;
        public Vector3[] Points = [];
        public Vector3[]? ShrunkPoints;
        public bool Changed;
    }

    public void Move(IReadOnlyList<CollisionMove> moves)
    {
        if (moves.Count == 0)
            return;
        var parts = new List<Part>();
        var composites = new List<int>();
        int polygon = 0;
        Collect(0, Matrix4x4.Identity, parts, composites, ref polygon);

        foreach (var move in moves)
        {
            var moved = move.Polygons.ToHashSet();
            var places = new HashSet<Vector3>();
            foreach (var part in parts)
            {
                for (int i = 0; i < part.PolygonCount; i++)
                {
                    if (!moved.Contains(part.FirstPolygon + i))
                        continue;
                    foreach (int v in VertexIndices(part.Polygons + i * 16))
                    {
                        if (v >= 0 && v < part.Points.Length)
                            places.Add(part.Points[v]);
                    }
                }
            }
            if (places.Count == 0)
                continue;
            var m = move.Transform;
            foreach (var part in parts)
            {
                for (int v = 0; v < part.Points.Length; v++)
                {
                    if (!places.Contains(part.Points[v]))
                        continue;
                    part.Points[v] = Vector3.Transform(part.Points[v], m);
                    if (part.ShrunkPoints is { } shrunk)
                        shrunk[v] = Vector3.Transform(shrunk[v], m);
                    part.Changed = true;
                }
            }
        }

        var changed = new HashSet<int>();
        foreach (var part in parts.Where(p => p.Changed))
        {
            WritePart(part);
            changed.Add(part.Offset);
        }
        // Composites after their children (they were listed parents first).
        for (int i = composites.Count - 1; i >= 0; i--)
        {
            if (UpdateComposite(composites[i], changed))
                changed.Add(composites[i]);
        }
    }

    private void Collect(int o, Matrix4x4 placement, List<Part> parts, List<int> composites, ref int polygon)
    {
        switch (U8(o + TypeOffset))
        {
            case Composite:
                composites.Add(o);
                int count = U16(o + ChildrenCount1);
                int children = Resolve(U64(o + ChildrenPointer), count, 8);
                int matrices = Resolve(TransformsOf(o), count, 64);
                for (int i = 0; i < count; i++)
                {
                    int child = Resolve(U64(children + i * 8), 1, 0x70);
                    if (child >= 0)
                        Collect(child, matrices >= 0 ? Transform(matrices + i * 64) : Matrix4x4.Identity, parts, composites, ref polygon);
                }
                break;

            case Geometry:
            case GeometryBvh:
                int polygonCount = (int)U32(o + PolygonsCount);
                int vertexCount = (int)U32(o + VerticesCount);
                var part = new Part
                {
                    Offset = o,
                    Placement = placement,
                    FirstPolygon = polygon,
                    PolygonCount = polygonCount,
                    Polygons = Resolve(U64(o + PolygonsPointer), polygonCount, 16),
                    Vertices = Resolve(U64(o + VerticesPointer), vertexCount, 6),
                };
                polygon += polygonCount;
                if (part.Polygons < 0 || part.Vertices < 0)
                {
                    part.PolygonCount = 0;
                    break;
                }
                var quantum = Vec3(o + QuantumOffset);
                var center = Vec3(o + CenterOffset);
                // Same computation as the reading, so that the points are equal to the mesh's to the bit.
                part.Points = new Vector3[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    part.Points[v] = Vector3.Transform(Vertex(part.Vertices, v) * quantum + center, placement);
                if (U64(o + ShrunkVerticesPointer) != 0 && U32(o + ShrunkVerticesCount) == vertexCount)
                {
                    part.Shrunk = Resolve(U64(o + ShrunkVerticesPointer), vertexCount, 6);
                    part.ShrunkPoints = new Vector3[vertexCount];
                    for (int v = 0; v < vertexCount; v++)
                        part.ShrunkPoints[v] = Vector3.Transform(Vertex(part.Shrunk, v) * quantum + center, placement);
                }
                parts.Add(part);
                break;
        }
    }

    private ulong TransformsOf(int composite)
    {
        ulong transforms = U64(composite + Transforms1Pointer);
        return transforms != 0 ? transforms : U64(composite + Transforms2Pointer);
    }

    private IEnumerable<int> VertexIndices(int p)
    {
        switch (U8(p) & 7)
        {
            case 0:
                return [U16(p + 4) & 0x7FFF, U16(p + 6) & 0x7FFF, U16(p + 8) & 0x7FFF];
            case 1:
                return [U16(p + 2)];
            case 2:
            case 4:
                return [U16(p + 2), U16(p + 8)];
            case 3:
                return [I16(p + 4), I16(p + 6), I16(p + 8), I16(p + 10)];
            default:
                return [];
        }
    }

    private void WritePart(Part part)
    {
        int o = part.Offset;
        if (!Matrix4x4.Invert(part.Placement, out var toLocal))
            throw new InvalidDataException("Placement d'un morceau de collision non inversible.");
        var local = part.Points.Select(p => Vector3.Transform(p, toLocal)).ToArray();
        var shrunk = part.ShrunkPoints?.Select(p => Vector3.Transform(p, toLocal)).ToArray();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in shrunk is null ? local : local.Concat(shrunk))
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (local.Length == 0)
            return;
        var center = (min + max) / 2;
        var quantum = Vector3.Max((max - min) / 2 / QuantizedRange, new Vector3(1e-6f));
        WriteVec3(o + QuantumOffset, quantum);
        WriteVec3(o + CenterOffset, center);
        WriteVertices(part.Vertices, local, center, quantum);
        if (shrunk is not null)
            WriteVertices(part.Shrunk, shrunk, center, quantum);

        // Box of the geometry: its vertices and the size of its round polygons.
        var boxMin = new Vector3(float.MaxValue);
        var boxMax = new Vector3(float.MinValue);
        for (int i = 0; i < part.PolygonCount; i++)
        {
            var (pMin, pMax) = PolygonBox(o, part.Polygons + i * 16);
            boxMin = Vector3.Min(boxMin, pMin);
            boxMax = Vector3.Max(boxMax, pMax);
        }
        if (part.PolygonCount == 0)
            (boxMin, boxMax) = (min, max);
        WriteBox(o, boxMin, boxMax);

        if (U8(o + TypeOffset) == GeometryBvh && Resolve(U64(o + GeometryBvhPointer), 1, 128) is var bvh and >= 0)
        {
            WriteTreeFrame(bvh, boxMin, boxMax);
            _rebuild.Add(o);
        }
    }

    private void WriteVertices(int at, Vector3[] points, Vector3 center, Vector3 quantum)
    {
        for (int v = 0; v < points.Length; v++)
        {
            var q = Vector3.Clamp((points[v] - center) / quantum, new Vector3(-QuantizedRange), new Vector3(QuantizedRange));
            W16(at + v * 6, (short)MathF.Round(q.X));
            W16(at + v * 6 + 2, (short)MathF.Round(q.Y));
            W16(at + v * 6 + 4, (short)MathF.Round(q.Z));
        }
    }

    // Box, centre and sphere of a bounds structure (the 4th float of each is kept: margin, material...).
    private void WriteBox(int o, Vector3 min, Vector3 max)
    {
        var center = (min + max) / 2;
        WriteVec3(o + BoxMaxOffset, max);
        WriteVec3(o + BoxMinOffset, min);
        WriteVec3(o + BoxCenterOffset, center);
        WriteVec3(o + SphereCenterOffset, center);
        WriteF32(o + SphereRadiusOffset, Vector3.Distance(min, max) / 2);
    }

    // Box and quantization of a search tree (its nodes are written again afterwards).
    private void WriteTreeFrame(int bvh, Vector3 min, Vector3 max)
    {
        var quantum = Vector3.Max((max - min) / 2 / QuantizedRange, new Vector3(1e-6f));
        WriteVec3(bvh + BvhBoxMin, min);
        WriteVec3(bvh + BvhBoxMax, max);
        WriteVec3(bvh + BvhCenter, (min + max) / 2);
        WriteVec3(bvh + BvhQuantumInverse, Vector3.One / quantum);
        WriteVec3(bvh + BvhQuantum, quantum);
    }

    // The boxes of the changed children, then the composite's own box and tree; false if no child changed.
    private bool UpdateComposite(int o, HashSet<int> changed)
    {
        int count = U16(o + ChildrenCount1);
        int children = Resolve(U64(o + ChildrenPointer), count, 8);
        int boxes = Resolve(U64(o + ChildBoxesPointer), count, 32);
        int matrices = Resolve(TransformsOf(o), count, 64);
        bool any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var items = new List<(Vector3 Min, Vector3 Max)>();
        for (int i = 0; i < count; i++)
        {
            int child = Resolve(U64(children + i * 8), 1, 0x70);
            Vector3 childMin, childMax;
            if (child >= 0 && changed.Contains(child))
            {
                var placement = matrices >= 0 ? Transform(matrices + i * 64) : Matrix4x4.Identity;
                (childMin, childMax) = Placed(Vec3(child + BoxMinOffset), Vec3(child + BoxMaxOffset), placement);
                if (boxes >= 0)
                {
                    WriteVec3(boxes + i * 32, childMin);
                    WriteVec3(boxes + i * 32 + 16, childMax);
                }
                any = true;
            }
            else if (boxes >= 0)
                (childMin, childMax) = (Vec3(boxes + i * 32), Vec3(boxes + i * 32 + 16));
            else
                continue;
            items.Add((childMin, childMax));
            min = Vector3.Min(min, childMin);
            max = Vector3.Max(max, childMax);
        }
        if (!any || items.Count == 0)
            return any;
        WriteBox(o, min, max);
        if (Resolve(U64(o + CompositeBvhPointer), 1, 128) is var bvh and >= 0 && items.Count == count)
        {
            WriteTreeFrame(bvh, min, max);
            WriteTree(bvh, items, threshold: 1, reorder: false);
        }
        return true;
    }

    private static (Vector3 Min, Vector3 Max) Placed(Vector3 min, Vector3 max, Matrix4x4 placement)
    {
        var resultMin = new Vector3(float.MaxValue);
        var resultMax = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y, (c & 4) == 0 ? min.Z : max.Z);
            var p = Vector3.Transform(corner, placement);
            resultMin = Vector3.Min(resultMin, p);
            resultMax = Vector3.Max(resultMax, p);
        }
        return (resultMin, resultMax);
    }

    private void WriteF32(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(At(o, 4), v);

    private void WriteVec3(int o, Vector3 v)
    {
        WriteF32(o, v.X);
        WriteF32(o + 4, v.Y);
        WriteF32(o + 8, v.Z);
    }
}
