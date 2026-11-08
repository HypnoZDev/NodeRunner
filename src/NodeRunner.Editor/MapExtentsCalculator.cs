using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Boxes of a map file after some of its objects were moved.
public static class MapExtentsCalculator
{
    public static MapExtents Compute(MapData map, Func<uint, Archetype?> archetypes)
    {
        if (map.IsCustom)
            return ComputeExact(map, archetypes);
        var extents = MapExtents.Of(map);
        foreach (var entity in map.KeptEntities.Where(e => e.Revision > 0))
        {
            Include(ref extents, entity, archetypes);
            foreach (var inside in entity.InteriorEntities)
                Include(ref extents, inside, archetypes);
        }
        // The game's streaming boxes hold every parking spot of their map (measured: 47 884 spots, none outside).
        foreach (var spot in map.CarGenerators)
            extents = extents with { StreamingMin = Vector3.Min(extents.StreamingMin, spot.Position), StreamingMax = Vector3.Max(extents.StreamingMax, spot.Position) };
        return extents;
    }

    // Boxes computed from the objects only (a map made by the user has no values of Rockstar to keep): exactly around
    // them, as "Calculate extents" of the map tools.
    private static MapExtents ComputeExact(MapData map, Func<uint, Archetype?> archetypes)
    {
        var empty = new MapExtents(new Vector3(float.MaxValue), new Vector3(float.MinValue), new Vector3(float.MaxValue), new Vector3(float.MinValue));
        var extents = empty;
        foreach (var entity in map.KeptEntities)
        {
            Include(ref extents, entity, archetypes);
            if (!entity.IsInterior)
                continue;
            // An interior made by the user: its objects and rooms (its archetype has no box of its own).
            foreach (var inside in entity.InteriorEntities.Where(e => !e.IsDeleted))
                Include(ref extents, inside, archetypes);
            var rooms = archetypes(entity.ArchetypeHash)?.InteriorRooms ?? [];
            for (int r = 1; r < rooms.Count; r++)
            {
                var (min, max) = WorldBox(rooms[r].BoundsMin, rooms[r].BoundsMax, entity.Transform);
                var reach = new Vector3(entity.LodDistance);
                extents = new MapExtents(Vector3.Min(extents.StreamingMin, min - reach), Vector3.Max(extents.StreamingMax, max + reach),
                    Vector3.Min(extents.EntitiesMin, min), Vector3.Max(extents.EntitiesMax, max));
            }
        }
        return extents == empty ? default : extents;
    }

    private static void Include(ref MapExtents extents, MapEntity entity, Func<uint, Archetype?> archetypes)
    {
        var archetype = archetypes(entity.ArchetypeHash);
        var (min, max) = archetype is null
            ? (entity.Position, entity.Position)
            : WorldBox(archetype.BoundsMin, archetype.BoundsMax, entity.Transform);
        float drawDistance = entity.LodDistance > 0f ? entity.LodDistance : archetype?.LodDistance ?? 0f;
        var reach = new Vector3(drawDistance);
        extents = new MapExtents(
            Vector3.Min(extents.StreamingMin, min - reach),
            Vector3.Max(extents.StreamingMax, max + reach),
            Vector3.Min(extents.EntitiesMin, min),
            Vector3.Max(extents.EntitiesMax, max));
    }

    private static (Vector3 Min, Vector3 Max) WorldBox(Vector3 boxMin, Vector3 boxMax, Matrix4x4 transform)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? boxMin.X : boxMax.X, (i & 2) == 0 ? boxMin.Y : boxMax.Y, (i & 4) == 0 ? boxMin.Z : boxMax.Z);
            var p = Vector3.Transform(corner, transform);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}
