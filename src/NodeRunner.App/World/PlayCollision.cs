using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// The collision around the character of the play mode, as triangles sorted into square cells: read again in the
// background when she walks away from where it was read.
internal sealed class PlayCollision
{
    private const float Radius = 50f;
    private const float ReadAgain = 20f; // Metres from where the area was read.
    private const float CellSize = 2f;
    private const int MaxCellsPerTriangle = 4096;

    private sealed class Area(object catalog, int edits, Vector2 centre, Vector3[] triangles, Dictionary<(int, int), List<int>> cells)
    {
        public readonly object Catalog = catalog;
        public readonly int Edits = edits;
        public readonly Vector2 Centre = centre;
        public readonly Vector3[] Triangles = triangles; // Three corners each.
        public readonly Dictionary<(int, int), List<int>> Cells = cells;
    }

    private readonly CollisionQuery _query = new();
    private Area? _area;
    private Task<Area>? _reading;

    // The collision under the character is known (false while the first area is read, or without collisions).
    public bool IsReady(Vector3 position) => _area is { } area && Vector2.Distance(area.Centre, Flat(position)) < Radius - 5f;

    public void Update(WorldLoader world, CollisionEdits edits, Vector3 position)
    {
        if (_reading is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                _area = done.Result;
            _reading = null;
        }
        if (_reading is not null || world.Collisions is not { } catalog)
            return;
        if (_area is { } area && ReferenceEquals(area.Catalog, catalog) && area.Edits == edits.Revision
            && Vector2.Distance(area.Centre, Flat(position)) < ReadAgain)
            return;
        int revision = edits.Revision;
        _reading = Task.Run(() => Read(world, catalog, edits, revision, position));
    }

    // A reading under way goes on (it uses the query): its area is replaced once the character is elsewhere.
    public void Clear() => _area = null;

    // Height of the highest surface under (x, y) that is not above fromZ; null when there is none (or not read yet).
    public float? Ground(Vector2 point, float fromZ)
    {
        if (_area is not { } area || !area.Cells.TryGetValue(CellOf(point), out var list))
            return null;
        var triangles = area.Triangles;
        float? best = null;
        foreach (int t in list)
        {
            if (GroundSnap.HeightAt(triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2], point) is { } z && z <= fromZ
                && (best is null || z > best))
                best = z;
        }
        return best;
    }

    private Area Read(WorldLoader world, CollisionCatalog catalog, CollisionEdits edits, int revision, Vector3 position)
    {
        var min = new Vector3(position.X - Radius, position.Y - Radius, -1000f);
        var max = new Vector3(position.X + Radius, position.Y + Radius, position.Z + 300f);
        var triangles = new List<Vector3>();
        var cells = new Dictionary<(int, int), List<int>>();
        foreach (var (mesh, transform) in _query.CollisionIn(world, min, max, edits))
        {
            bool identity = transform.IsIdentity;
            var vertices = mesh.Vertices;
            var indices = mesh.Indices;
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                if (!identity)
                {
                    a = Vector3.Transform(a, transform);
                    b = Vector3.Transform(b, transform);
                    c = Vector3.Transform(c, transform);
                }
                var low = Vector3.Min(a, Vector3.Min(b, c));
                var high = Vector3.Max(a, Vector3.Max(b, c));
                if (high.X < min.X || low.X > max.X || high.Y < min.Y || low.Y > max.Y)
                    continue;
                var (x0, y0) = CellOf(new Vector2(Math.Max(low.X, min.X), Math.Max(low.Y, min.Y)));
                var (x1, y1) = CellOf(new Vector2(Math.Min(high.X, max.X), Math.Min(high.Y, max.Y)));
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerTriangle)
                    continue;
                int index = triangles.Count / 3;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!cells.TryGetValue((x, y), out var list))
                            cells[(x, y)] = list = [];
                        list.Add(index);
                    }
                }
            }
        }
        return new Area(catalog, revision, Flat(position), triangles.ToArray(), cells);
    }

    private static (int, int) CellOf(Vector2 point) => ((int)MathF.Floor(point.X / CellSize), (int)MathF.Floor(point.Y / CellSize));

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);
}
