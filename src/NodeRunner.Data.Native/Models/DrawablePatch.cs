using System.Buffers.Binary;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Models;

// Changes to a model file that keep its layout: nothing is moved, so every pointer of the file stays valid.
public static class DrawablePatch
{
    // The file with these triangles emptied (their three corners on the same vertex: the game draws nothing). Triangles
    // are given by where their geometry's indices are in the data (DrawableReader.StoredGeometry) and their number.
    public static byte[] HideTriangles(ResourceHeader header, byte[] content, IEnumerable<(int IndicesAt, int Triangle)> triangles)
    {
        var data = (byte[])content.Clone();
        foreach (var (indicesAt, triangle) in triangles)
        {
            int at = indicesAt + triangle * 6;
            if (indicesAt < 0 || triangle < 0 || at + 6 > data.Length)
                throw new ArgumentOutOfRangeException(nameof(triangles));
            ushort first = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 2), first);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 4), first);
        }
        return ResourceFile.Write(header, data);
    }
}
