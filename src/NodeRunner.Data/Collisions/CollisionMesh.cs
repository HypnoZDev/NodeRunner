using System.Numerics;

namespace NodeRunner.Data.Collisions;

// The collision of one file (.ybn) as plain triangles: what the game uses for physics (peds walking, vehicles
// driving, objects resting on the ground), simpler than the visible model.
public sealed class CollisionMesh
{
    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    public required IReadOnlyList<Vector3> Vertices { get; init; }

    public required IReadOnlyList<int> Indices { get; init; }

    public required IReadOnlyList<byte> Materials { get; init; }

    // Polygon of the file each triangle comes from, numbered in reading order across the file's geometries (a box
    // polygon gives several triangles), or -1 for a lone primitive.
    public IReadOnlyList<int> Polygons { get; init; } = [];

    public int TriangleCount => Indices.Count / 3;
}

public interface ICollisionReader
{
    // Reads a .ybn file of the game, or returns null if it does not exist or holds no collision.
    CollisionMesh? Read(string path);
}
