using System.Numerics;

namespace NodeRunner.Data.Maps;

/// <summary>
/// A box occluder of a map file: the game does not draw what is hidden behind it. Turned around the vertical axis only;
/// <see cref="Index"/> is its position in the file's list.
/// </summary>
public sealed record OccluderBox(int Index, Vector3 Center, Vector3 Size, float Angle)
{
    /// <summary>The 8 corners in world positions (bit 0 = +X half, bit 1 = +Y, bit 2 = +Z, in the box's axes).</summary>
    public Vector3[] Corners()
    {
        var x = new Vector3(MathF.Cos(Angle), MathF.Sin(Angle), 0);
        var y = new Vector3(-x.Y, x.X, 0);
        var corners = new Vector3[8];
        for (int k = 0; k < 8; k++)
        {
            corners[k] = Center
                         + x * (((k & 1) != 0 ? 0.5f : -0.5f) * Size.X)
                         + y * (((k & 2) != 0 ? 0.5f : -0.5f) * Size.Y)
                         + Vector3.UnitZ * (((k & 4) != 0 ? 0.5f : -0.5f) * Size.Z);
        }
        return corners;
    }

    /// <summary>The 12 triangles of the box's faces, 3 corners each.</summary>
    public Vector3[] Triangles()
    {
        var c = Corners();
        int[] faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        return faces.Select(i => c[i]).ToArray();
    }
}

/// <summary>
/// An occluder mesh of a map file, in world positions: simplified walls and roofs of a whole block, merged together.
/// <see cref="Indices"/> holds 3 vertex numbers per triangle; <see cref="Index"/> is its position in the file's list.
/// </summary>
public sealed record OccluderModel(int Index, IReadOnlyList<Vector3> Vertices, IReadOnlyList<byte> Indices)
{
    public int TriangleCount => Indices.Count / 3;
}

/// <summary>The occluders of a map file (CMapData boxOccluders and occludeModels). The game keeps them in map files of their own (*_occl_*.ymap).</summary>
public sealed record MapOccluders(IReadOnlyList<OccluderBox> Boxes, IReadOnlyList<OccluderModel> Models)
{
    public static readonly MapOccluders Empty = new([], []);
    public bool IsEmpty => Boxes.Count == 0 && Models.Count == 0;
}

public interface IOccluderReader
{
    /// <summary>The occluders of the decompressed content of a .ymap.</summary>
    /// <exception cref="InvalidDataException">The data is not a map file.</exception>
    MapOccluders ReadOccluders(ReadOnlySpan<byte> map);
}

public interface IOccluderWriter
{
    /// <summary>
    /// The decompressed content of the .ymap <paramref name="map"/> without the given boxes (by <see cref="OccluderBox.Index"/>)
    /// and triangles (by <see cref="OccluderModel.Index"/>, then triangle number); a mesh left without triangles goes.
    /// Everything else is kept as it is.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a map file.</exception>
    MapFileContent RemoveOccluders(ReadOnlySpan<byte> map, IReadOnlySet<int> boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> triangles);
}
