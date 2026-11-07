using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Thin box occluders in the outer walls of an interior (MLO). A side of a room is an outer wall where no other room
// lies behind it; the openings to the outside (portals to "limbo": doors, windows) are left free, so the rooms can
// still be seen through them. The interior's collision puts each box inside the real wall.
public static class MloOccluders
{
    private const float Cell = 1f;
    private const int MaxCells = 64;
    // How far behind a side another room is looked for.
    private const float Probe = 0.6f;
    // How far from a room's side its wall is looked for in the collision.
    private const float Search = 2f;
    private const float MaxWallThickness = 1.5f;
    private const float SamePlane = 0.5f;
    private const float Gap = 0.05f;
    // Enough to hide; a thicker box only risks sticking out of the wall.
    private const float MaxBoxThickness = 0.5f;
    private const float PortalMargin = 0.3f;
    private const float PortalDistance = 2f;
    private const float VerticalNormal = 0.3f;
    public const float MinWidth = 1f;
    public const float MinHeight = 1.5f;
    public const int MaxBoxes = 64;

    private sealed record Room(int Index, Vector3 Min, Vector3 Max);

    // collision: the interior's own collision, in its local space.
    public static List<OccluderBox> Fit(MapEntity interior, Archetype archetype, CollisionMesh? collision)
    {
        var rooms = archetype.InteriorRooms
            .Select((r, i) => new Room(i, r.BoundsMin, r.BoundsMax))
            .Where(r => r.Index > 0 && r.Min != r.Max && !archetype.InteriorRooms[r.Index].Name.Equals("limbo", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var openings = archetype.InteriorPortals.Where(p => (p.RoomFrom == 0 || p.RoomTo == 0) && p.Corners.Count >= 3).ToList();
        var walls = collision is null ? [] : VerticalTriangles(collision);

        var transform = interior.Transform;
        float heading = MathF.Atan2(transform.M12, transform.M11);
        var boxes = new List<OccluderBox>();
        foreach (var room in rooms)
        {
            for (int side = 0; side < 4; side++)
            {
                foreach (var (center, size, angle) in SideWalls(room, side, rooms, openings, walls, collision is not null))
                {
                    boxes.Add(new OccluderBox(0, Vector3.Transform(center, transform), size, heading + angle));
                    if (boxes.Count >= MaxBoxes)
                        return boxes;
                }
            }
        }
        return boxes;
    }

    // The wall pieces of one side of a room (-X, +X, -Y, +Y), in the interior's space.
    private static IEnumerable<(Vector3 Center, Vector3 Size, float Angle)> SideWalls(Room room, int side, List<Room> rooms,
        List<InteriorPortal> openings, List<(Vector3 A, Vector3 B, Vector3 C)> walls, bool hasCollision)
    {
        int a = side / 2;              // 0: wall across X, 1: across Y.
        int b = 1 - a;                 // Along the wall.
        float sign = side % 2 == 0 ? -1f : 1f;
        var normal = Axis(a) * sign;
        var along = Axis(b);
        float plane = Get(sign < 0 ? room.Min : room.Max, a);
        float u0 = Get(room.Min, b), u1 = Get(room.Max, b);
        float z0 = room.Min.Z, z1 = room.Max.Z;
        int nu = Math.Clamp((int)MathF.Round((u1 - u0) / Cell), 1, MaxCells);
        int nz = Math.Clamp((int)MathF.Round((z1 - z0) / Cell), 1, MaxCells);
        float cu = (u1 - u0) / nu, cz = (z1 - z0) / nz;

        Vector3 OnPlane(float u, float z, float offset)
        {
            var p = along * u + Vector3.UnitZ * z;
            p = Set(p, a, plane);
            return p + normal * offset;
        }

        var wall = new bool[nu, nz];
        bool any = false;
        for (int i = 0; i < nu; i++)
        for (int j = 0; j < nz; j++)
        {
            var behind = OnPlane(u0 + (i + 0.5f) * cu, z0 + (j + 0.5f) * cz, Probe);
            wall[i, j] = !rooms.Any(r => r != room && Inside(r, behind));
            any |= wall[i, j];
        }
        if (!any)
            yield break;

        foreach (var portal in openings)
        {
            if (portal.Corners.Any(c => MathF.Abs(Get(c, a) - plane) > PortalDistance))
                continue;
            float pu0 = portal.Corners.Min(c => Get(c, b)) - PortalMargin, pu1 = portal.Corners.Max(c => Get(c, b)) + PortalMargin;
            float pz0 = portal.Corners.Min(c => c.Z) - PortalMargin, pz1 = portal.Corners.Max(c => c.Z) + PortalMargin;
            // A portal lying flat (skylight, stairwell to the roof) is not in this wall.
            if (pu1 - pu0 <= 2 * PortalMargin + 0.05f)
                continue;
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nz; j++)
            {
                if (u0 + (i + 1) * cu > pu0 && u0 + i * cu < pu1 && z0 + (j + 1) * cz > pz0 && z0 + j * cz < pz1)
                    wall[i, j] = false;
            }
        }

        // The real wall: hits of lines crossing the side, at the middle of the wall cells. With a collision, a cell
        // without wall there (an opening that is no portal) or with a wall elsewhere (a recess) is left free: a box
        // out of the walls would hide what is in the room.
        float from = 0f, to = 0f;
        if (hasCollision)
        {
            var inner = new float?[nu, nz];
            var outer = new float[nu, nz];
            var near = walls.Where(t => Near(t, a, plane)).ToList();
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nz; j++)
            {
                if (!wall[i, j])
                    continue;
                var start = OnPlane(u0 + (i + 0.5f) * cu, z0 + (j + 0.5f) * cz, -Search);
                float first = float.MaxValue, last = float.MinValue;
                foreach (var t in near)
                {
                    if (Hit(start, normal, t) is { } d && d <= 2 * Search)
                    {
                        first = MathF.Min(first, d);
                        last = MathF.Max(last, d);
                    }
                }
                if (first == float.MaxValue)
                {
                    wall[i, j] = false;
                    continue;
                }
                inner[i, j] = first - Search;
                outer[i, j] = MathF.Min(last, first + MaxWallThickness) - Search;
            }
            var hits = inner.Cast<float?>().OfType<float>().ToList();
            if (hits.Count == 0)
                yield break;
            from = Median(hits);
            var thick = new List<float>();
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nz; j++)
            {
                if (inner[i, j] is not { } first)
                    continue;
                if (MathF.Abs(first - from) > SamePlane)
                    wall[i, j] = false;
                else
                    thick.Add(outer[i, j]);
            }
            to = MathF.Max(from, Median(thick));
        }
        const float thin = OccluderBox.PositionUnit;
        if (to - from >= thin + 2 * Gap)
        {
            from += Gap;
            to = MathF.Min(to - Gap, from + MaxBoxThickness);
        }
        else
        {
            float middle = (from + to) / 2;
            (from, to) = (middle - thin / 2, middle + thin / 2);
        }

