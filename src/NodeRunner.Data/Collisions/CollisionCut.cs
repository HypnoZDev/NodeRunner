using System.Numerics;

namespace NodeRunner.Data.Collisions;

// The space of a deleted building: its model's box, in the building's own axes.
public readonly record struct CutVolume(Matrix4x4 ToLocal, Vector3 Min, Vector3 Max, bool IncludesGround = false);

// Writes collision files (.ybn) back.
public interface ICollisionWriter
{
    // The game's file path without the given polygons (numbered as in Polygons), as a complete .ybn file ready to be
    // streamed; null if the file does not exist.
    byte[]? RemovePolygons(string path, IReadOnlySet<int> polygons);
}

// Chooses the collision polygons of deleted buildings.
public static class CollisionCut
{
    public const float Margin = 0.3f;

    public const float FloorGap = 1f;

    public const float GroundHeight = 1.5f;

    private const float FlatNormal = 0.8f;

    // Polygons of mesh (placed by meshToWorld) to remove for volumes. ground: flat surfaces of the area (all its
    // files of the same kind), to tell the ground from floors.
    public static HashSet<int> Select(CollisionMesh mesh, Matrix4x4 meshToWorld, IReadOnlyList<CutVolume> volumes, FlatSurfaces? ground = null)
    {
        var removed = new HashSet<int>();
        var kept = new HashSet<int>();
        if (volumes.Count == 0 || mesh.Polygons.Count != mesh.TriangleCount)
            return removed;

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            int polygon = mesh.Polygons[t];
            if (polygon < 0 || kept.Contains(polygon))
                continue;
            var a = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3]], meshToWorld);
            var b = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + 1]], meshToWorld);
            var c = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + 2]], meshToWorld);
            bool inside = false;
            foreach (var volume in volumes)
            {
                if (IsInside(volume, a, b, c, ground))
                {
                    inside = true;
                    break;
                }
            }
            // Every triangle of the polygon must go (a box polygon gives twelve).
            if (inside)
                removed.Add(polygon);
            else
            {
                kept.Add(polygon);
                removed.Remove(polygon);
            }
        }
        return removed;
    }

    public static bool IsInside(CutVolume volume, Vector3 a, Vector3 b, Vector3 c, FlatSurfaces? ground = null)
    {
        var la = Vector3.Transform(a, volume.ToLocal);
        var lb = Vector3.Transform(b, volume.ToLocal);
        var lc = Vector3.Transform(c, volume.ToLocal);
        var min = volume.Min - new Vector3(Margin);
        var max = volume.Max + new Vector3(Margin);
        if (!Within(la, min, max) || !Within(lb, min, max) || !Within(lc, min, max))
            return false;
        if (volume.IncludesGround || !IsFlat(a, b, c))
            return true;
        if (ground is not null)
        {
            // A floor only if something lies under all of it: a large triangle can overhang the edge of a lower area.
            var center = (a + b + c) / 3f;
            return ground.HasSurfaceBelow(center, FloorGap)
                && ground.HasSurfaceBelow(Vector3.Lerp(a, center, 0.05f), FloorGap)
                && ground.HasSurfaceBelow(Vector3.Lerp(b, center, 0.05f), FloorGap)
                && ground.HasSurfaceBelow(Vector3.Lerp(c, center, 0.05f), FloorGap);
        }
        float groundTop = volume.Min.Z + GroundHeight;
        return !(la.Z < groundTop && lb.Z < groundTop && lc.Z < groundTop);
    }

    public static bool IsFlat(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        float length = normal.Length();
        return length > 1e-6f && MathF.Abs(normal.Z / length) >= FlatNormal;
    }

    private static bool Within(Vector3 p, Vector3 min, Vector3 max) =>
        p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z;
}

// The flat collision surfaces of an area (world positions), sorted in cells to find what lies under a point.
public sealed class FlatSurfaces
{
    private const float CellSize = 4f;
    private readonly Dictionary<(int, int), List<int>> _cells = new();
    private readonly List<Vector3> _triangles = new();

    public int TriangleCount => _triangles.Count / 3;

    public void Add(CollisionMesh mesh, Matrix4x4 meshToWorld, Vector2 areaMin, Vector2 areaMax)
    {
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var a = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3]], meshToWorld);
            var b = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + 1]], meshToWorld);
            var c = Vector3.Transform(mesh.Vertices[mesh.Indices[t * 3 + 2]], meshToWorld);
            var min = Vector2.Min(Flat(a), Vector2.Min(Flat(b), Flat(c)));
            var max = Vector2.Max(Flat(a), Vector2.Max(Flat(b), Flat(c)));
            if (max.X < areaMin.X || max.Y < areaMin.Y || min.X > areaMax.X || min.Y > areaMax.Y || !CollisionCut.IsFlat(a, b, c))
                continue;
            int index = _triangles.Count / 3;
            _triangles.Add(a);
            _triangles.Add(b);
            _triangles.Add(c);
            for (int x = Cell(min.X); x <= Cell(max.X); x++)
            {
                for (int y = Cell(min.Y); y <= Cell(max.Y); y++)
                {
                    if (!_cells.TryGetValue((x, y), out var list))
                        _cells[(x, y)] = list = new List<int>();
                    list.Add(index);
                }
            }
        }
    }

    public bool HasSurfaceBelow(Vector3 point, float gap)
    {
        if (!_cells.TryGetValue((Cell(point.X), Cell(point.Y)), out var list))
            return false;
        foreach (int index in list)
        {
            if (HeightAt(_triangles[index * 3], _triangles[index * 3 + 1], _triangles[index * 3 + 2], Flat(point)) is { } z
                && z < point.Z - gap)
                return true;
        }
        return false;
    }

    private static float? HeightAt(Vector3 a, Vector3 b, Vector3 c, Vector2 p)
    {
        var v0 = Flat(b) - Flat(a);
        var v1 = Flat(c) - Flat(a);
        var v2 = p - Flat(a);
        float d = v0.X * v1.Y - v1.X * v0.Y;
        if (MathF.Abs(d) < 1e-8f)
            return null;
        float u = (v2.X * v1.Y - v1.X * v2.Y) / d;
        float v = (v0.X * v2.Y - v2.X * v0.Y) / d;
        if (u < 0 || v < 0 || u + v > 1)
            return null;
        return a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    private static int Cell(float value) => (int)MathF.Floor(value / CellSize);
}
