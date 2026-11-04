using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Finds the collision placed in a part of the world, for editing tools ("put on the ground"): map collision files
// (standard version) and the collision of the interiors (MLO) placed there.
internal sealed class CollisionQuery
{
    private const int CacheSize = 24;

    // Interiors are found by their position: some (tunnels) reach this far from it.
    private const float InteriorSearchRadius = 400f;

    private readonly LinkedList<(string Path, CollisionMesh? Mesh)> _cache = new();
    private CollisionCatalog? _catalog;

    public List<string> Errors { get; } = new();

    // edits: hand changes to the collision (removed surfaces are not ground anymore).
    public List<PlacedCollision> CollisionIn(WorldLoader loader, Vector3 min, Vector3 max, CollisionEdits? edits = null)
    {
        Errors.Clear();
        var result = new List<PlacedCollision>();
        if (loader.Collisions is not { } catalog || loader.Maps is not { } library)
            return result;
        if (catalog != _catalog)
        {
            _cache.Clear();
            _catalog = catalog;
        }

        foreach (var file in catalog.FilesIn(min, max))
        {
            if (Read(catalog, file.Path) is { } mesh)
                result.Add(new PlacedCollision(edits?.Apply(file.Path, mesh) ?? mesh, Matrix4x4.Identity));
        }

        var center = new Vector2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f);
        foreach (var interior in library.EntitiesAround(center, InteriorSearchRadius))
        {
            if (!interior.IsInterior || catalog.FindInteriorFile(interior.ArchetypeHash) is not { } path)
                continue;
            if (GroundSnap.WorldBox(interior, library.GetArchetype(interior.ArchetypeHash)) is not { } box
                || box.Min.X > max.X || box.Max.X < min.X || box.Min.Y > max.Y || box.Max.Y < min.Y
                || box.Min.Z > max.Z || box.Max.Z < min.Z)
                continue;
            if (Read(catalog, path) is { } mesh)
                result.Add(new PlacedCollision(mesh, interior.Transform));
        }
        return result;
    }

    private CollisionMesh? Read(CollisionCatalog catalog, string path)
    {
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Path != path)
                continue;
            _cache.Remove(node);
            _cache.AddFirst(node);
            return node.Value.Mesh;
        }

        CollisionMesh? mesh = null;
        try
        {
            mesh = catalog.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
        }
        _cache.AddFirst((path, mesh));
        if (_cache.Count > CacheSize)
            _cache.RemoveLast();
        return mesh;
    }
}
