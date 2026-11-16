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

    // The file with these triangles emptied.
    public static byte[] HideTriangles(ResourceHeader header, byte[] content, IEnumerable<(int IndicesAt, int Triangle)> triangles)
    {
        var data = (byte[])content.Clone();
        HideTriangles(data, triangles);
        return ResourceFile.Write(header, data);
    }
}
