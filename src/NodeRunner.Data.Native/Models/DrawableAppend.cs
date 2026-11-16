using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Models;

// Adds vertices and triangles to geometries of a model file (original release). Nothing of the file moves: the grown
// vertex and index buffers go after the system pages (where the game's own files often keep their vertices), with a new
// page list, and only the counts and pointers of the geometries change. The system pages are laid out again in pages of
// one size, at least as big as the old biggest one: no old block can cross a page end.
public static class DrawableAppend
{
    private const int MinPageSize = 0x2000;
    private const int MaxUnitSize = 0x200 << 15;
    private const int MaxUnits = 0x7FF;
    private const ulong SystemBase = 0x50000000;

    // Length of the system pages of a file (the systemKept of Apply for a file as read).
    public static int SystemLength(ResourceHeader header) => ResourcePages.Size(header.SystemFlags);

    // Additions to one geometry: whole vertices (its stride each) and triangles numbered in these new vertices.
    public sealed record Addition(DrawableReader.StoredGeometry Geometry, byte[] Vertices, ushort[] Indices);

    // The vertices and triangles of these triangles of a geometry, ready to add again (a copy in the same place).
    public static Addition Copy(ReadOnlySpan<byte> content, DrawableReader.StoredGeometry geometry, IEnumerable<int> triangles)
    {
        var map = new Dictionary<int, ushort>();
        var vertices = new List<byte>();
        var indices = new List<ushort>();
        foreach (int t in triangles)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = geometry.Indices[t * 3 + k];
                if (!map.TryGetValue(v, out ushort copy))
                {
                    map[v] = copy = (ushort)map.Count;
                    vertices.AddRange(content.Slice(geometry.VertexAt + v * geometry.Stride, geometry.Stride));
                }
                indices.Add(copy);
            }
        }
        return new Addition(geometry, [.. vertices], [.. indices]);
    }

    // Simple shapes (model space), around a centre. Faces turn outwards (corners anticlockwise seen from the front, as in
    // the game's files); the texture is upright on the sides, once per face or once per metre.
    public static (List<DrawableReader.VertexValues> Vertices, ushort[] Indices) Cube(Vector3 center, Vector3 size, bool perMetre) =>
        Faces(center, size, perMetre,
        [
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ),
            (Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY),
        ]);

    // A flat rectangle facing up (size X by Y).
    public static (List<DrawableReader.VertexValues> Vertices, ushort[] Indices) Plane(Vector3 center, Vector2 size, bool perMetre) =>
        Faces(center, new Vector3(size, 0f), perMetre, [(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY)]);

    // Each face: its normal, its texture's U direction and V direction (upwards in the image), cross(u, v) = normal.
    private static (List<DrawableReader.VertexValues>, ushort[]) Faces(Vector3 center, Vector3 size, bool perMetre,
        (Vector3 N, Vector3 U, Vector3 V)[] faces)
    {
        var half = size * 0.5f;
        float Extent(Vector3 axis) => MathF.Abs(Vector3.Dot(axis, half));
        var vertices = new List<DrawableReader.VertexValues>();
        var indices = new List<ushort>();
        foreach (var (n, u, v) in faces)
        {
            float hu = Extent(u), hv = Extent(v);
            var repeat = perMetre ? new Vector2(Math.Max(2 * hu, 0.01f), Math.Max(2 * hv, 0.01f)) : Vector2.One;
            int first = vertices.Count;
            foreach (var (s, t) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var position = center + n * Extent(n) + u * (s * hu) + v * (t * hv);
                var uv = new Vector2((s + 1) * 0.5f * repeat.X, (1 - t) * 0.5f * repeat.Y);
                vertices.Add(new DrawableReader.VertexValues(position, n, uv, Vector4.One, new Vector4(u, 1f)));
            }
            indices.AddRange([(ushort)first, (ushort)(first + 1), (ushort)(first + 2), (ushort)first, (ushort)(first + 2), (ushort)(first + 3)]);
        }
        return (vertices, [.. indices]);
    }

    // New vertices and triangles for a geometry (values in model space), made from its first vertex. Null when the
    // geometry's vertices cannot be written.
    public static Addition? Make(ReadOnlySpan<byte> content, DrawableReader.StoredGeometry geometry,
        IReadOnlyList<DrawableReader.VertexValues> vertices, ushort[] indices)
    {
        if (geometry.Vertices is not { } stored || geometry.VertexAt < 0 || geometry.VertexCount == 0)
            return null;
        return stored.Make(content.Slice(geometry.VertexAt, geometry.Stride), vertices) is { } bytes
            ? new Addition(geometry, bytes, indices)
            : null;
    }

    // The model data with these additions. systemKept: length of the system pages kept as they are (those of the file as
    // first read); a geometry whose buffers are after it (added earlier) is written again, so repeated additions do not
    // pile up old copies. all: every geometry of the drawable. Throws InvalidDataException with a message when the file
    // cannot take them.
    public static (ResourceHeader Header, byte[] Content) Apply(ResourceHeader header, byte[] content, int systemKept,
        IReadOnlyList<DrawableReader.StoredGeometry> all, IReadOnlyList<Addition> additions)
    {
        if (header.Version is DrawableReader.EnhancedDrawableVersion or DrawableReader.EnhancedFragmentVersion)
            throw new InvalidDataException("Les modèles de GTA V Enhanced ne peuvent pas encore être agrandis.");
        int systemLength = ResourcePages.Size(header.SystemFlags);
        int graphicsLength = ResourcePages.Size(header.GraphicsFlags);
        int largestOld = LargestPage(header.SystemFlags);
        if (systemLength + graphicsLength > content.Length || systemKept > systemLength || systemKept < 16
            || largestOld > MaxUnitSize)
            throw new InvalidDataException("Pages du fichier inhabituelles : le modèle ne peut pas être agrandi.");

        var byGeometry = additions.GroupBy(a => a.Geometry.GeometryAt).ToDictionary(g => g.Key, g => g.ToList());
        bool Added(int at) => at >= systemKept && at < systemLength;
        var rewritten = all.Where(g => byGeometry.ContainsKey(g.GeometryAt) || Added(g.VertexAt) || Added(g.IndicesAt))
            .GroupBy(g => g.GeometryAt).Select(g => g.First()).ToList();
        foreach (var g in rewritten)
        {
            if (g.GeometryAt < 0 || g.VertexBufferAt < 0 || g.IndexBufferAt < 0 || g.VertexAt < 0 || g.Stride <= 0)
                throw new InvalidDataException("Une partie du modèle n'a pas pu être lue en entier.");
            // A buffer used by another geometry would change it too.
            if (all.Any(o => o.GeometryAt != g.GeometryAt && (o.VertexBufferAt == g.VertexBufferAt || o.IndexBufferAt == g.IndexBufferAt)))
                throw new InvalidDataException("Ce modèle partage ses sommets entre plusieurs parties : il ne peut pas être agrandi.");
        }

        // The new buffers of each geometry: its vertices then the added ones, its indices then the added ones.
        var blocks = new List<(DrawableReader.StoredGeometry Geometry, byte[] Vertices, byte[] Indices, int VertexCount, int IndexCount)>();
        foreach (var g in rewritten)
        {
            int indexCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(g.IndexBufferAt + 0x08));
            var vertices = new List<byte>(content.AsSpan(g.VertexAt, g.VertexCount * g.Stride).ToArray());
            var indices = new List<ushort>(indexCount);
            for (int i = 0; i < indexCount; i++)
                indices.Add(BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(g.IndicesAt + 2 * i)));
            int vertexCount = g.VertexCount;
            foreach (var a in byGeometry.GetValueOrDefault(g.GeometryAt) ?? [])
            {
                if (a.Vertices.Length % g.Stride != 0 || a.Indices.Length % 3 != 0)
                    throw new InvalidDataException("Ajout mal formé.");
                indices.AddRange(a.Indices.Select(i => (ushort)(i + vertexCount)));
                vertexCount += a.Vertices.Length / g.Stride;
                vertices.AddRange(a.Vertices);
            }
            if (vertexCount > ushort.MaxValue)
                throw new InvalidDataException($"Trop de sommets dans une partie du modèle ({vertexCount} ; 65 535 au plus).");
            var indexBytes = new byte[indices.Count * 2];
            for (int i = 0; i < indices.Count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(indexBytes.AsSpan(2 * i), indices[i]);
            blocks.Add((g, [.. vertices], indexBytes, vertexCount, indices.Count));
        }

        // Layout after the kept pages: the page list, then the buffers. The page list's size depends on the number of
        // pages: it only grows, so the passes end.
        int graphicsChunks = Chunks(header.GraphicsFlags);
        int pageSize = Math.Max(MinPageSize, largestOld);
        int largest = blocks.Count == 0 ? 0 : blocks.Max(b => Math.Max(b.Vertices.Length, b.Indices.Length));
        while (pageSize < largest + 16)
            pageSize *= 2;
        int mapLength = 16 + 8 * (Chunks(header.SystemFlags) + graphicsChunks);
        int[] positions;
        int pageCount, unitSize, units;
        while (true)
        {
            var lengths = new List<int> { mapLength };
            foreach (var b in blocks)
            {
                lengths.Add(b.Vertices.Length);
                lengths.Add(b.Indices.Length);
            }
            positions = new int[lengths.Count];
            int at = systemKept;
            for (int i = 0; i < lengths.Count; i++)
            {
                at = (at + 15) & ~15;
                if (at / pageSize != (at + lengths[i] - 1) / pageSize)
                    at = (at / pageSize + 1) * pageSize;
                positions[i] = at;
                at += lengths[i];
            }
            pageCount = (at + pageSize - 1) / pageSize;
            unitSize = Math.Min(pageSize, MaxUnitSize);
            units = pageCount * (pageSize / unitSize);
            if (units > MaxUnits)
            {
                pageSize *= 2;
                if (pageSize > MaxUnitSize * 16)
                    throw new InvalidDataException("Modèle trop grand pour être agrandi.");
                continue;
            }
            int needed = 16 + 8 * (ChunkCount(units) + graphicsChunks);
            if (needed <= mapLength)
                break;
            mapLength = needed;
        }

        int newSystem = pageCount * pageSize;
        var result = new byte[newSystem + graphicsLength];
        content.AsSpan(0, systemKept).CopyTo(result);
        content.AsSpan(systemLength, graphicsLength).CopyTo(result.AsSpan(newSystem));
        var span = result.AsSpan();

        // The page list: counts of the system and graphics pages, entries filled by the game.
        int map = positions[0];
        result[map + 8] = (byte)ChunkCount(units);
        result[map + 9] = (byte)graphicsChunks;
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x08..], SystemBase + (ulong)map);

        for (int i = 0; i < blocks.Count; i++)
        {
            var (g, vertices, indices, vertexCount, indexCount) = blocks[i];
            int vertexAt = positions[1 + 2 * i], indexAt = positions[2 + 2 * i];
            vertices.CopyTo(result, vertexAt);
            indices.CopyTo(result, indexAt);
            ulong vertexAddress = SystemBase + (ulong)vertexAt;
            ulong indexAddress = SystemBase + (ulong)indexAt;
            // Vertex buffer: 0x10 data, 0x18 count, 0x20 second copy of the data (same data from now on).
            BinaryPrimitives.WriteUInt64LittleEndian(span[(g.VertexBufferAt + 0x10)..], vertexAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(g.VertexBufferAt + 0x18)..], (uint)vertexCount);
            if (BinaryPrimitives.ReadUInt64LittleEndian(span[(g.VertexBufferAt + 0x20)..]) != 0)
                BinaryPrimitives.WriteUInt64LittleEndian(span[(g.VertexBufferAt + 0x20)..], vertexAddress);
            // Index buffer: 0x08 count, 0x10 data.
            BinaryPrimitives.WriteUInt32LittleEndian(span[(g.IndexBufferAt + 0x08)..], (uint)indexCount);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(g.IndexBufferAt + 0x10)..], indexAddress);
            // Geometry: 0x58 index count, 0x5C triangle count, 0x60 vertex count, 0x78 vertex data.
            BinaryPrimitives.WriteUInt32LittleEndian(span[(g.GeometryAt + 0x58)..], (uint)indexCount);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(g.GeometryAt + 0x5C)..], (uint)(indexCount / 3));
            BinaryPrimitives.WriteUInt16LittleEndian(span[(g.GeometryAt + 0x60)..], (ushort)vertexCount);
            if (BinaryPrimitives.ReadUInt64LittleEndian(span[(g.GeometryAt + 0x78)..]) != 0)
                BinaryPrimitives.WriteUInt64LittleEndian(span[(g.GeometryAt + 0x78)..], vertexAddress);
        }

        uint flags = (header.SystemFlags & 0xF0000000) | MetaDocument.PageFlags(units, unitSize);
        return (new ResourceHeader(header.Version, flags, header.GraphicsFlags), result);
    }

    // Grows the boxes and sphere holding a geometry to hold these points (model space), as DrawablePatch.MoveVertices.
    public static void Grow(byte[] content, DrawableReader.StoredGeometry geometry, IReadOnlyCollection<Vector3> points)
    {
        if (points.Count == 0)
            return;
        foreach (int box in geometry.Boxes)
        {
            if (box < 0 || box + 32 > content.Length)
                continue;
            var min = Read(content, box);
            var max = Read(content, box + 16);
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            Write(content, box, min);
            Write(content, box + 16, max);
        }
        if (geometry.SphereAt >= 0 && geometry.SphereAt + 16 <= content.Length)
        {
            var center = Read(content, geometry.SphereAt);
            float radius = BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(geometry.SphereAt + 12));
            foreach (var p in points)
                radius = MathF.Max(radius, Vector3.Distance(center, p));
            BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(geometry.SphereAt + 12), radius);
        }
    }

    private static Vector3 Read(byte[] c, int at) => new(BinaryPrimitives.ReadSingleLittleEndian(c.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(c.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(c.AsSpan(at + 8)));

    private static void Write(byte[] c, int at, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(at + 8), v.Z);
    }

    // Page counts of a flag word, biggest first: 256, 128, 64, 32, 16, 8, 4, 2 and 1 times the base size.
    private static readonly (int Shift, uint Mask, int Units)[] Classes =
        [(4, 0x1, 256), (5, 0x3, 128), (7, 0xF, 64), (11, 0x3F, 32), (17, 0x7F, 16), (24, 1, 8), (25, 1, 4), (26, 1, 2), (27, 1, 1)];

    private static int LargestPage(uint flags)
    {
        long baseSize = 0x200L << (int)(flags & 0xF);
        foreach (var (shift, mask, units) in Classes)
        {
            if (((flags >> shift) & mask) != 0)
                return (int)Math.Min(baseSize * units, int.MaxValue);
        }
        return 0;
    }

    private static int Chunks(uint flags) => Classes.Sum(c => (int)((flags >> c.Shift) & c.Mask));

    // Pages written for this many units (MetaDocument.PageFlags: groups of 16, then 8, 4, 2, 1).
    private static int ChunkCount(int units) => (units >> 4) + BitOperations.PopCount((uint)units & 0xF);
}
