using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

/// <summary>
/// One collision file (.ybn) of the game touched by deleted buildings: the polygons to remove, and the removed triangles
/// in world positions (for the preview in the 3D view).
/// </summary>
public sealed record CollisionFileCut(CollisionFile File, IReadOnlySet<int> Polygons, IReadOnlyList<Vector3> Triangles)
{
    public string FileName => Path.GetFileName(File.Path);
    public int TriangleCount => Triangles.Count / 3;
}

/// <summary>
/// Removes the collision of deleted buildings (see <see cref="CollisionCut"/>): finds the map collision files around
/// each one (standard, vehicles "hi@" and "ma@" versions) and their polygons inside the building.
/// </summary>
public static class CollisionRemoval
{
    private static readonly CollisionKind[] Kinds = Enum.GetValues<CollisionKind>();

    /// <summary>Objects lower than this (ground slabs, decals) keep their collision: it is the ground people walk on.</summary>
    public const float MinHeight = 3f;

    /// <summary>
    /// The space of a deleted object, or null without a model box. A flat object (see <see cref="MinHeight"/>) keeps its
    /// collision (null), unless <paramref name="removeGround"/>: then everything in its box goes.
    /// </summary>
    public static CutVolume? VolumeOf(MapEntity entity, Archetype? archetype, bool removeGround = false)
    {
        if (archetype is null || archetype.BoundsMin == archetype.BoundsMax)
            return null;
        bool flat = (archetype.BoundsMax.Z - archetype.BoundsMin.Z) * entity.Scale.Z < MinHeight;
        if (flat && !removeGround)
            return null;
        return Matrix4x4.Invert(entity.Transform, out var toLocal)
            ? new CutVolume(toLocal, archetype.BoundsMin, archetype.BoundsMax, IncludesGround: flat)
            : null;
    }

    /// <summary>
    /// The files to rewrite for these deleted objects, sorted by name. Unreadable files are reported in
    /// <paramref name="errors"/>. <paramref name="removeGround"/>: the collision of deleted ground slabs goes too.
    /// </summary>
    public static List<CollisionFileCut> Plan(CollisionCatalog catalog, IEnumerable<(MapEntity Entity, Archetype? Archetype)> removed,
        List<string> errors, bool removeGround = false)
    {
        // Volumes grouped by file: a building can lie across two files, a file can hold several buildings.
        var byFile = new Dictionary<CollisionFile, List<CutVolume>>();
        var areaMin = new Vector2(float.MaxValue);
        var areaMax = new Vector2(float.MinValue);
        foreach (var (entity, archetype) in removed)
        {
            if (VolumeOf(entity, archetype, removeGround) is not { } volume || GroundSnap.WorldBox(entity, archetype) is not var (min, max))
                continue;
            areaMin = Vector2.Min(areaMin, new Vector2(min.X, min.Y));
            areaMax = Vector2.Max(areaMax, new Vector2(max.X, max.Y));
            foreach (var kind in Kinds)
            {
                foreach (var file in catalog.FilesIn(min, max, kind))
                {
                    if (!byFile.TryGetValue(file, out var volumes))
                        byFile[file] = volumes = new List<CutVolume>();
                    volumes.Add(volume);
                }
            }
        }

        var meshes = new Dictionary<CollisionFile, CollisionMesh>();
        foreach (var file in byFile.Keys)
        {
            try
            {
                if (catalog.Read(file) is { } mesh)
                    meshes[file] = mesh;
            }
            catch (InvalidDataException ex)
            {
                errors.Add($"{Path.GetFileName(file.Path)} : {ex.Message}");
            }
        }

        // Flat surfaces of the area, per kind: the ground under a building is often in another file (the road's).
        // Map collision files are in world positions.
        var ground = new Dictionary<CollisionKind, FlatSurfaces>();
        foreach (var (file, mesh) in meshes)
        {
            if (!ground.TryGetValue(file.Kind, out var surfaces))
                ground[file.Kind] = surfaces = new FlatSurfaces();
            surfaces.Add(mesh, Matrix4x4.Identity, areaMin, areaMax);
        }

        var cuts = new List<CollisionFileCut>();
        foreach (var (file, mesh) in meshes)
        {
            var polygons = CollisionCut.Select(mesh, Matrix4x4.Identity, byFile[file], ground[file.Kind]);
            if (polygons.Count == 0)
                continue;
            var triangles = new List<Vector3>();
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                if (!polygons.Contains(mesh.Polygons[t]))
                    continue;
                for (int k = 0; k < 3; k++)
                    triangles.Add(mesh.Vertices[mesh.Indices[t * 3 + k]]);
            }
            cuts.Add(new CollisionFileCut(file, polygons, triangles));
        }
        cuts.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName));
        return cuts;
    }
}
