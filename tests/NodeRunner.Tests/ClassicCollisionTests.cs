using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Classic;

namespace NodeRunner.Tests;

// Collision shapes of the first trilogy (".col"), written by hand.
public class ClassicCollisionTests
{
    [Fact]
    public void Version3_GivesFacesAndBoxes()
    {
        // Header (4 letters, size, name, bounds), counts and offsets, then a box, 3 vertices and a face.
        const int header = 8 + 24 + 40, counts = 36 + 12, box = 28, vertices = 3 * 6, face = 8;
        var data = new byte[header + counts + box + vertices + face];
        Encoding.ASCII.GetBytes("COL3").CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        Encoding.ASCII.GetBytes("crate").CopyTo(data, 8);
        int h = header;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(h + 2), 1); // boxes
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(h + 4), 1); // faces
        int boxAt = header + counts, vertexAt = boxAt + box, faceAt = vertexAt + vertices;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(h + 16), (uint)(boxAt - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(h + 24), (uint)(vertexAt - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(h + 28), (uint)(faceAt - 4));
        foreach (var (i, value) in new[] { (0, -1f), (1, -1f), (2, 0f), (3, 1f), (4, 1f), (5, 2f) })
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(boxAt + i * 4), value);
        data[boxAt + 24] = 4; // pavement
        short[] v = [0, 0, 0, 256, 0, 0, 0, 256, 0]; // 2 m in 128ths
        for (int i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(vertexAt + i * 2), v[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceAt + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceAt + 4), 2);
        data[faceAt + 6] = 9; // grass

        var (model, mesh) = Assert.Single(ClassicCollisions.ReadMeshes(data));

        Assert.Equal("crate", model);
        Assert.Equal(13, mesh.TriangleCount);
        Assert.Equal(new Vector3(-1, -1, 0), mesh.BoundsMin);
        Assert.Equal(new Vector3(2, 2, 2), mesh.BoundsMax);
        Assert.Equal(CollisionSurface.Road, ClassicCollisions.Surface(ClassicGame.SanAndreas, mesh.Materials[0]));
        Assert.Equal(CollisionSurface.Vegetation, ClassicCollisions.Surface(ClassicGame.SanAndreas, mesh.Materials[12]));
    }
}
