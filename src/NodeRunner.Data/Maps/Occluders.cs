using System.Numerics;

namespace NodeRunner.Data.Maps;

// A box occluder of a map file: the game does not draw what is hidden behind it. Turned around the vertical axis
// only; Index is its position in the file's list.
public sealed record OccluderBox(int Index, Vector3 Center, Vector3 Size, float Angle)
{
    // Units of the game's files: centre and size in quarters of a metre, sine and cosine of the angle times 16384.
    public const float PositionUnit = 0.25f;
    public const float AngleUnit = 16384f;

    // The box as the game's file stores it.
    public OccluderBox Quantized()
    {
        static float Round(float v) => MathF.Round(v / PositionUnit) * PositionUnit;
        static Vector3 RoundVector(Vector3 v) => new(Round(v.X), Round(v.Y), Round(v.Z));
        float angle = MathF.Atan2(MathF.Round(MathF.Sin(Angle) * AngleUnit), MathF.Round(MathF.Cos(Angle) * AngleUnit));
        return this with { Center = RoundVector(Center), Size = Vector3.Max(RoundVector(Size), new Vector3(PositionUnit)), Angle = angle };
    }

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

    public Vector3[] Triangles()
    {
        var c = Corners();
        int[] faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        return faces.Select(i => c[i]).ToArray();
    }
}

// An occluder mesh of a map file, in world positions: simplified walls and roofs of a whole block, merged together.
public sealed record OccluderModel(int Index, IReadOnlyList<Vector3> Vertices, IReadOnlyList<byte> Indices)
{
    public int TriangleCount => Indices.Count / 3;
}

// The occluders of a map file (CMapData boxOccluders and occludeModels). The game keeps them in map files of their
// own (*_occl_*.ymap).
public sealed record MapOccluders(IReadOnlyList<OccluderBox> Boxes, IReadOnlyList<OccluderModel> Models)
{
    public static readonly MapOccluders Empty = new([], []);
    public bool IsEmpty => Boxes.Count == 0 && Models.Count == 0;
}

public interface IOccluderReader
{
    // The occluders of the decompressed content of a .ymap.
    MapOccluders ReadOccluders(ReadOnlySpan<byte> map);
}

public interface IOccluderWriter
{
    // The decompressed content of the .ymap map without the given boxes (by Index) and triangles (by Index, then
    // triangle number); a mesh left without triangles goes.
    MapFileContent RemoveOccluders(ReadOnlySpan<byte> map, IReadOnlySet<int> boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> triangles);

    // The decompressed content of the .ymap map with these boxes in place of its own and without the given mesh
    // triangles; its extents grow to hold every occluder left (never shrink).
    MapFileContent WriteOccluders(ReadOnlySpan<byte> map, IReadOnlyList<OccluderBox> boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> triangles);

    // A new occluder map file holding only these boxes; template is a game occluder map file with boxes (descriptions
    // of the structures).
    MapFileContent BuildOccluders(ReadOnlySpan<byte> template, string name, IReadOnlyList<OccluderBox> boxes);
}
