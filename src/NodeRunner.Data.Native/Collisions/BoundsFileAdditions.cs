using System.Numerics;
using NodeRunner.Data.Collisions;

namespace NodeRunner.Data.Native.Collisions;

// Adding triangles to a collision file (file space): each goes into the geometry with a search tree nearest to it, as
// three corners of its own; the geometry is quantized again around its new extent, its box and the boxes of the
// composites above it grow, its material and options are set, then its search tree is rebuilt.
internal sealed partial class BoundsFile
{
    public static byte[] AddTriangles(byte[] data, IReadOnlyList<PlacedTriangle> triangles)
    {
        if (triangles.Count == 0)
            return data;
        var file = new BoundsFile(data);
        var parts = new List<Part>();
        int polygon = 0;
        file.Collect(0, Matrix4x4.Identity, parts, [], ref polygon);
        var usable = parts.Where(p => p.PolygonCount > 0 && p.Points.Length > 0 && file.U8(p.Offset + TypeOffset) == GeometryBvh
            && file.U64(p.Offset + OctantsPointer) == 0).ToList();
        if (usable.Count == 0)
            throw new InvalidDataException("Ce fichier de collision ne peut pas recevoir de nouvelles surfaces (aucun morceau avec arbre de recherche).");

        var byPart = triangles.GroupBy(t => usable.MinBy(p => file.DistanceToPart(p, (t.A + t.B + t.C) / 3f))!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var materials = new Dictionary<int, byte>();
        var flags = new Dictionary<int, ushort>();
        var grown = new HashSet<int>();
        int shift = 0;
        foreach (var part in parts)
        {
            if (!byPart.TryGetValue(part, out var list))
                continue;
            var corners = new Corners(part);
            var added = new List<NewPolygon>();
            foreach (var t in list)
                added.Add(new NewPolygon(0, [corners.Add(t.A, t.A, 0), corners.Add(t.B, t.B, 0), corners.Add(t.C, t.C, 0)]));
            if (corners.Points.Count > MaxVertices)
                throw new InvalidDataException("Trop de sommets dans ce morceau de collision pour y ajouter ces surfaces.");
            file.WriteGrown(part, corners, added, 0, refit: true);
            for (int n = 0; n < list.Count; n++)
            {
                int number = part.FirstPolygon + shift + part.PolygonCount + n;
                materials[number] = list[n].Material;
                flags[number] = list[n].Flags;
            }
            shift += added.Count;
            grown.Add(part.Offset);
        }
        data = file.WithAppended();

        // Material and options while the new polygons keep their numbers (the tree rebuilt below reorders them).
        var withMaterials = new BoundsFile(data);
        int first = 0;
        withMaterials.SetMaterials(0, materials, flags, ref first);
        data = withMaterials.WithAppended();

        var refit = new BoundsFile(data);
        refit.Refit(grown);
        int start = 0;
        refit.Remove(0, new HashSet<int>(), ref start);
        return data;
    }

    // Distance from a point to the world box of a geometry's corners.
    private float DistanceToPart(Part part, Vector3 point)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in part.Points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return Vector3.Distance(point, Vector3.Clamp(point, min, max));
    }

    // Geometries whose corners changed: quantized again, boxes and tree frames written (trees rebuilt by Remove),
    // the composites above them updated.
    private void Refit(IReadOnlySet<int> offsets)
    {
        var parts = new List<Part>();
        var composites = new List<int>();
        int polygon = 0;
        Collect(0, Matrix4x4.Identity, parts, composites, ref polygon);
        var changed = new HashSet<int>();
        foreach (var part in parts.Where(p => offsets.Contains(p.Offset)))
        {
            WritePart(part);
            changed.Add(part.Offset);
        }
        for (int i = composites.Count - 1; i >= 0; i--)
        {
            if (UpdateComposite(composites[i], changed))
                changed.Add(composites[i]);
        }
    }
}
