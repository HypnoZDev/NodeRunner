using System.Numerics;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.NavMeshes;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// Added buildings standing on the navmesh, deleted buildings leaving walkable floors in the air: the export ships
// neither navmesh file.
internal static partial class ExportChecker
{
    // Objects looked at, at most (each one reads the polygons around it).
    private const int MaxNavMeshObjects = 500;

    // Highest polygons of a deleted building checked against the ground left.
    private const int FloorsChecked = 3;

    private static void CheckNavMesh(WorldLoader world, HashNames names, List<MapEntity> added,
        List<(MapEntity, Archetype?)> deletedScenery, List<CollisionFileCut> cuts, List<ExportCheckItem> items)
    {
        if (world.NavMeshes is not { } catalog || world.Maps is not { } library || added.Count + deletedScenery.Count == 0)
            return;
        var tiles = new Dictionary<NavMeshTileKey, NavMeshTile?>();
        List<NavMeshTile> TilesOf(MapEntity entity, Vector3 min, Vector3 max)
        {
            var list = new List<NavMeshTile>();
            var (worldMin, worldMax) = NavMeshCheck.WorldBox(entity.Transform, min, max);
            foreach (var key in NavMeshCheck.Tiles(worldMin, worldMax))
            {
                if (!tiles.TryGetValue(key, out var tile))
                {
                    try
                    {
                        tile = catalog.ReadTile(key);
                    }
                    catch (InvalidDataException)
                    {
                        tile = null;
                    }
                    tiles[key] = tile;
                }
                if (tile is not null)
                    list.Add(tile);
            }
            return list;
        }
        string Where(MapEntity e) => $"{names.Describe(e.ArchetypeHash)} ({e.Position.X:0.0}, {e.Position.Y:0.0}, {e.Position.Z:0.0})";

        var blocking = new List<MapEntity>();
        foreach (var entity in added.Take(MaxNavMeshObjects))
        {
            var archetype = library.GetArchetype(entity.ArchetypeHash);
            if (!NavMeshCheck.BlocksPeds(archetype) || GroundSnap.LocalBox(archetype) is not var (min, max))
                continue;
            if (ModelTriangles(world, entity, archetype!) is { } triangles
                && NavMeshCheck.CrossedArea(TilesOf(entity, min, max), entity.Transform, min, max, triangles) >= NavMeshCheck.MinCrossedArea)
                blocking.Add(entity);
        }
        if (blocking.Count > 0)
        {
            int n = blocking.Count;
            items.Add(new ExportCheckItem(false,
                n == 1 ? L("1 bâtiment ajouté sur la zone où marchent les PNJ") : L($"{n} bâtiments ajoutés sur la zone où marchent les PNJ"),
                Limit(blocking.Select(Where).ToList()),
                L("Le jeu guide les PNJ avec le navmesh, calculé d'avance sans ces objets : ils les traverseront ou s'y bloqueront. ")
                + L("Les petits objets (props) sont contournés, pas les bâtiments. Sans gêne si aucun PNJ ne passe là ; sinon, ")
                + L("le navmesh de la zone est à refaire dans un autre outil (NodeRunner ne le modifie pas encore)."),
                Entities: blocking.Take(MaxDetails).ToList()));
        }

        // The ground as it will be in game, without the deleted buildings' collision.
        var edits = new CollisionEdits();
        foreach (var cut in cuts)
            edits.SetRemoved(cut.File.Path, cut.Polygons, true);
        var query = new CollisionQuery();
        var floating = new List<string>();
        foreach (var (entity, archetype) in deletedScenery.Take(MaxNavMeshObjects))
        {
            if (world.Collisions is null || GroundSnap.LocalBox(archetype) is not var (min, max))
                continue;
            foreach (var centre in NavMeshCheck.CentresInside(TilesOf(entity, min, max), entity.Transform, min, max).Take(FloorsChecked))
            {
                // Down to the ground far below, not only around the polygon.
                var below = query.CollisionIn(world, centre - new Vector3(1f, 1f, 200f), centre + new Vector3(1f, 1f, 0.5f), edits);
                if (below.Count == 0)
                    continue;
                if (PlacementCheck.GroundGap(below, centre) is not { } gap || gap > NavMeshCheck.FloatingGap)
                {
                    floating.Add(Where(entity));
                    break;
                }
            }
        }
        if (floating.Count > 0)
        {
            int n = floating.Count;
            items.Add(new ExportCheckItem(false,
                n == 1 ? L("1 bâtiment supprimé laisse des PNJ marcher en l'air") : L($"{n} bâtiments supprimés laissent des PNJ marcher en l'air"),
                Limit(floating),
                L("Le navmesh (surface où marchent les PNJ) garde les étages et le toit de ces bâtiments : des PNJ peuvent y ")
                + L("apparaître et tomber, et ils contourneront leur ancienne place au sol. Sans gêne si aucun PNJ ne passe là ; ")
                + L("sinon, posez un objet à la place, ou refaites le navmesh de la zone dans un autre outil.")));
        }
    }

    // The triangles of an object's model in world positions, or null when it cannot be read.
    private static List<Vector3>? ModelTriangles(WorldLoader world, MapEntity entity, Archetype archetype)
    {
        if (world.Models?.Locate(archetype) is not { } location || world.ModelReader is not { } reader)
            return null;
        try
        {
            if (reader.ReadModels(location.Path).GetValueOrDefault(location.ModelHash) is not { } model)
                return null;
            var transform = entity.Transform;
            var triangles = new List<Vector3>(model.TriangleCount * 3);
            foreach (var mesh in model.Meshes)
            {
                foreach (ushort index in mesh.Indices)
                    triangles.Add(Vector3.Transform(mesh.Positions[index], transform));
            }
            return triangles;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
