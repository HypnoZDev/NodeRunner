using System.Numerics;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Native.Models;

// A new model made from a game model (original release .ydr): every triangle of the source emptied, then flat faces
// added with two of its materials, one for walls and ceiling, one for floors (the most used upright and upward ones).
// The source's shaders and embedded textures stay, so the new faces look like the source's surfaces.
public static class ShellModel
{
    // A rectangle (corners anticlockwise seen from the side it faces), its texture coordinates, its across direction.
    public readonly record struct Face(bool Floor, Vector3[] Corners, Vector2[] TexCoords, Vector3 Normal, Vector3 Across);

    // The new model's data, or an error message.
    public static (ResourceHeader Header, byte[] Content)? Build(ResourceHeader header, byte[] source, uint modelHash, IReadOnlyList<Face> faces,
        out string? error)
    {
        error = null;
        var all = DrawableReader.StoredGeometries(source, header, ".ydr", modelHash);
        if (all.Count == 0)
        {
            error = "Le modèle source n'a aucune partie lisible.";
            return null;
        }
        int top = all.Min(g => g.Level);
        var shown = all.Where(g => g.Level == top && g.Vertices is not null && g.VertexAt >= 0 && g.VertexCount > 0).ToList();
        if (shown.Count == 0)
        {
            error = "Les sommets de ce modèle sont dans un format que NodeRunner ne sait pas encore modifier.";
            return null;
        }
        var wall = MostUsed(shown, upright: true) ?? shown[0];
        var floor = MostUsed(shown, upright: false) ?? wall;

        var content = (byte[])source.Clone();
        DrawablePatch.HideTriangles(content, all.SelectMany(g => Enumerable.Range(0, g.Indices.Length / 3).Select(t => (g.IndicesAt, t))));
        var additions = new List<DrawableAppend.Addition>();
        foreach (var (geometry, group) in new[] { (wall, faces.Where(f => !f.Floor).ToList()), (floor, faces.Where(f => f.Floor).ToList()) })
        {
            if (group.Count == 0)
                continue;
            var vertices = new List<DrawableReader.VertexValues>();
            var indices = new List<ushort>();
            foreach (var face in group)
            {
                int first = vertices.Count;
                for (int k = 0; k < 4; k++)
                    vertices.Add(new DrawableReader.VertexValues(face.Corners[k], face.Normal, face.TexCoords[k], Vector4.One, new Vector4(face.Across, 1f)));
                indices.AddRange([(ushort)first, (ushort)(first + 1), (ushort)(first + 2), (ushort)first, (ushort)(first + 2), (ushort)(first + 3)]);
            }
            // Walls and floor in one material when the source has only one: both groups go into the same geometry.
            if (additions.FirstOrDefault(a => a.Geometry == geometry) is { } existing)
            {
                additions.Remove(existing);
                var more = DrawableAppend.Make(content, geometry, vertices, [.. indices]);
                if (more is null)
                    continue;
                int offset = existing.Vertices.Length / geometry.Stride;
                additions.Add(existing with
                {
                    Vertices = [.. existing.Vertices, .. more.Vertices],
                    Indices = [.. existing.Indices, .. more.Indices.Select(i => (ushort)(i + offset))],
                });
                continue;
            }
            if (DrawableAppend.Make(content, geometry, vertices, [.. indices]) is not { } addition)
            {
                error = "Les sommets de ce modèle sont dans un format que NodeRunner ne sait pas encore modifier.";
                return null;
            }
            additions.Add(addition);
        }
        try
        {
            var (newHeader, newContent) = DrawableAppend.Apply(header, content, DrawableAppend.SystemLength(header), all, additions);
            var points = faces.SelectMany(f => f.Corners).ToList();
            foreach (var a in additions)
                DrawableAppend.Grow(newContent, a.Geometry, points);
            return (newHeader, newContent);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // The geometry with the largest area of upright (or upward) triangles; null when none has any.
    private static DrawableReader.StoredGeometry? MostUsed(List<DrawableReader.StoredGeometry> geometries, bool upright)
    {
        DrawableReader.StoredGeometry? best = null;
        float bestArea = 0;
        foreach (var g in geometries)
        {
            float area = 0;
            for (int t = 0; t * 3 + 2 < g.Indices.Length; t++)
            {
                var a = g.Positions[g.Indices[t * 3]];
                var n = Vector3.Cross(g.Positions[g.Indices[t * 3 + 1]] - a, g.Positions[g.Indices[t * 3 + 2]] - a);
                float length = n.Length();
                if (length < 1e-8f)
                    continue;
                float z = n.Z / length;
                if (upright ? MathF.Abs(z) < 0.2f : z > 0.8f)
                    area += length * 0.5f;
            }
            if (area > bestArea)
                (best, bestArea) = (g, area);
        }
        return best;
    }
}
