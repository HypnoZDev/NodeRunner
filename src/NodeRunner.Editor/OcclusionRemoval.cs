using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// One occluder map file of the game (*_occl_*.ymap) touched by deleted buildings: the boxes and mesh triangles to
// remove, and the removed shapes as triangles in world positions (for the preview in the 3D view).
public sealed record OccluderFileCut(string GamePath, IReadOnlySet<int> Boxes, IReadOnlyDictionary<int, IReadOnlySet<int>> Triangles,
    IReadOnlyList<Vector3> Preview)
{
    public string FileName => Path.GetFileName(GamePath);
    public int TriangleCount => Triangles.Values.Sum(t => t.Count);
}

// Removes the occluders of deleted buildings.
public static class OcclusionRemoval
{
    // Taken off each side of the building's box, so that an occluder of a neighbour merely touching it stays.
    public const float Tolerance = 0.3f;

    // The space of a deleted object whose occluders go, or null (no model box, or a flat object: see MinHeight).
    public static CutVolume? VolumeOf(MapEntity entity, Archetype? archetype)
    {
        if (CollisionRemoval.VolumeOf(entity, archetype) is not { } volume)
            return null;
        var shrink = Vector3.Min(new Vector3(Tolerance), (volume.Max - volume.Min) * 0.25f);
        return volume with { Min = volume.Min + shrink, Max = volume.Max - shrink };
    }

    // What to remove from files (game path and occluders of each candidate map file) for these deleted objects; files
    // with nothing to remove are left out.
    public static List<OccluderFileCut> Plan(IEnumerable<(string GamePath, MapOccluders Occluders)> files,
        IEnumerable<(MapEntity Entity, Archetype? Archetype)> removed)
    {
        var volumes = new List<(CutVolume Volume, Vector3 Min, Vector3 Max)>();
        foreach (var (entity, archetype) in removed)
        {
            if (VolumeOf(entity, archetype) is { } volume && GroundSnap.WorldBox(entity, archetype) is var (min, max))
                volumes.Add((volume, min, max));
        }
        var cuts = new List<OccluderFileCut>();
        if (volumes.Count == 0)
            return cuts;

        foreach (var (path, occluders) in files)
        {
            var preview = new List<Vector3>();
            var boxes = new HashSet<int>();
            foreach (var box in occluders.Boxes)
            {
                var shape = box.Triangles();
                if (!volumes.Any(v => Near(v, shape) && (Touches(v.Volume, shape) || Contains(box, v.Volume))))
                    continue;
                boxes.Add(box.Index);
                preview.AddRange(shape);
            }
            var triangles = new Dictionary<int, IReadOnlySet<int>>();
            foreach (var model in occluders.Models)
            {
                var gone = new HashSet<int>();
                for (int t = 0; t < model.TriangleCount; t++)
                {
                    var a = model.Vertices[model.Indices[t * 3]];
                    var b = model.Vertices[model.Indices[t * 3 + 1]];
                    var c = model.Vertices[model.Indices[t * 3 + 2]];
                    Vector3[] shape = [a, b, c];
                    if (!volumes.Any(v => Near(v, shape) && Touches(v.Volume, shape)))
                        continue;
                    gone.Add(t);
                    preview.AddRange(shape);
                }
                if (gone.Count > 0)
                    triangles[model.Index] = gone;
            }
            if (boxes.Count > 0 || triangles.Count > 0)
                cuts.Add(new OccluderFileCut(path, boxes, triangles, preview));
        }
        cuts.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.FileName, y.FileName));
        return cuts;
    }

    public static bool Touches(CutVolume volume, IReadOnlyList<Vector3> triangles)
    {
        var center = (volume.Min + volume.Max) * 0.5f;
        var half = (volume.Max - volume.Min) * 0.5f;
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            if (TriangleTouchesBox(
                    Vector3.Transform(triangles[i], volume.ToLocal) - center,
                    Vector3.Transform(triangles[i + 1], volume.ToLocal) - center,
                    Vector3.Transform(triangles[i + 2], volume.ToLocal) - center, half))
                return true;
        }
        return false;
    }

    private static bool Contains(OccluderBox box, CutVolume volume)
    {
        if (!Matrix4x4.Invert(volume.ToLocal, out var toWorld))
            return false;
        var p = Vector3.Transform((volume.Min + volume.Max) * 0.5f, toWorld) - box.Center;
        float x = p.X * MathF.Cos(box.Angle) + p.Y * MathF.Sin(box.Angle);
        float y = -p.X * MathF.Sin(box.Angle) + p.Y * MathF.Cos(box.Angle);
        return MathF.Abs(x) <= box.Size.X / 2 && MathF.Abs(y) <= box.Size.Y / 2 && MathF.Abs(p.Z) <= box.Size.Z / 2;
    }

    private static bool Near((CutVolume Volume, Vector3 Min, Vector3 Max) v, Vector3[] shape)
    {
        var min = shape[0];
        var max = shape[0];
        foreach (var p in shape)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return min.X <= v.Max.X && max.X >= v.Min.X && min.Y <= v.Max.Y && max.Y >= v.Min.Y && min.Z <= v.Max.Z && max.Z >= v.Min.Z;
    }

    // Separating axis test between a triangle and a box centred on the origin with half sizes h: the box's 3 axes,
    // the triangle's normal, and the 9 cross products of the box axes with the triangle's edges.
    public static bool TriangleTouchesBox(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 h)
    {
        // Box axes.
        if (MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < -h.X || MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > h.X) return false;
        if (MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < -h.Y || MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > h.Y) return false;
        if (MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)) < -h.Z || MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)) > h.Z) return false;

        Span<Vector3> edges = [v1 - v0, v2 - v1, v0 - v2];
        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        foreach (var e in edges)
        {
            foreach (var u in axes)
            {
                var axis = Vector3.Cross(u, e);
                if (axis.LengthSquared() < 1e-12f)
                    continue;
                if (Separates(axis, v0, v1, v2, h))
                    return false;
            }
        }
        var normal = Vector3.Cross(edges[0], edges[1]);
        return normal.LengthSquared() < 1e-12f || !Separates(normal, v0, v1, v2, h);
    }

    private static bool Separates(Vector3 axis, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 h)
    {
        float p0 = Vector3.Dot(axis, v0), p1 = Vector3.Dot(axis, v1), p2 = Vector3.Dot(axis, v2);
        float r = h.X * MathF.Abs(axis.X) + h.Y * MathF.Abs(axis.Y) + h.Z * MathF.Abs(axis.Z);
        return MathF.Min(p0, MathF.Min(p1, p2)) > r || MathF.Max(p0, MathF.Max(p1, p2)) < -r;
    }
}
