using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Models;

namespace NodeRunner.Data.Native.Collisions;

// Cutting door-sized openings through a collision file (WallOpening, file space):
// - triangles facing the opening are replaced by the pieces around the hole, and when the wall has two faces (front and
//   back), the sides and top of the doorway are closed so nothing can slip into the wall;
// - boxes (solid) crossing it are split into the boxes around it (left, right, above, below), through their whole
//   thickness;
// - small round shapes (spheres, capsules, cylinders) whose middle is in it go.
// New corners, polygons and materials go into longer copies of the geometry's arrays appended at the end of the file
// (with room for a bigger search tree); the replaced polygons are then removed by Remove, which rebuilds the tree.
internal sealed partial class BoundsFile
{
    private const int VertexColoursPointer = 0xB8;
    private const int OctantsPointer = 0xC0;
    private const int MaxVertices = 0x7FFF;
    private const float AxisAlignment = 0.97f;
    private const float MinPiece = 0.01f;
    private const float TwoFaces = 0.05f;

    // Boxes crossing an opening that could not be split (turned too far from it), found by the last PlanOpening.
    public int UnsplitBoxes { get; private set; }

    // Plans the new arrays (WithAppended writes them) and returns the polygons to remove afterwards, numbered as in the
    // grown file.
    public HashSet<int> PlanOpening(Opening opening)
    {
        var parts = new List<Part>();
        int polygon = 0;
        Collect(0, Matrix4x4.Identity, parts, [], ref polygon);
        var removed = new HashSet<int>();
        int shift = 0;
        UnsplitBoxes = 0;
        foreach (var part in parts)
        {
            int added = PlanPart(part, opening, out var cut);
            foreach (int p in cut)
                removed.Add(part.FirstPolygon + shift + p);
            shift += added;
        }
        return removed;
    }

    // New polygon: copied from a polygon of the geometry (type, material, other bytes), with new corners.
    private readonly record struct NewPolygon(int Source, int[] Corners);

    // The corners of a geometry with those added, the shrunk copy of each corner when the geometry has one, and for each
    // added corner the corner whose colour it takes.
    private sealed class Corners(Part part)
    {
        public readonly List<Vector3> Points = [.. part.Points];
        public readonly List<Vector3>? Shrunk = part.ShrunkPoints?.ToList();
        public readonly List<int> ColourOf = [];

        // A corner mixing corners of the geometry (the same mix for the shrunk copies).
        public int Mix((int Vertex, float Weight)[] mix)
        {
            var p = Vector3.Zero;
            var s = Vector3.Zero;
            foreach (var (v, w) in mix)
            {
                p += Points[v] * w;
                if (Shrunk is not null)
                    s += Shrunk[v] * w;
            }
            return Add(p, s, mix.MaxBy(m => m.Weight).Vertex);
        }

        public int Add(Vector3 point, Vector3 shrunk, int colourOf)
        {
            Points.Add(point);
            Shrunk?.Add(shrunk);
            ColourOf.Add(colourOf < part.Points.Length ? colourOf : ColourOf[colourOf - part.Points.Length]);
            return Points.Count - 1;
        }
    }

    // One geometry: the polygons of its own to remove; returns how many polygons it gains.
    private int PlanPart(Part part, Opening opening, out List<int> removed)
    {
        removed = [];
        int o = part.Offset;
        var corners = new Corners(part);
        var added = new List<NewPolygon>();
        CutTriangles(part, opening, removed, added, corners);
        CutBoxes(part, opening, removed, added, corners);
        CutRoundShapes(part, opening, removed, added, corners);
        if (added.Count == 0)
            return 0;

        if (U8(o + TypeOffset) != GeometryBvh || U64(o + OctantsPointer) != 0)
            throw new InvalidDataException("Ce morceau de collision n'a pas d'arbre de recherche : il ne peut pas être percé.");
        if (corners.Points.Count > MaxVertices)
            throw new InvalidDataException("Trop de sommets dans ce morceau de collision pour le percer.");
        WriteGrown(part, corners, added, removed.Count);
        return added.Count;
    }

