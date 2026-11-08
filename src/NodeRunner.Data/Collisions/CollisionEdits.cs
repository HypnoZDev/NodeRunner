using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeRunner.Data.Collisions;

// Polygons moved by hand: their corners, and the corners of any other polygon at the same place (so that the surfaces
// around stay joined), go through Matrix (file space, row-vector convention, 16 floats).
public sealed record CollisionMove(int[] Polygons, float[] Matrix)
{
    public static CollisionMove Of(IEnumerable<int> polygons, Matrix4x4 m) => new([.. polygons.Order()],
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44]);

    [JsonIgnore]
    public Matrix4x4 Transform => Matrix.Length == 16
        ? new(Matrix[0], Matrix[1], Matrix[2], Matrix[3], Matrix[4], Matrix[5], Matrix[6], Matrix[7],
            Matrix[8], Matrix[9], Matrix[10], Matrix[11], Matrix[12], Matrix[13], Matrix[14], Matrix[15])
        : Matrix4x4.Identity;

    // The vertices after these moves, in order. Indices / polygons: those of a CollisionMesh.
    public static Vector3[] MoveVertices(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, IReadOnlyList<int> polygons,
        IEnumerable<CollisionMove> moves)
    {
        var result = vertices.ToArray();
        foreach (var move in moves)
        {
            var moved = move.Polygons.ToHashSet();
            var places = new HashSet<Vector3>();
            for (int t = 0; t < polygons.Count && t * 3 + 2 < indices.Count; t++)
            {
                if (!moved.Contains(polygons[t]))
                    continue;
                for (int k = 0; k < 3; k++)
                    places.Add(result[indices[t * 3 + k]]);
            }
            if (places.Count == 0)
                continue;
            var m = move.Transform;
            for (int i = 0; i < result.Length; i++)
            {
                if (places.Contains(result[i]))
                    result[i] = Vector3.Transform(result[i], m);
            }
        }
        return result;
    }
}

// Changes made by hand to one collision file: polygons moved, removed and given new materials, numbered as in the game's
// file (CollisionMesh.Polygons), so they can be applied again to the original at any time.
public sealed class CollisionFileEdit
{
    public HashSet<int> Removed { get; init; } = [];

    // Polygon -> material index of materials.dat.
    public Dictionary<int, byte> Materials { get; init; } = [];

    // In the order they were made.
    public List<CollisionMove> Moves { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Removed.Count == 0 && Materials.Count == 0 && Moves.Count == 0;

    // The mesh as the game will see it: polygons moved, removed ones left out, new materials applied. withMoves false:
    // the mesh is already moved (read from the moved file, exact even for the round shapes sharing a moved corner).
    public CollisionMesh Apply(CollisionMesh mesh, bool withMoves = true)
    {
        bool move = withMoves && Moves.Count > 0;
        if ((Removed.Count == 0 && Materials.Count == 0 && !move) || mesh.Polygons.Count != mesh.TriangleCount)
            return mesh;
        var vertices = move ? CollisionMove.MoveVertices(mesh.Vertices, mesh.Indices, mesh.Polygons, Moves) : mesh.Vertices;
        var indices = new List<int>(mesh.Indices.Count);
        var materials = new List<byte>(mesh.TriangleCount);
        var polygons = new List<int>(mesh.TriangleCount);
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            int polygon = mesh.Polygons[t];
            if (polygon >= 0 && Removed.Contains(polygon))
                continue;
            indices.Add(mesh.Indices[t * 3]);
            indices.Add(mesh.Indices[t * 3 + 1]);
            indices.Add(mesh.Indices[t * 3 + 2]);
            materials.Add(polygon >= 0 && Materials.TryGetValue(polygon, out byte m) ? m : mesh.Materials[t]);
            polygons.Add(polygon);
        }
        var (min, max) = (mesh.BoundsMin, mesh.BoundsMax);
        if (move)
        {
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }
        return new CollisionMesh
        {
            BoundsMin = min,
            BoundsMax = max,
            Vertices = vertices,
            Indices = indices,
            Materials = materials,
            Polygons = polygons,
        };
    }
}

// Hand changes to the collision files of a project, by file path in the game's archives.
public sealed class CollisionEdits
{
    private readonly Dictionary<string, CollisionFileEdit> _files = new(StringComparer.OrdinalIgnoreCase);

    // Incremented by every change: layers compare it to rebuild what they show.
    public int Revision { get; private set; }

    public IReadOnlyDictionary<string, CollisionFileEdit> Files => _files;

    public CollisionFileEdit? For(string path) => _files.TryGetValue(path, out var edit) && !edit.IsEmpty ? edit : null;

    public CollisionMesh Apply(string path, CollisionMesh mesh) => For(path)?.Apply(mesh) ?? mesh;

    public void SetRemoved(string path, IEnumerable<int> polygons, bool removed)
    {
        var edit = Get(path);
        foreach (int polygon in polygons)
        {
            if (removed)
                edit.Removed.Add(polygon);
            else
                edit.Removed.Remove(polygon);
        }
        Changed(path);
    }

    // material null = back to the game's material.
    public void SetMaterial(string path, int polygon, byte? material)
    {
        var edit = Get(path);
        if (material is { } m)
            edit.Materials[polygon] = m;
        else
            edit.Materials.Remove(polygon);
        Changed(path);
    }

    public void AddMove(string path, CollisionMove move)
    {
        Get(path).Moves.Add(move);
        Changed(path);
    }

    // The last time this move was made is taken back.
    public void RemoveMove(string path, CollisionMove move)
    {
        if (_files.TryGetValue(path, out var edit) && edit.Moves.LastIndexOf(move) is var i and >= 0)
            edit.Moves.RemoveAt(i);
        Changed(path);
    }

    public byte? MaterialOf(string path, int polygon) =>
        _files.TryGetValue(path, out var edit) && edit.Materials.TryGetValue(polygon, out byte m) ? m : null;

    public bool IsRemoved(string path, int polygon) =>
        _files.TryGetValue(path, out var edit) && edit.Removed.Contains(polygon);

    public void Clear()
    {
        _files.Clear();
        Revision++;
    }

    private CollisionFileEdit Get(string path)
    {
        if (!_files.TryGetValue(path, out var edit))
            _files[path] = edit = new CollisionFileEdit();
        return edit;
    }

    private void Changed(string path)
    {
        if (_files.TryGetValue(path, out var edit) && edit.IsEmpty)
            _files.Remove(path);
        Revision++;
    }

    public const string FileName = "collisions.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Saved in the project (collisions.json): the .ybn files are rebuilt from the game's files with these changes.
    public string ToJson() => JsonSerializer.Serialize(
        new SavedEdits(1, _files.Where(f => !f.Value.IsEmpty).ToDictionary(f => f.Key, f => f.Value)), JsonOptions);

    public static CollisionEdits FromJson(string json)
    {
        var saved = JsonSerializer.Deserialize<SavedEdits>(json) ?? throw new InvalidDataException("collisions.json vide.");
        var edits = new CollisionEdits();
        foreach (var (path, edit) in saved.Files ?? [])
        {
            if (!edit.IsEmpty)
                edits._files[path] = edit;
        }
        return edits;
    }

    private sealed record SavedEdits(int Format, Dictionary<string, CollisionFileEdit>? Files);
}
