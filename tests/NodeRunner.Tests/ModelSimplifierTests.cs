using System.Numerics;
using NodeRunner.Data.Models;
using NodeRunner.Editor;

namespace NodeRunner.Tests;

public class ModelSimplifierTests
{
    // A flat 10 x 10 m square of 20 x 20 cells; split: the middle column of vertices is doubled (a texture seam).
    private static ModelMesh Grid(bool split)
    {
        const int cells = 20;
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var index = new Dictionary<(int, int, bool), int>();
        int Vertex(int x, int y, bool right)
        {
            bool seam = split && x == cells / 2;
            var key = (x, y, seam && right);
            if (!index.TryGetValue(key, out int i))
            {
                i = positions.Count;
                index[key] = i;
                positions.Add(new Vector3(x * 0.5f, y * 0.5f, 0f));
                uvs.Add(new Vector2(x / (float)cells + (seam && right ? 0.5f : 0f), y / (float)cells));
            }
            return i;
        }
        var indices = new List<ushort>();
        for (int y = 0; y < cells; y++)
        {
            for (int x = 0; x < cells; x++)
            {
                bool right = x >= cells / 2;
                int a = Vertex(x, y, right), b = Vertex(x + 1, y, right), c = Vertex(x + 1, y + 1, right), d = Vertex(x, y + 1, right);
                indices.AddRange([(ushort)a, (ushort)b, (ushort)c, (ushort)a, (ushort)c, (ushort)d]);
            }
        }
        return new ModelMesh
        {
            Positions = positions.ToArray(),
            Normals = positions.Select(_ => Vector3.UnitZ).ToArray(),
            TexCoords = uvs.ToArray(),
            Colors = positions.Select(_ => Vector4.One).ToArray(),
            Indices = indices.ToArray(),
        };
    }

    private static ModelData Model(params ModelMesh[] meshes) => new()
    {
        NameHash = 1,
        BoundsMin = Vector3.Zero,
        BoundsMax = new Vector3(10f, 10f, 1f),
        Meshes = meshes,
    };

    // Edges used by a single triangle, by position: the outline of the surface (and any crack).
    private static bool OnOutline(Vector3 p) => p.X is 0f or 10f || p.Y is 0f or 10f;

    private static List<(Vector3, Vector3)> OpenEdges(ModelMesh mesh)
    {
        var count = new Dictionary<(Vector3, Vector3), int>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                var a = mesh.Positions[mesh.Indices[i + k]];
                var b = mesh.Positions[mesh.Indices[i + (k + 1) % 3]];
                var key = a.X < b.X || a.X == b.X && a.Y < b.Y ? (a, b) : (b, a);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        }
        return count.Where(c => c.Value == 1).Select(c => c.Key).ToList();
    }

    private static float Area(ModelMesh mesh)
    {
        float area = 0f;
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Positions[mesh.Indices[i]];
            area += Vector3.Cross(mesh.Positions[mesh.Indices[i + 1]] - a, mesh.Positions[mesh.Indices[i + 2]] - a).Length() / 2f;
        }
        return area;
    }

    [Fact]
    public void A_flat_surface_loses_most_triangles_and_keeps_its_area()
    {
        var lod = ModelSimplifier.Simplify(Model(Grid(split: false)), 0.1f);
        var mesh = Assert.Single(lod.Meshes);
        Assert.True(mesh.Indices.Length / 3 <= 80, $"{mesh.Indices.Length / 3} triangles left");
        Assert.Equal(100f, Area(mesh), 1);
    }

    [Fact]
    public void A_texture_seam_does_not_open()
    {
        var lod = ModelSimplifier.Simplify(Model(Grid(split: true)), 0.1f);
        var mesh = Assert.Single(lod.Meshes);
        Assert.True(mesh.Indices.Length / 3 < 400, $"{mesh.Indices.Length / 3} triangles left");
        Assert.Equal(100f, Area(mesh), 1);
        // Closed inside: only the outline of the square is open.
        Assert.All(OpenEdges(mesh), e => Assert.True(OnOutline(e.Item1) && OnOutline(e.Item2), $"crack at {e}"));
        foreach (var uv in mesh.TexCoords)
            Assert.InRange(uv.X, 0f, 1.5f);
    }

    [Fact]
    public void Small_parts_are_left_out()
    {
        var small = new ModelMesh
        {
            Positions = [Vector3.Zero, new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0.1f, 0f)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            Colors = [Vector4.One, Vector4.One, Vector4.One],
            Indices = [0, 1, 2],
        };
        var lod = ModelSimplifier.Simplify(Model(Grid(split: false), small), 0.25f);
        Assert.Single(lod.Meshes);
    }
}