        foreach (var (i0, j0, i1, j1) in Rectangles(wall))
        {
            float width = (i1 - i0) * cu, height = (j1 - j0) * cz;
            if (width < MinWidth || height < MinHeight)
                continue;
            var center = OnPlane(u0 + (i0 + i1) * 0.5f * cu, z0 + (j0 + j1) * 0.5f * cz, (from + to) / 2);
            // Box X along the wall, Y across it.
            float angle = a == 0 ? MathF.PI / 2 : 0f;
            yield return (center, new Vector3(width, to - from, height), angle);
        }
    }

    // Wall cells grouped into rectangles: runs along a row, grown upwards while the rows above match.
    private static IEnumerable<(int I0, int J0, int I1, int J1)> Rectangles(bool[,] cells)
    {
        int nu = cells.GetLength(0), nz = cells.GetLength(1);
        var used = new bool[nu, nz];
        for (int j = 0; j < nz; j++)
        for (int i = 0; i < nu; i++)
        {
            if (!cells[i, j] || used[i, j])
                continue;
            int i1 = i;
            while (i1 < nu && cells[i1, j] && !used[i1, j])
                i1++;
            int j1 = j + 1;
            while (j1 < nz && Enumerable.Range(i, i1 - i).All(k => cells[k, j1] && !used[k, j1]))
                j1++;
            for (int k = i; k < i1; k++)
            for (int l = j; l < j1; l++)
                used[k, l] = true;
            yield return (i, j, i1, j1);
        }
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> VerticalTriangles(CollisionMesh mesh)
    {
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            var (p, q, r) = (mesh.Vertices[mesh.Indices[t]], mesh.Vertices[mesh.Indices[t + 1]], mesh.Vertices[mesh.Indices[t + 2]]);
            var n = Vector3.Cross(q - p, r - p);
            float length = n.Length();
            if (length > 1e-6f && MathF.Abs(n.Z) / length < VerticalNormal)
                triangles.Add((p, q, r));
        }
        return triangles;
    }

    private static bool Near((Vector3 A, Vector3 B, Vector3 C) t, int axis, float plane)
    {
        float min = MathF.Min(Get(t.A, axis), MathF.Min(Get(t.B, axis), Get(t.C, axis)));
        float max = MathF.Max(Get(t.A, axis), MathF.Max(Get(t.B, axis), Get(t.C, axis)));
        return max >= plane - Search && min <= plane + Search;
    }

    // Distance along the line to the triangle (either side), or null.
    private static float? Hit(Vector3 origin, Vector3 direction, (Vector3 A, Vector3 B, Vector3 C) t)
    {
        var e1 = t.B - t.A;
        var e2 = t.C - t.A;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-8f)
            return null;
        float inv = 1f / det;
        var s = origin - t.A;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
            return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0 || u + v > 1)
            return null;
        float d = Vector3.Dot(e2, q) * inv;
        return d >= 0 ? d : null;
    }

    private static bool Inside(Room r, Vector3 p) =>
        p.X >= r.Min.X && p.X <= r.Max.X && p.Y >= r.Min.Y && p.Y <= r.Max.Y && p.Z >= r.Min.Z && p.Z <= r.Max.Z;

    private static float Median(List<float> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : Vector3.UnitY;

    private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : v.Y;

    private static Vector3 Set(Vector3 v, int axis, float value) => axis == 0 ? v with { X = value } : v with { Y = value };
}