    private void CutTriangles(Part part, Opening opening, List<int> removed, List<NewPolygon> added, Corners grown)
    {
        var triangles = new List<int>();
        var indices = new List<ushort>();
        for (int i = 0; i < part.PolygonCount; i++)
        {
            int p = part.Polygons + i * 16;
            if ((U8(p) & 7) != 0)
                continue;
            triangles.Add(i);
            indices.Add((ushort)(U16(p + 4) & 0x7FFF));
            indices.Add((ushort)(U16(p + 6) & 0x7FFF));
            indices.Add((ushort)(U16(p + 8) & 0x7FFF));
        }
        if (triangles.Count == 0)
            return;
        var cut = WallOpening.Cut(part.Points, indices, opening);
        if (cut.IsEmpty)
            return;
        removed.AddRange(cut.Removed.Select(t => triangles[t]));

        var corners = new int[cut.Vertices.Count];
        for (int v = 0; v < cut.Vertices.Count; v++)
        {
            var (t, w) = cut.Vertices[v];
            corners[v] = grown.Mix([(indices[t * 3], w.X), (indices[t * 3 + 1], w.Y), (indices[t * 3 + 2], w.Z)]);
        }
        for (int k = 0; k + 2 < cut.Indices.Count; k += 3)
        {
            int source = triangles[cut.Vertices[cut.Indices[k]].Triangle];
            added.Add(new NewPolygon(source, [corners[cut.Indices[k]], corners[cut.Indices[k + 1]], corners[cut.Indices[k + 2]]]));
        }

        // Faces cut at different depths (the front and the back of a wall): the doorway gets its sides and top, up to
        // where the wall goes.
        float front = float.MaxValue, back = float.MinValue, top = 0;
        int sample = -1, colour = 0;
        foreach (int t in cut.Removed)
        {
            var a = opening.Local(part.Points[indices[t * 3]]);
            var b = opening.Local(part.Points[indices[t * 3 + 1]]);
            var c = opening.Local(part.Points[indices[t * 3 + 2]]);
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-12f || MathF.Abs(normal.Z) / normal.Length() < 0.25f)
                continue;
            float depth = (a.Z + b.Z + c.Z) / 3f;
            front = MathF.Min(front, depth);
            back = MathF.Max(back, depth);
            top = MathF.Max(top, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
            sample = triangles[t];
            colour = indices[t * 3];
        }
        if (sample < 0 || back - front < TwoFaces)
            return;
        float hw = opening.Width * 0.5f, h = MathF.Min(opening.Height, top);
        // Corners of the doorway on both faces, from the opening's own axes (the shrunk copy is the same point).
        int Corner(float x, float y, float z) => grown.Add(opening.World(x, y, z), opening.World(x, y, z), colour);
        int fl = Corner(-hw, 0, front), fr = Corner(hw, 0, front), ftl = Corner(-hw, h, front), ftr = Corner(hw, h, front);
        int bl = Corner(-hw, 0, back), br = Corner(hw, 0, back), btl = Corner(-hw, h, back), btr = Corner(hw, h, back);
        // Corners given clockwise seen from the doorway: the faces turn towards it (cross(b - a, c - a) points inwards).
        void Quad(int a, int b, int c, int d)
        {
            added.Add(new NewPolygon(sample, [a, c, b]));
            added.Add(new NewPolygon(sample, [a, d, c]));
        }
        Quad(fl, bl, btl, ftl);       // left side, facing right
        Quad(br, fr, ftr, btr);       // right side, facing left
        if (h >= opening.Height - 1e-3f)
            Quad(ftl, btl, btr, ftr); // top, facing down (none when the wall stops below it)
    }

    private void CutBoxes(Part part, Opening opening, List<int> removed, List<NewPolygon> added, Corners grown)
    {
        for (int i = 0; i < part.PolygonCount; i++)
        {
            int p = part.Polygons + i * 16;
            if ((U8(p) & 7) != 3)
                continue;
            int[] corners = [I16(p + 4), I16(p + 6), I16(p + 8), I16(p + 10)];
            if (corners.Any(c => c < 0 || c >= part.Points.Length))
                continue;
            var box = BoxOf(corners.Select(c => part.Points[c]).ToArray());
            var shrunkBox = part.ShrunkPoints is { } s ? BoxOf(corners.Select(c => s[c]).ToArray()) : box;
            if (SplitBox(box, opening) is not { } pieces)
                continue;
            removed.Add(i);
            foreach (var (lo, hi) in pieces)
            {
                var at = BoxCorners(box, lo, hi);
                var shrunkAt = BoxCorners(shrunkBox, lo, hi);
                added.Add(new NewPolygon(i, [.. Enumerable.Range(0, 4).Select(k => grown.Add(at[k], shrunkAt[k], corners[0]))]));
            }
        }
    }

