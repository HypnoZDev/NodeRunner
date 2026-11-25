using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Drawing new collision surfaces with clicks in the 3D view: 3 corners make a triangle, 4 a quadrilateral (two
// triangles). The first corner, on an existing collision surface, chooses the file and the material; the next ones may
// be in the air (at the height of the first corner). Corners snap to the collision corners near them.
internal sealed class CollisionDrawTool(bool quad)
{
    public const float SnapRadius = 0.3f;

    public bool Quad { get; } = quad;

    public int CornerCount => Quad ? 4 : 3;

    public List<Vector3> Corners { get; } = [];

    public string? Path { get; private set; }
    public Matrix4x4 Placement { get; private set; } = Matrix4x4.Identity;
    public byte Material { get; private set; }

    // Where a click would put the next corner (world space), or null when it would not go anywhere.
    public Vector3? Next(CollisionLayer layer, Vector3 origin, Vector3 direction)
    {
        var hit = layer.RayHit(origin, direction);
        Vector3 point;
        if (hit is { } h)
            point = h.Point;
        else if (Corners.Count > 0 && MathF.Abs(direction.Z) > 1e-4f)
        {
            // In the air: on the level of the first corner.
            float t = (Corners[0].Z - origin.Z) / direction.Z;
            if (t <= 0f)
                return null;
            point = origin + direction * t;
        }
        else
            return null;
        var placed = Corners.Where(c => Vector3.DistanceSquared(c, point) < SnapRadius * SnapRadius).Cast<Vector3?>().FirstOrDefault();
        return placed ?? layer.NearestCorner(point, SnapRadius) ?? point;
    }

    // Adds a corner; the first one must be on a surface (it gives the file). True when it was taken.
    public bool Add(CollisionLayer layer, Vector3 origin, Vector3 direction)
    {
        if (Corners.Count == 0)
        {
            if (layer.RayHit(origin, direction) is not { } hit)
                return false;
            Path = hit.Path;
            Placement = hit.Placement;
            Material = hit.Material;
        }
        if (Next(layer, origin, direction) is not { } point)
            return false;
        Corners.Add(point);
        return true;
    }

    public bool IsComplete => Corners.Count >= CornerCount && Path is not null;

    // The finished shape as triangles of the file (file space), turned towards the camera, then starts again.
    public CollisionAddEdit Finish(CollisionEdits edits, Vector3 camera, string description)
    {
        if (!Matrix4x4.Invert(Placement, out var toFile))
            toFile = Matrix4x4.Identity;
        var c = Corners;
        List<(Vector3, Vector3, Vector3)> faces = Quad ? [(c[0], c[1], c[2]), (c[0], c[2], c[3])] : [(c[0], c[1], c[2])];
        var triangles = new List<CollisionTriangle>();
        foreach (var (a, b0, c0) in faces)
        {
            var (b, d) = (b0, c0);
            var normal = Vector3.Cross(b - a, d - a);
            if (normal.LengthSquared() < 1e-8f)
                continue; // Corners in a line: no surface.
            if (Vector3.Dot(normal, camera - (a + b + d) / 3f) < 0f)
                (b, d) = (d, b);
            triangles.Add(CollisionTriangle.Of(Vector3.Transform(a, toFile), Vector3.Transform(b, toFile), Vector3.Transform(d, toFile), Material, 0));
        }
        var edit = new CollisionAddEdit(edits, description, Path!, triangles);
        Corners.Clear();
        return edit;
    }
}
