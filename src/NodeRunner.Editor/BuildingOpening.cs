using System.Numerics;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Models;

namespace NodeRunner.Editor;

// An interior (MLO) behind a door-sized opening cut through a building (a door drawn on its wall): where the interior
// goes, its room, its portal on the opening and the door attached to it. Values measured on the game's interiors:
// doors to the outside are attached to a portal going from the room to limbo, the door's origin in the portal's plane.
public static class BuildingOpening
{
    // Portal of a door to the outside: closes with the door (0x40), light passes through it (0x2000); the most common
    // setting of the game's doors attached to their portal.
    public const uint DoorPortalFlags = 0x2040;

    // The most common flags of the game's interior doors.
    public const uint DoorFlags = 0x180000;

    // A door that opens: dynamic (0x20000) and with door physics (0x4000000), as 72 % of the game's interior doors.
    public const uint OpeningDoorTypeFlags = 0x20000 | 0x4000000;

    public static readonly Vector3 DefaultRoomSize = new(6f, 6f, 3.5f);

    // Rooms of the game reach a little under their floor.
    private const float RoomBelowFloor = 0.5f;

    public sealed record Plan(Vector3 Position, Quaternion Orientation, InteriorRoom Room, InteriorPortal Portal, MapEntity? Door);

    // opening: in world space, facing out of the building. roomSize: across, into the building, high. The interior's
    // origin is the middle of the opening's bottom; its +Y goes into the building.
    public static Plan Make(Opening opening, Vector3 roomSize, uint doorHash = 0, Archetype? door = null, string? doorName = null)
    {
        var orientation = InteriorCreation.FacingAway(-opening.Normal);
        float hw = opening.Width * 0.5f, height = opening.Height;
        var room = new InteriorRoom(new Vector3(-roomSize.X * 0.5f, 0, -RoomBelowFloor), new Vector3(roomSize.X * 0.5f, roomSize.Y, roomSize.Z))
        {
            Name = "room1",
            Flags = InteriorCreation.DefaultRoomFlags,
        };
        var portal = InteriorCreation.FacingAsTheGame(new InteriorPortal(1, 0, door is null ? 0u : DoorPortalFlags)
        {
            Corners = [new(hw, 0, height), new(-hw, 0, height), new(-hw, 0, 0), new(hw, 0, 0)],
        }, [room, room]);
        return new Plan(opening.Bottom, orientation, room, portal, door is null ? null : DoorIn(doorHash, door, doorName, portalIndex: 0));
    }

    // The door's definition in the interior (its space as in Make): a swinging door's hinge on the side its leaf comes
    // from, centred in the opening, bottom on the floor, in the portal's plane; attached to the portal, in no room.
    public static MapEntity DoorIn(uint doorHash, Archetype door, string? doorName, int portalIndex)
    {
        var shape = DoorShape.Of(door, doorName);
        float x = shape.Kind == DoorKind.Hinged
            ? -shape.Side * shape.Width * 0.5f
            : -(door.BoundsMin.X + door.BoundsMax.X) * 0.5f;
        float y = -(door.BoundsMin.Y + door.BoundsMax.Y) * 0.5f;
        return new MapEntity
        {
            ArchetypeHash = doorHash,
            Position = new Vector3(x, y, -door.BoundsMin.Z),
            StoredRotation = Quaternion.Identity,
            Flags = DoorFlags,
            Guid = (uint)Random.Shared.Next(1, int.MaxValue),
            LodDistance = door.LodDistance > 0 ? Math.Min(door.LodDistance, 500f) : 100f,
            LodLevel = LodLevel.OrphanHd,
            InteriorRoom = -1,
            InteriorPortal = portalIndex,
        };
    }

    // The width a door fills in an opening.
    public static float WidthOf(Archetype door, string? doorName) => DoorShape.Of(door, doorName).Width;

    // A game door that opens (swinging, door physics) whose width is the closest to width; null when none.
    public static (uint Hash, string Name)? ChooseDoor(IEnumerable<(uint Hash, string? Name, Archetype Type)> types, float width) =>
        types.Where(t => t.Name is not null && t.Type.Kind == ArchetypeKind.Base && Doors.IsDoor(t.Name)
                && (t.Type.Flags & OpeningDoorTypeFlags) == OpeningDoorTypeFlags && DoorShape.Of(t.Type, t.Name).Kind == DoorKind.Hinged
                && t.Type.BoundsMax.Z - t.Type.BoundsMin.Z is > 1.9f and < 2.8f)
            .OrderBy(t => MathF.Abs(DoorShape.Of(t.Type, t.Name).Width - width))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => ((uint, string)?)(t.Hash, t.Name!))
            .FirstOrDefault();

    // The opening in the space of the model of a building placed by toModel (its inverse transform).
    public static Opening ToModel(Opening opening, Matrix4x4 toModel)
    {
        var normal = Vector3.TransformNormal(opening.Normal, toModel);
        normal = new Vector3(normal.X, normal.Y, 0);
        return opening with
        {
            Bottom = Vector3.Transform(opening.Bottom, toModel),
            Normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : opening.Normal,
        };
    }

    // The room's box in world space (axes of the interior), for finding what is in the way (occluders, collisions).
    public static (Matrix4x4 ToLocal, Vector3 Min, Vector3 Max) RoomVolume(Plan plan)
    {
        var transform = Matrix4x4.CreateFromQuaternion(plan.Orientation) * Matrix4x4.CreateTranslation(plan.Position);
        Matrix4x4.Invert(transform, out var toLocal);
        return (toLocal, plan.Room.BoundsMin, plan.Room.BoundsMax);
    }
}