    private readonly record struct BoxShape(Vector3 Center, Vector3[] Half);

    // The box of four stored corners: the middle of two of them is the centre of a face.
    private static BoxShape BoxOf(Vector3[] c)
    {
        var center = (c[0] + c[1] + c[2] + c[3]) * 0.25f;
        return new BoxShape(center, [(c[0] + c[1]) * 0.5f - center, (c[0] + c[2]) * 0.5f - center, (c[0] + c[3]) * 0.5f - center]);
    }

    // The four corners to store for the part of the box between lo and hi (from -1 to 1 along each half axis).
    private static Vector3[] BoxCorners(BoxShape box, Vector3 lo, Vector3 hi)
    {
        var center = box.Center;
        var half = new Vector3[3];
        for (int k = 0; k < 3; k++)
        {
            center += box.Half[k] * ((lo[k] + hi[k]) * 0.5f);
            half[k] = box.Half[k] * ((hi[k] - lo[k]) * 0.5f);
        }
        return
        [
            center + half[0] + half[1] + half[2],
            center + half[0] - half[1] - half[2],
            center - half[0] + half[1] - half[2],
            center - half[0] - half[1] + half[2],
        ];
    }

    // The boxes left around the opening (from -1 to 1 along each half axis of the box), null when the box does not
    // cross it or is turned too far from it.
    private List<(Vector3 Lo, Vector3 Hi)>? SplitBox(BoxShape box, Opening opening)
    {
        if (box.Half.Any(h => h.LengthSquared() < 1e-10f))
            return null;
        float Along(int k, Vector3 point) => Vector3.Dot(point - box.Center, box.Half[k]) / box.Half[k].LengthSquared();

        // Quick test in the opening's axes: the box's corners around the doorway.
        var stored = BoxCorners(box, -Vector3.One, Vector3.One);
        var corners = stored.Concat(stored.Select(c => 2 * box.Center - c)).Select(opening.Local).ToList();
        float hw = opening.Width * 0.5f;
        if (corners.Max(c => c.X) <= -hw || corners.Min(c => c.X) >= hw || corners.Max(c => c.Y) <= 0 || corners.Min(c => c.Y) >= opening.Height
            || corners.Min(c => c.Z) > opening.Depth || corners.Max(c => c.Z) < -opening.Depth)
            return null;

        // Which half axis goes through the wall (the box is cut through its whole thickness), which one is the most
        // upright, which one across. A box on a slope is cut in its own axes: around the opening's rectangle.
        int[] order = [0, 1, 2];
        int through = order.MaxBy(k => MathF.Abs(Vector3.Dot(Vector3.Normalize(box.Half[k]), opening.Normal)));
        int up = order.Where(k => k != through).MaxBy(k => MathF.Abs(Vector3.Normalize(box.Half[k]).Z));
        int across = 3 - through - up;
        if (MathF.Abs(Vector3.Dot(Vector3.Normalize(box.Half[through]), opening.Normal)) < AxisAlignment)
        {
            UnsplitBoxes++;
            return null;
        }
        Vector3[] rectangle = [opening.World(-hw, 0, 0), opening.World(hw, 0, 0), opening.World(hw, opening.Height, 0), opening.World(-hw, opening.Height, 0)];
        float a0 = Math.Max(rectangle.Min(p => Along(across, p)), -1), a1 = Math.Min(rectangle.Max(p => Along(across, p)), 1);
        float b0 = Math.Max(rectangle.Min(p => Along(up, p)), -1), b1 = Math.Min(rectangle.Max(p => Along(up, p)), 1);
        if (a0 >= a1 || b0 >= b1)
            return null;

        var pieces = new List<(Vector3, Vector3)>();
        void Piece(float acrossLo, float acrossHi, float upLo, float upHi)
        {
            // Thinner than 1 cm: left out.
            if ((acrossHi - acrossLo) * box.Half[across].Length() < MinPiece || (upHi - upLo) * box.Half[up].Length() < MinPiece)
                return;
            var lo = new Vector3(-1);
            var hi = new Vector3(1);
            lo[across] = acrossLo; hi[across] = acrossHi;
            lo[up] = upLo; hi[up] = upHi;
            pieces.Add((lo, hi));
        }
        Piece(-1, a0, -1, 1);  // one side
        Piece(a1, 1, -1, 1);   // other side
        Piece(a0, a1, -1, b0); // under
        Piece(a0, a1, b1, 1);  // above
        return pieces;
    }

