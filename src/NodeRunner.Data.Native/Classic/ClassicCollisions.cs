using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Classic;

// The box and sphere around a model, as its collision gives them.
public readonly record struct ClassicBounds(string Model, Vector3 Min, Vector3 Max, Vector3 Center, float Radius);

// Collision files of the first trilogy (".col"): one entry per model, one after the other. Each starts with a
// four-letter version ("COLL" GTA III / Vice City, "COL2" / "COL3" / "COL4" San Andreas), its size, the model name
// (22 bytes, then a model number on 2 bytes from COL2 on) and the bounds: radius, centre, box for COLL; box, centre,
// radius for the later versions. Then the shapes:
// - COLL: counted lists one after the other: spheres (radius, centre, surface), lines, boxes (min, max, surface),
//   vertices (3 floats), faces (3 vertex numbers on 4 bytes, surface);
// - COL2 and later: counts (spheres, boxes, faces, lines), flags, then offsets (from the byte after the four letters) of
//   spheres (centre, radius, surface), boxes, lines, vertices (3 numbers on 2 bytes, in 128ths of a metre; as many as
//   the faces use) and faces (3 vertex numbers on 2 bytes, material, light).
// A surface starts with its material number.
public static class ClassicCollisions
{
    private const uint Coll = 0x4C4C4F43, Col2 = 0x324C4F43, Col3 = 0x334C4F43, Col4 = 0x344C4F43;
    private const int HeaderSize = 8, NameSize = 24, BoundsSize = 40;
    private const int MaxCount = 100_000;

    public static List<ClassicBounds> ReadBounds(ReadOnlySpan<byte> data)
    {
        var bounds = new List<ClassicBounds>();
        foreach (var (magic, start, size) in Entries(data))
        {
            var b = data.Slice(start + HeaderSize + NameSize, BoundsSize);
            string model = Name(data.Slice(start + HeaderSize, 22));
            if (magic == Coll)
                bounds.Add(new ClassicBounds(model, Vec(b[16..]), Vec(b[28..]), Vec(b[4..]), Float(b)));
            else
                bounds.Add(new ClassicBounds(model, Vec(b), Vec(b[12..]), Vec(b[24..]), Float(b[36..])));
        }
        return bounds;
    }

    // Triangles of every model of the file, in the model's space; boxes and spheres are turned into triangles.
    // Materials hold the game's surface numbers (see Surface).
    public static List<(string Model, CollisionMesh Mesh)> ReadMeshes(ReadOnlySpan<byte> data)
    {
        var meshes = new List<(string, CollisionMesh)>();
        foreach (var (magic, start, size) in Entries(data))
        {
            var entry = data.Slice(start, HeaderSize + size);
            var builder = new Builder();
            try
            {
                if (magic == Coll)
                    ReadVersion1(entry, builder);
                else
                    ReadVersion2(entry, builder);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Damaged entry: what was read is kept.
            }
            if (builder.Mesh() is { } mesh)
                meshes.Add((Name(entry.Slice(HeaderSize, 22)), mesh));
        }
        return meshes;
    }

