using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.NavMeshes;

namespace NodeRunner.Editor;

// The navmesh (where peds walk) is baked by the game from the map's collision and is not rebuilt by an export: an added
// building stands on walkable ground peds go through, a deleted one leaves walkable floors in the air.
public static class NavMeshCheck
{
    // An added object smaller than this (width or height) does not block anyone.
    public const float MinWidth = 2f;
    public const float MinHeight = 1.5f;

    // Walkable area an added object must cross to be reported. Measured on the game's own buildings (navmesh baked
    // around them), 3 % cross this much (bridges, tunnels, overhangs), against 78 % of those put elsewhere on it.
    public const float MinCrossedArea = 10f;

    // A polygon of a deleted building this high above the ground left is in the air.
    public const float FloatingGap = 1.5f;

    // Props are avoided by peds while the game runs (archetype flag 0x20000000, breakable models); buildings are not.
    public static bool BlocksPeds(Archetype? archetype)
    {
        if (archetype is null || (archetype.Flags & 0x20000000) != 0 || archetype.AssetType == ArchetypeAssetType.Fragment)
            return false;
        if (GroundSnap.LocalBox(archetype) is not var (min, max))
            return false;
        var size = max - min;
        return MathF.Max(size.X, size.Y) >= MinWidth && size.Z >= MinHeight;
    }

    // Tiles a world box spreads over.
    public static IEnumerable<NavMeshTileKey> Tiles(Vector3 min, Vector3 max)
    {
        var first = NavMeshCatalog.TileAt(new Vector2(min.X, min.Y));
        var last = NavMeshCatalog.TileAt(new Vector2(max.X, max.Y));
        for (int x = first.X; x <= last.X; x += 3)
        {
            for (int y = first.Y; y <= last.Y; y += 3)
                yield return new NavMeshTileKey(x, y);
        }
    }

    // Polygons walked through the object: from a point of the polygon (centre, corners pulled inwards), the object's
    // surface is met less than a ped's height above. Ground pieces under the navmesh are never met (they are below),
    // the game's own buildings neither (their navmesh goes around them). triangles: the object's, in world positions.
    // Returns the walkable area crossed, in square metres.
    public static float CrossedArea(IEnumerable<NavMeshTile> tiles, Matrix4x4 transform, Vector3 min, Vector3 max,
        IReadOnlyList<Vector3> triangles)
    {
        var grid = new TriangleGrid(triangles);
        float area = 0;
        foreach (var (centre, points) in Polygons(tiles, transform, min, max))
        {
            foreach (var corner in points)
            {
                var p = corner == centre ? centre : Vector3.Lerp(corner, centre, 0.3f);
                if (grid.LowestAbove(p, p.Z + StartAbove, p.Z + PedHeight) is not null)
                {
                    area += Area(points);
                    break;
                }
            }
        }
        return area;
    }

    // The vertical ray starts this high above the navmesh (it lies a little above or below the real ground).
    private const float StartAbove = 0.3f;

    public const float PedHeight = 1.8f;

    // Centres of the polygons with their centre inside the box (deleted building), highest first.
    public static List<Vector3> CentresInside(IEnumerable<NavMeshTile> tiles, Matrix4x4 transform, Vector3 min, Vector3 max)
    {
        Matrix4x4.Invert(transform, out var toLocal);
        var centres = new List<Vector3>();
        foreach (var (centre, _) in Polygons(tiles, transform, min, max))
        {
            var p = Vector3.Transform(centre, toLocal);
            if (p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z + 1f)
                centres.Add(centre);
        }
        centres.Sort((a, b) => b.Z.CompareTo(a.Z));
        return centres;
    }

    // Polygons whose bounds meet the object's world box.
    private static IEnumerable<(Vector3 Centre, List<Vector3> Points)> Polygons(IEnumerable<NavMeshTile> tiles, Matrix4x4 transform,
        Vector3 min, Vector3 max)
    {
        var (worldMin, worldMax) = WorldBox(transform, min, max);
        foreach (var tile in tiles)
        {
            foreach (var polygon in tile.Polygons)
            {
                var low = new Vector3(float.MaxValue);
                var high = new Vector3(float.MinValue);
                int n = 0;
                foreach (var v in tile.PolygonVertices(polygon))
                {
                    low = Vector3.Min(low, v);
                    high = Vector3.Max(high, v);
                    n++;
                }
                if (n == 0 || high.X < worldMin.X || low.X > worldMax.X || high.Y < worldMin.Y || low.Y > worldMax.Y
                    || high.Z < worldMin.Z - 2f || low.Z > worldMax.Z + 2f)
                    continue;
                var points = tile.PolygonVertices(polygon).ToList();
                var centre = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Count;
                points.Add(centre);
                yield return (centre, points);
            }
        }
    }

    // Horizontal area of a convex polygon (its corners in order, the centre last).
    private static float Area(List<Vector3> points)
    {
        float twice = 0;
        int n = points.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % n];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return MathF.Abs(twice) / 2;
    }

    public static (Vector3 Min, Vector3 Max) WorldBox(Matrix4x4 transform, Vector3 min, Vector3 max)
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var p = Vector3.Transform(corner, transform);
            low = Vector3.Min(low, p);
            high = Vector3.Max(high, p);
        }
        return (low, high);
    }

    // Triangles sorted into 2 m columns, for vertical rays.
    private sealed class TriangleGrid
    {
        private const float Cell = 2f;
        private readonly IReadOnlyList<Vector3> _triangles;
        private readonly Dictionary<(int, int), List<int>> _cells = new();

        public TriangleGrid(IReadOnlyList<Vector3> triangles)
        {
            _triangles = triangles;
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                var a = triangles[i];
                var b = triangles[i + 1];
                var c = triangles[i + 2];
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Cell), y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Cell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 10000)
                    continue;
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out var list))
                            _cells[(x, y)] = list = [];
                        list.Add(i);
                    }
                }
            }
        }

        // Lowest surface met between from and to over the point, or null.
        public float? LowestAbove(Vector3 point, float from, float to)
        {
            if (!_cells.TryGetValue(((int)MathF.Floor(point.X / Cell), (int)MathF.Floor(point.Y / Cell)), out var list))
                return null;
            float? best = null;
            var xy = new Vector2(point.X, point.Y);
            foreach (int i in list)
            {
                if (GroundSnap.HeightAt(_triangles[i], _triangles[i + 1], _triangles[i + 2], xy) is { } z && z >= from && z <= to
                    && (best is null || z < best))
                    best = z;
            }
            return best;
        }
    }
}