    // Spheres in the doorway go; capsules and cylinders (rails, pipes) crossing it keep the parts on both sides of it.
    private void CutRoundShapes(Part part, Opening opening, List<int> removed, List<NewPolygon> added, Corners grown)
    {
        for (int i = 0; i < part.PolygonCount; i++)
        {
            int p = part.Polygons + i * 16;
            int type = U8(p) & 7;
            if (type is not (1 or 2 or 4))
                continue;
            int a = U16(p + 2), b = type == 1 ? a : U16(p + 8);
            if (a >= part.Points.Length || b >= part.Points.Length)
                continue;
            float radius = MathF.Max(F32(p + 4), 0);
            if (SegmentInside(opening, opening.Local(part.Points[a]), opening.Local(part.Points[b]), radius) is not { } inside)
                continue;
            removed.Add(i);
            if (type == 1)
                continue;
            float length = Vector3.Distance(part.Points[a], part.Points[b]);
            foreach (var (from, to) in new[] { (0f, inside.From), (inside.To, 1f) })
            {
                if ((to - from) * length < MinPiece)
                    continue;
                int start = grown.Mix([(a, 1 - from), (b, from)]);
                int end = grown.Mix([(a, 1 - to), (b, to)]);
                added.Add(new NewPolygon(i, [start, end]));
            }
        }
    }

    // The part of the segment (0 to 1) within the opening widened by the radius, null when it does not cross it.
    private static (float From, float To)? SegmentInside(Opening opening, Vector3 a, Vector3 b, float radius)
    {
        float hw = opening.Width * 0.5f + radius;
        var min = new Vector3(-hw, -radius, -opening.Depth);
        var max = new Vector3(hw, opening.Height + radius, opening.Depth);
        float from = 0, to = 1;
        var d = b - a;
        for (int k = 0; k < 3; k++)
        {
            if (MathF.Abs(d[k]) < 1e-9f)
            {
                if (a[k] < min[k] || a[k] > max[k])
                    return null;
                continue;
            }
            float t0 = (min[k] - a[k]) / d[k], t1 = (max[k] - a[k]) / d[k];
            from = MathF.Max(from, MathF.Min(t0, t1));
            to = MathF.Min(to, MathF.Max(t0, t1));
            if (from >= to)
                return null;
        }
        return (from, to);
    }