    private static IEnumerable<(uint Magic, int Start, int Size)> Entries(ReadOnlySpan<byte> data)
    {
        var entries = new List<(uint, int, int)>();
        int at = 0;
        while (at + HeaderSize + NameSize + BoundsSize <= data.Length)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if (magic is not (Coll or Col2 or Col3 or Col4))
                break;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (size < NameSize + BoundsSize || at + HeaderSize + size > data.Length)
                break;
            entries.Add((magic, at, (int)size));
            at += HeaderSize + (int)size;
        }
        return entries;
    }

    private static void ReadVersion1(ReadOnlySpan<byte> e, Builder builder)
    {
        int at = HeaderSize + NameSize + BoundsSize;
        int spheres = Count(e, ref at);
        for (int i = 0; i < spheres; i++, at += 20)
            builder.AddSphere(Vec(e[(at + 4)..]), Float(e[at..]), e[at + 16]);
        int lines = Count(e, ref at);
        at += lines * 24;
        int boxes = Count(e, ref at);
        for (int i = 0; i < boxes; i++, at += 28)
            builder.AddBox(Vec(e[at..]), Vec(e[(at + 12)..]), e[at + 24]);
        int vertexCount = Count(e, ref at);
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++, at += 12)
            vertices[i] = Vec(e[at..]);
        int faces = Count(e, ref at);
        for (int i = 0; i < faces; i++, at += 16)
        {
            int a = BinaryPrimitives.ReadInt32LittleEndian(e[at..]), b = BinaryPrimitives.ReadInt32LittleEndian(e[(at + 4)..]),
                c = BinaryPrimitives.ReadInt32LittleEndian(e[(at + 8)..]);
            if ((uint)a < vertexCount && (uint)b < vertexCount && (uint)c < vertexCount)
                builder.AddTriangle(vertices[a], vertices[b], vertices[c], e[at + 12]);
        }
    }

    private static void ReadVersion2(ReadOnlySpan<byte> e, Builder builder)
    {
        int h = HeaderSize + NameSize + BoundsSize;
        int spheres = BinaryPrimitives.ReadUInt16LittleEndian(e[h..]);
        int boxes = BinaryPrimitives.ReadUInt16LittleEndian(e[(h + 2)..]);
        int faces = BinaryPrimitives.ReadUInt16LittleEndian(e[(h + 4)..]);
        int sphereAt = Offset(e, h, 0), boxAt = Offset(e, h, 1), vertexAt = Offset(e, h, 3), faceAt = Offset(e, h, 4);
        for (int i = 0; i < spheres; i++)
        {
            int at = sphereAt + i * 20;
            builder.AddSphere(Vec(e[at..]), Float(e[(at + 12)..]), e[at + 16]);
        }
        for (int i = 0; i < boxes; i++)
        {
            int at = boxAt + i * 28;
            builder.AddBox(Vec(e[at..]), Vec(e[(at + 12)..]), e[at + 24]);
        }
        for (int i = 0; i < faces; i++)
        {
            var f = e.Slice(faceAt + i * 8, 8);
            builder.AddTriangle(Vertex(e, vertexAt, BinaryPrimitives.ReadUInt16LittleEndian(f)),
                Vertex(e, vertexAt, BinaryPrimitives.ReadUInt16LittleEndian(f[2..])),
                Vertex(e, vertexAt, BinaryPrimitives.ReadUInt16LittleEndian(f[4..])), f[6]);
        }
    }

    // Offsets count from the byte after the four letters.
    private static int Offset(ReadOnlySpan<byte> e, int header, int field) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(e[(header + 12 + field * 4)..]) + 4;

    private static Vector3 Vertex(ReadOnlySpan<byte> e, int start, int index)
    {
        var v = e.Slice(start + index * 6, 6);
        return new Vector3(BinaryPrimitives.ReadInt16LittleEndian(v), BinaryPrimitives.ReadInt16LittleEndian(v[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(v[4..])) / 128f;
    }

    private static int Count(ReadOnlySpan<byte> e, ref int at)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(e[at..]);
        at += 4;
        if (count < 0 || count > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(e));
        return count;
    }

    // Kind of ground of a surface number, by game (GTA III, Vice City and San Andreas number them differently).
    public static CollisionSurface Surface(ClassicGame game, byte material) => game switch
    {
        ClassicGame.SanAndreas => material switch
        {
            (>= 1 and <= 8) or 89 => CollisionSurface.Road,
            (>= 9 and <= 17) or 19 or 20 or 23 or 40 or 41 or (>= 80 and <= 84) or 87 => CollisionSurface.Vegetation,
            18 or 21 or 22 or (>= 24 and <= 37) or (>= 74 and <= 79) or 85 or 86 or 88 or (>= 96 and <= 98) => CollisionSurface.Terrain,
            38 or 39 => CollisionSurface.Water,
            >= 45 and <= 47 => CollisionSurface.Glass,
            _ => CollisionSurface.Building,
        },
        _ => material switch
        {
            1 or 5 => CollisionSurface.Road,
            2 or 25 => CollisionSurface.Vegetation,
            3 or 4 or 18 or 26 or 33 or 34 => CollisionSurface.Terrain,
            19 when game == ClassicGame.ViceCity => CollisionSurface.Water,
            7 => CollisionSurface.Glass,
            _ => CollisionSurface.Building,
        },
    };

    private sealed class Builder
    {
        private readonly List<Vector3> _vertices = [];
        private readonly List<int> _indices = [];
        private readonly List<byte> _materials = [];

        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, byte material)
        {
            _indices.AddRange([_vertices.Count, _vertices.Count + 1, _vertices.Count + 2]);
            _vertices.AddRange([a, b, c]);
            _materials.Add(material);
        }

        public void AddBox(Vector3 min, Vector3 max, byte material)
        {
            Vector3 P(int i) => new((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
            foreach (var f in faces)
            {
                AddTriangle(P(f[0]), P(f[1]), P(f[2]), material);
                AddTriangle(P(f[0]), P(f[2]), P(f[3]), material);
            }
        }

        // A sphere drawn as an octahedron.
        public void AddSphere(Vector3 center, float radius, byte material)
        {
            if (!(radius > 0f) || radius > 1000f)
                return;
            var x = Vector3.UnitX * radius;
            var y = Vector3.UnitY * radius;
            var z = Vector3.UnitZ * radius;
            foreach (var top in new[] { z, -z })
            {
                var corners = new[] { x, y, -x, -y };
                for (int i = 0; i < 4; i++)
                {
                    var (a, b) = top.Z > 0 ? (corners[i], corners[(i + 1) % 4]) : (corners[(i + 1) % 4], corners[i]);
                    AddTriangle(center + a, center + b, center + top, material);
                }
            }
        }

        public CollisionMesh? Mesh()
        {
            if (_indices.Count == 0)
                return null;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in _vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            return new CollisionMesh { BoundsMin = min, BoundsMax = max, Vertices = _vertices, Indices = _indices, Materials = _materials };
        }
    }

    private static string Name(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
    }

    private static float Float(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadSingleLittleEndian(s);

    private static Vector3 Vec(ReadOnlySpan<byte> s) => new(Float(s), Float(s[4..]), Float(s[8..]));
}
