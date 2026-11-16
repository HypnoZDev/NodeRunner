using System.Buffers.Binary;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Models;

// Changes to a model file that keep its layout: nothing is moved, so every pointer of the file stays valid.
public static class DrawablePatch
{
    // Empties triangles in the resource data (their three corners on the same vertex: the game draws nothing). Triangles
    // are given by where their geometry's indices are in the data (DrawableReader.StoredGeometry) and their number.
    // Returns what was there, to put it back (Restore).
    public static List<(int At, ushort Second, ushort Third)> HideTriangles(byte[] content, IEnumerable<(int IndicesAt, int Triangle)> triangles)
    {
        var before = new List<(int, ushort, ushort)>();
        foreach (var (indicesAt, triangle) in triangles)
        {
            int at = indicesAt + triangle * 6;
            if (indicesAt < 0 || triangle < 0 || at + 6 > content.Length)
                throw new ArgumentOutOfRangeException(nameof(triangles));
            var span = content.AsSpan(at);
            before.Add((at, BinaryPrimitives.ReadUInt16LittleEndian(span[2..]), BinaryPrimitives.ReadUInt16LittleEndian(span[4..])));
            ushort first = BinaryPrimitives.ReadUInt16LittleEndian(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], first);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], first);
        }
        return before;
    }

    public static void Restore(byte[] content, IEnumerable<(int At, ushort Second, ushort Third)> before)
    {
        foreach (var (at, second, third) in before)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(at + 2), second);
            BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(at + 4), third);
        }
    }

    // Moves and turns vertices of geometries (move in the model's space); the boxes and sphere holding them grow when
    // needed (never shrink). Returns the bytes changed, to put them back (Restore). Null when a geometry's vertices cannot
    // be written.
    public static List<(int At, byte[] Bytes)>? MoveVertices(byte[] content,
        IEnumerable<(DrawableReader.StoredGeometry Geometry, IReadOnlyCollection<int> Vertices)> parts, System.Numerics.Matrix4x4 move)
    {
        var list = parts.ToList();
        if (list.Any(p => p.Geometry.Vertices is null))
            return null;
        var before = new List<(int, byte[])>();
        var saved = new HashSet<int>();
        void Save(int at, int length)
        {
            if (at >= 0 && at + length <= content.Length && saved.Add(at))
                before.Add((at, content.AsSpan(at, length).ToArray()));
        }
        foreach (var (geometry, vertices) in list)
        {
            var stored = geometry.Vertices!;
            if (!System.Numerics.Matrix4x4.Invert(stored.Bone, out var fromModel))
                return null;
            var local = stored.Bone * move * fromModel;
            foreach (int at in new[] { stored.At, stored.SecondAt })
            {
                if (at < 0)
                    continue;
                var buffer = content.AsSpan(at, stored.Count * stored.Stride);
                foreach (int v in vertices)
                {
                    if (v < 0 || v >= stored.Count)
                        continue;
                    Save(at + v * stored.Stride, stored.Stride);
                    stored.Transform(buffer, v, local);
                }
            }
            var moved = vertices.Where(v => v >= 0 && v < geometry.Positions.Length)
                .Select(v => System.Numerics.Vector3.Transform(geometry.Positions[v], move)).ToList();
            foreach (int box in geometry.Boxes)
            {
                Save(box, 32);
                Grow(content, box, moved);
            }
            if (geometry.SphereAt >= 0)
            {
                Save(geometry.SphereAt, 16);
                GrowSphere(content, geometry.SphereAt, moved);
            }
        }
        return before;
    }

    public static void Restore(byte[] content, List<(int At, byte[] Bytes)> before)
    {
        for (int i = before.Count - 1; i >= 0; i--)
            before[i].Bytes.CopyTo(content, before[i].At);
    }

    private static System.Numerics.Vector3 Vector(byte[] content, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(at + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(at + 8)));

    private static void WriteVector(byte[] content, int at, System.Numerics.Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(at), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(at + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(at + 8), v.Z);
    }

    private static void Grow(byte[] content, int at, List<System.Numerics.Vector3> points)
    {
        if (at < 0 || at + 32 > content.Length || points.Count == 0)
            return;
        var min = Vector(content, at);
        var max = Vector(content, at + 16);
        foreach (var p in points)
        {
            min = System.Numerics.Vector3.Min(min, p);
            max = System.Numerics.Vector3.Max(max, p);
        }
        WriteVector(content, at, min);
        WriteVector(content, at + 16, max);
    }

    private static void GrowSphere(byte[] content, int at, List<System.Numerics.Vector3> points)
    {
        if (at < 0 || at + 16 > content.Length || points.Count == 0)
            return;
        var center = Vector(content, at);
        float radius = BinaryPrimitives.ReadSingleLittleEndian(content.AsSpan(at + 12));
        foreach (var p in points)
            radius = MathF.Max(radius, System.Numerics.Vector3.Distance(center, p));
        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(at + 12), radius);
    }

    // The file with these triangles emptied.
    public static byte[] HideTriangles(ResourceHeader header, byte[] content, IEnumerable<(int IndicesAt, int Triangle)> triangles)
    {
        var data = (byte[])content.Clone();
        HideTriangles(data, triangles);
        return ResourceFile.Write(header, data);
    }
}
