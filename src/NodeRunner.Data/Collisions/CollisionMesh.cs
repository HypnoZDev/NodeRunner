using System.Numerics;

namespace NodeRunner.Data.Collisions;

/// <summary>
/// The collision of one file (.ybn) as plain triangles: what the game uses for physics (peds walking, vehicles
/// driving, objects resting on the ground), simpler than the visible model. Boxes, spheres, capsules and cylinders
/// of the file are turned into triangles too.
/// Map collision files are in world coordinates; those of an interior are relative to the interior.
/// </summary>
public sealed class CollisionMesh
{
    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    public required IReadOnlyList<Vector3> Vertices { get; init; }

    /// <summary>Three indices into <see cref="Vertices"/> per triangle (both sides are solid).</summary>
    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>Material of each triangle: index in the game's material list (materials.dat), e.g. concrete, grass.</summary>
    public required IReadOnlyList<byte> Materials { get; init; }

    /// <summary>
    /// Polygon of the file each triangle comes from, numbered in reading order across the file's geometries (a box
    /// polygon gives several triangles), or -1 for a lone primitive. Used to remove polygons from the file.
    /// </summary>
    public IReadOnlyList<int> Polygons { get; init; } = [];

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>Decodes collision files (.ybn).</summary>
public interface ICollisionReader
{
    /// <summary>Reads a .ybn file of the game, or returns null if it does not exist or holds no collision.</summary>
    /// <exception cref="InvalidDataException">The file is damaged or in an unsupported format.</exception>
    CollisionMesh? Read(string path);
}
