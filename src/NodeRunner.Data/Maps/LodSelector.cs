using System.Numerics;

namespace NodeRunner.Data.Maps;

/// <summary>
/// Chooses which entities to draw for a camera position, following the game's level-of-detail tree:
/// from far away a single simplified model (SLOD) stands for a whole district; closer, it is replaced
/// by its children (LOD), then by theirs (HD). The draw distance of a top-level entity decides whether its
/// tree is shown; below it, only the parents' switch distances count (as in the game: a child's own draw
/// distance is measured from another point and may be a little shorter, which would leave holes).
/// </summary>
public static class LodSelector
{
    /// <summary>What one selection needs: the map, the camera, and the entities chosen so far.</summary>
    private readonly record struct Pass(MapLibrary Library, Vector3 Camera, float Multiplier, int? Hour, List<MapEntity> Selected);

    /// <param name="distanceMultiplier">1 = the game's distances; larger shows details from further away.</param>
    /// <param name="hour">Hour of the day (0 to 23) for the objects shown at certain hours only; null = all shown.</param>
    public static List<MapEntity> Select(MapLibrary library, Vector3 camera, float distanceMultiplier, int? hour = null)
    {
        var pass = new Pass(library, camera, distanceMultiplier, hour, new List<MapEntity>());

        foreach (var root in library.FarRoots)
            Visit(pass, root, isRoot: true);

        float nearRadius = MapLibrary.NearRootDistance * distanceMultiplier;
        foreach (var entity in library.EntitiesAround(new Vector2(camera.X, camera.Y), nearRadius))
        {
            if (entity.Parent is null && library.DrawDistance(entity) <= MapLibrary.NearRootDistance)
                Visit(pass, entity, isRoot: true);
        }

        return pass.Selected;
    }

    private static void Visit(Pass pass, MapEntity entity, bool isRoot)
    {
        if (entity.IsDeleted)
            return; // Deleted in the editor (with its children).
        if (entity.ParentIndex >= 0 && entity.Parent is null)
            return; // Its parent ymap is loaded by scripts only (or is a mission variant): not part of the default map.

        if (entity.IsInterior)
        {
            // The interior's own draw distance is short (often 20-50 m) and measured from its origin, while a
            // tunnel spans hundreds of metres: each object inside is shown within its own draw distance instead.
            foreach (var inside in entity.InteriorEntities)
            {
                if (!inside.IsWindowView && IsDrawn(pass, inside) && InRange(pass, inside))
                    pass.Selected.Add(inside);
            }
            foreach (var set in entity.EntitySets)
            {
                if (!set.IsActive)
                    continue; // Hidden until activated, as in the game.
                foreach (var inside in set.Entities)
                {
                    if (IsDrawn(pass, inside) && InRange(pass, inside))
                        pass.Selected.Add(inside);
                }
            }
            return;
        }

        float distance = Vector3.Distance(pass.Camera, entity.Position);
        float drawDistance = pass.Library.DrawDistance(entity) * pass.Multiplier;
        if (isRoot && drawDistance > 0f && distance > drawDistance)
            return;

        if (entity.Children.Count > 0 && HandsOver(pass, entity, distance))
        {
            foreach (var child in entity.Children)
                Visit(pass, child, isRoot: false);
            return;
        }
        if (IsChildlessStandIn(pass, entity, distance))
            return;

        if (IsDrawn(pass, entity))
            pass.Selected.Add(entity);
    }

    /// <summary>
    /// A simplified model (LOD, SLOD) whose file declares no children, within its switch distance: a stand-in
    /// for detailed objects that are gone, never shown up close. A few were left by the DLC packs (e.g. two SLOD2
    /// of hei_vb_lod.ymap, which would cover the Vespucci beach with a blurry model). Stand-ins whose declared
    /// children are not loaded (maps loaded by scripts only) are still shown, so as not to leave holes.
    /// </summary>
    private static bool IsChildlessStandIn(Pass pass, MapEntity entity, float distance) =>
        entity.LodLevel is not (LodLevel.Hd or LodLevel.OrphanHd)
        && entity.ChildCount == 0 && entity.Children.Count == 0
        && entity.ChildLodDistance > 0f && distance < entity.ChildLodDistance * pass.Multiplier;

    /// <summary>False for the stand-ins the game only draws in shadows or reflections.</summary>
    private static bool IsDrawn(Pass pass, MapEntity entity)
    {
        if (!entity.IsDrawnInMainView)
            return false;
        var archetype = pass.Library.GetArchetype(entity.ArchetypeHash);
        return archetype is null
            || !archetype.IsShadowOrReflectionProxy && (pass.Hour is not { } hour || archetype.IsVisibleAt(hour));
    }

    /// <summary>
    /// Whether the children replace the entity: within its switch distance, or as soon as a child (or an object
    /// inside a child interior) is within its own draw distance. The switch distance is measured from the
    /// entity's origin, which can be far from the area it covers: the sea floor off Vespucci beach hangs from
    /// vb_lod_rv_slod4, whose origin is 1.9 km away by the port, so its children would never be shown there.
    /// It is not set at all (0) on the simplified shells of every interior (tunnels, car parks...): metro
    /// tunnels are chains of short interiors, and measured from the interior's origin, the shell of the next
    /// section would stay in place and block the tunnel.
    /// </summary>
    private static bool HandsOver(Pass pass, MapEntity entity, float distance)
    {
        if (entity.ChildLodDistance > 0f && distance < entity.ChildLodDistance * pass.Multiplier)
            return true;

        foreach (var child in entity.Children)
        {
            if (child.IsDeleted)
                continue;
            if (child.IsInterior ? child.InteriorEntities.Any(inside => !inside.IsWindowView && InRange(pass, inside)) : InRange(pass, child))
                return true;
        }
        return false;
    }

    private static bool InRange(Pass pass, MapEntity entity)
    {
        float drawDistance = pass.Library.DrawDistance(entity) * pass.Multiplier;
        return drawDistance <= 0f || Vector3.Distance(pass.Camera, entity.Position) <= drawDistance;
    }
}
