using System.Numerics;
using NodeRunner.Data.Maps;

namespace NodeRunner.Editor;

// Health of an interior (MLO) definition: what breaks it in game (errors) and what often does (warnings). Reads only,
// never changes anything.
public static class MloCheck
{
    public enum Kind
    {
        // Errors.
        PortalUnknownRoom,
        PortalCorners,
        PortalFlat,
        PortalTwisted,
        EntityUnknownRoom,
        EntityNoRoom,

        // Warnings (each one is found in some of the game's own interiors).
        MissingModel,
        RoomWithoutPortal,
        NoExteriorPortal,
        RoomBadBox,
        RoomHuge,
        PortalDuplicate,
        EntitySetEmpty,
        EntitySetDuplicate,
        TooManyRooms,
    }

    public static bool IsError(Kind kind) => kind < Kind.MissingModel;

    // What a line points to: Index = room, portal or entity set number; Entity = the object (definition), if any.
    public sealed record Issue(Kind Kind, int Index = -1, MapEntity? Entity = null, float Value = 0);

    // The collision files keep a room number on 5 bits.
    public const int MaxCollisionRooms = 31;
    public const float TwistTolerance = 0.1f;
    public const float HugeRoomSize = 500f;

    public static List<Issue> Check(Archetype interior, Func<uint, bool> modelExists)
    {
        var issues = new List<Issue>();
        var rooms = interior.InteriorRooms;
        var portals = interior.InteriorPortals;
        bool ValidRoom(int r) => r >= 0 && r < rooms.Count;

        var linked = new bool[rooms.Count];
        bool exterior = false;
        for (int p = 0; p < portals.Count; p++)
        {
            var portal = portals[p];
            if (!ValidRoom(portal.RoomFrom) || !ValidRoom(portal.RoomTo))
            {
                issues.Add(new Issue(Kind.PortalUnknownRoom, p));
                continue;
            }
            linked[portal.RoomFrom] = linked[portal.RoomTo] = true;
            exterior |= portal.RoomFrom == 0 || portal.RoomTo == 0;
            if (portal.Corners.Count != 4)
                issues.Add(new Issue(Kind.PortalCorners, p, Value: portal.Corners.Count));
            else if (Twist(portal.Corners) is var (area, twist))
            {
                if (area < 0.01f)
                    issues.Add(new Issue(Kind.PortalFlat, p));
                else if (twist > TwistTolerance)
                    issues.Add(new Issue(Kind.PortalTwisted, p, Value: twist));
            }
            for (int q = 0; q < p; q++)
                if (portals[q].SameAs(portal) || SameOpening(portals[q], portal))
                {
                    issues.Add(new Issue(Kind.PortalDuplicate, p, Value: q));
                    break;
                }
        }

        for (int r = 1; r < rooms.Count; r++)
        {
            var room = rooms[r];
            var size = room.BoundsMax - room.BoundsMin;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
                issues.Add(new Issue(Kind.RoomBadBox, r));
            else if (size.X > HugeRoomSize || size.Y > HugeRoomSize || size.Z > HugeRoomSize)
                issues.Add(new Issue(Kind.RoomHuge, r, Value: MathF.Max(size.X, MathF.Max(size.Y, size.Z))));
            if (!linked[r])
                issues.Add(new Issue(Kind.RoomWithoutPortal, r));
        }
        if (rooms.Count > 1 && !exterior)
            issues.Add(new Issue(Kind.NoExteriorPortal));
        if (rooms.Count - 1 > MaxCollisionRooms)
            issues.Add(new Issue(Kind.TooManyRooms, Value: rooms.Count - 1));

        var missing = new HashSet<uint>();
        void CheckEntity(MapEntity entity, bool inSet)
        {
            if (entity.IsDeleted)
                return;
            if (!modelExists(entity.ArchetypeHash) && missing.Add(entity.ArchetypeHash))
                issues.Add(new Issue(Kind.MissingModel, Entity: entity));
            if (entity.InteriorPortal >= 0)
            {
                if (entity.InteriorPortal >= portals.Count)
                    issues.Add(new Issue(Kind.EntityUnknownRoom, entity.InteriorPortal, entity));
                return;
            }
            if (entity.InteriorRoom < 0)
            {
                // The game only draws the objects listed in a room (or a portal).
                if (!inSet)
                    issues.Add(new Issue(Kind.EntityNoRoom, Entity: entity));
                return;
            }
            // No check of the position: a room's box only tells the game where the player is, its objects may
            // stick out of it (the game's own interiors do it all the time).
            if (!ValidRoom(entity.InteriorRoom))
                issues.Add(new Issue(Kind.EntityUnknownRoom, entity.InteriorRoom, entity));
        }
        foreach (var entity in interior.InteriorEntities)
            CheckEntity(entity, inSet: false);

        var names = new HashSet<uint>();
        for (int s = 0; s < interior.InteriorEntitySets.Count; s++)
        {
            var set = interior.InteriorEntitySets[s];
            if (!set.Entities.Any(e => !e.IsDeleted))
                issues.Add(new Issue(Kind.EntitySetEmpty, s));
            if (!names.Add(set.NameHash))
                issues.Add(new Issue(Kind.EntitySetDuplicate, s));
            foreach (var entity in set.Entities)
                CheckEntity(entity, inSet: true);
        }
        return issues;
    }

    // Area of the 4 corners and how far the 4th one is from the plane of the 3 others.
    private static (float Area, float Twist) Twist(IReadOnlyList<Vector3> c)
    {
        var cross = Vector3.Cross(c[1] - c[0], c[2] - c[0]) + Vector3.Cross(c[2] - c[0], c[3] - c[0]);
        float area = cross.Length() / 2;
        if (area < 1e-6f)
            return (area, 0);
        var normal = Vector3.Normalize(cross);
        float twist = 0;
        var center = (c[0] + c[1] + c[2] + c[3]) / 4;
        foreach (var corner in c)
            twist = MathF.Max(twist, MathF.Abs(Vector3.Dot(corner - center, normal)));
        return (area, twist);
    }

    // Same two rooms (either way) and the same corners in any order.
    private static bool SameOpening(InteriorPortal a, InteriorPortal b) =>
        ((a.RoomFrom == b.RoomFrom && a.RoomTo == b.RoomTo) || (a.RoomFrom == b.RoomTo && a.RoomTo == b.RoomFrom))
        && a.Corners.Count == b.Corners.Count && a.Corners.Count > 0
        && a.Corners.All(x => b.Corners.Any(y => Vector3.DistanceSquared(x, y) < 0.0001f));
}