    // The geometry's arrays with the new corners and polygons, appended (WithAppended), and room for its search tree.
    private void WriteGrown(Part part, Corners grown, List<NewPolygon> added, int removedCount)
    {
        int o = part.Offset;
        var points = grown.Points;
        var shrunk = grown.Shrunk;
        if (!Matrix4x4.Invert(part.Placement, out var toLocal))
            throw new InvalidDataException("Placement d'un morceau de collision non inversible.");
        var quantum = Vec3(o + QuantumOffset);
        var center = Vec3(o + CenterOffset);
        byte[] Quantized(IReadOnlyList<Vector3> all, int from)
        {
            var bytes = new byte[all.Count * 6];
            _data.AsSpan(from, part.Points.Length * 6).CopyTo(bytes);
            for (int v = part.Points.Length; v < all.Count; v++)
            {
                var q = Vector3.Clamp((Vector3.Transform(all[v], toLocal) - center) / quantum, new Vector3(-QuantizedRange), new Vector3(QuantizedRange));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(v * 6), (short)MathF.Round(q.X));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(v * 6 + 2), (short)MathF.Round(q.Y));
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(v * 6 + 4), (short)MathF.Round(q.Z));
            }
            return bytes;
        }

        int count = part.PolygonCount + added.Count;
        var polygons = new byte[count * 16];
        _data.AsSpan(part.Polygons, part.PolygonCount * 16).CopyTo(polygons);
        int materialsAt = Resolve(U64(o + PolygonMaterialsPointer), part.PolygonCount, 1);
        var materials = new byte[count];
        if (materialsAt >= 0)
            _data.AsSpan(materialsAt, part.PolygonCount).CopyTo(materials);
        for (int n = 0; n < added.Count; n++)
        {
            var (source, corners) = added[n];
            var at = polygons.AsSpan((part.PolygonCount + n) * 16, 16);
            _data.AsSpan(part.Polygons + source * 16, 16).CopyTo(at);
            if (corners.Length == 2)
            {
                // Capsule or cylinder: its two ends (the radius is kept).
                BinaryPrimitives.WriteUInt16LittleEndian(at[2..], (ushort)corners[0]);
                BinaryPrimitives.WriteUInt16LittleEndian(at[8..], (ushort)corners[1]);
            }
            else if (corners.Length == 3)
            {
                var (a, b, c) = (points[corners[0]], points[corners[1]], points[corners[2]]);
                // The area, its lowest bits holding the type (0: triangle).
                BinaryPrimitives.WriteSingleLittleEndian(at, Vector3.Cross(b - a, c - a).Length() * 0.5f);
                at[0] &= 0xF8;
                for (int k = 0; k < 3; k++)
                    BinaryPrimitives.WriteUInt16LittleEndian(at[(4 + 2 * k)..], (ushort)corners[k]);
                for (int k = 0; k < 3; k++)
                    BinaryPrimitives.WriteUInt16LittleEndian(at[(10 + 2 * k)..], 0xFFFF);
            }
            else
            {
                for (int k = 0; k < 4; k++)
                    BinaryPrimitives.WriteInt16LittleEndian(at[(4 + 2 * k)..], (short)corners[k]);
            }
            materials[part.PolygonCount + n] = materials[source];
        }

        _appended.Add((o + VerticesPointer, Quantized(points, part.Vertices)));
        W32(o + VerticesCount, (uint)points.Count);
        if (part.Shrunk >= 0 && shrunk is not null)
        {
            _appended.Add((o + ShrunkVerticesPointer, Quantized(shrunk, part.Shrunk)));
            W32(o + ShrunkVerticesCount, (uint)points.Count);
        }
        _appended.Add((o + PolygonsPointer, polygons));
        W32(o + PolygonsCount, (uint)count);
        _appended.Add((o + PolygonMaterialsPointer, materials));
        // Colours of the corners (4 bytes each), when the geometry has them: a new corner takes one of an old one.
        int colours = Resolve(U64(o + VertexColoursPointer), part.Points.Length, 4);
        if (colours >= 0)
        {
            var bytes = new byte[points.Count * 4];
            _data.AsSpan(colours, part.Points.Length * 4).CopyTo(bytes);
            for (int v = part.Points.Length; v < points.Count; v++)
                _data.AsSpan(colours + grown.ColourOf[v - part.Points.Length] * 4, 4).CopyTo(bytes.AsSpan(v * 4));
            _appended.Add((o + VertexColoursPointer, bytes));
        }

        // Room for the tree of the polygons kept in the end.
        int bvh = Resolve(U64(o + GeometryBvhPointer), 1, 128);
        if (bvh < 0)
            return;
        int final = Math.Max(count - removedCount, 1);
        var size = BvhBuilder.Build([.. Enumerable.Repeat((Vector3.Zero, Vector3.One), final)], 4, reorder: true);
        int nodeCapacity = Math.Max((int)U32(bvh + BvhNodesCapacity), size.Nodes.Count);
        int treeCapacity = Math.Max(U16(bvh + BvhTreesCapacity), size.Trees.Count);
        if (treeCapacity > ushort.MaxValue)
            throw new InvalidDataException("L'arbre de recherche de la collision serait trop grand.");
        _appended.Add((bvh + BvhNodesPointer, new byte[nodeCapacity * 16]));
        W32(bvh + BvhNodesCapacity, (uint)nodeCapacity);
        if (U64(bvh + BvhTreesPointer) != 0)
        {
            _appended.Add((bvh + BvhTreesPointer, new byte[treeCapacity * 16]));
            W16(bvh + BvhTreesCapacity, (ushort)treeCapacity);
        }
    }
}
