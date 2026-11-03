using System.Numerics;

namespace NodeRunner.Data.NavMeshes;

/// <summary>
/// The navigation mesh of one square of the map (one navmesh[X][Y].ynv file): the surfaces where peds can walk.
/// Polygons are stored as index ranges into <see cref="Indices"/>, which point into <see cref="Vertices"/>.
/// </summary>
public sealed class NavMeshTile
{
    public required int AreaId { get; init; }

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    /// <summary>World positions in metres.</summary>
    public required IReadOnlyList<Vector3> Vertices { get; init; }

    public required IReadOnlyList<ushort> Indices { get; init; }

    public required IReadOnlyList<NavMeshPolygon> Polygons { get; init; }

    /// <summary>Vehicle navmeshes (used by some AI vehicles) instead of the usual pedestrian ones.</summary>
    public bool IsVehicleMesh { get; init; }

    /// <summary>Vertex positions of a polygon, in order around its outline.</summary>
    public IEnumerable<Vector3> PolygonVertices(NavMeshPolygon polygon)
    {
        for (int i = 0; i < polygon.IndexCount; i++)
        {
            int index = polygon.FirstIndex + i;
            if (index < Indices.Count && Indices[index] < Vertices.Count)
                yield return Vertices[Indices[index]];
        }
    }
}

/// <summary>
/// A convex polygon of a navmesh. Flag meanings come from community research; unknown bits are kept raw.
/// </summary>
public readonly record struct NavMeshPolygon(int FirstIndex, int IndexCount, ushort Flags0, uint Flags1, uint Flags2)
{
    public bool IsPavement => (Flags0 & 0x0004) != 0;
    public bool IsSteepSlope => (Flags0 & 0x0040) != 0;
    public bool IsWater => (Flags0 & 0x0080) != 0;
    public bool IsInterior => (Flags1 & 0x0040) != 0;
    public bool IsRoad => (Flags1 & 0x0400) != 0;
    public bool IsTrainTrack => (Flags1 & 0x1000) != 0;
    public bool IsShallowWater => (Flags1 & 0x2000) != 0;

    /// <summary>Main surface kind, used for colours and layers.</summary>
    public NavMeshSurface Surface =>
        IsWater || IsShallowWater ? NavMeshSurface.Water
        : IsTrainTrack ? NavMeshSurface.TrainTrack
        : IsInterior ? NavMeshSurface.Interior
        : IsRoad ? NavMeshSurface.Road
        : IsPavement ? NavMeshSurface.Pavement
        : IsSteepSlope ? NavMeshSurface.SteepSlope
        : NavMeshSurface.Ground;
}

public enum NavMeshSurface
{
    Pavement,
    Road,
    Ground,
    SteepSlope,
    Interior,
    Water,
    TrainTrack,
}

/// <summary>Decodes a navmesh file (.ynv).</summary>
public interface INavMeshReader
{
    /// <summary>Parses the decompressed content of a .ynv file (as returned by IGameFileSystem.ReadFile).</summary>
    /// <exception cref="InvalidDataException">The data is not a valid navmesh.</exception>
    NavMeshTile Read(ReadOnlySpan<byte> data);
}
