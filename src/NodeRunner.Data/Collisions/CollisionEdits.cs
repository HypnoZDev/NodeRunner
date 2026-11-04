using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeRunner.Data.Collisions;

// Changes made by hand to one collision file: polygons removed and new materials, numbered as in the game's file
// (CollisionMesh.Polygons), so they can be applied again to the original at any time.
public sealed class CollisionFileEdit
{
    public HashSet<int> Removed { get; init; } = [];

    // Polygon -> material index of materials.dat.
    public Dictionary<int, byte> Materials { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Removed.Count == 0 && Materials.Count == 0;

    // The mesh as the game will see it: removed polygons left out, new materials applied.
    public CollisionMesh Apply(CollisionMesh mesh)
    {
        if (IsEmpty || mesh.Polygons.Count != mesh.TriangleCount)
            return mesh;
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
        return new CollisionMesh
        {
            BoundsMin = mesh.BoundsMin,
            BoundsMax = mesh.BoundsMax,
            Vertices = mesh.Vertices,
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
