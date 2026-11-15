using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;

namespace NodeRunner.Data.Audio;

// Audio settings (.dat151 items 44 and 46) for an interior the game data does not fully cover: a new interior, or rooms
// added or renamed. Rooms keep the settings found under their name (in the interior itself, or in the one it was
// duplicated from); the others get neutral values, close to the most common ones of the game.
public static class InteriorAudioBuilder
{
    public const uint DefaultWallaSoundSet = 0xD4855127;
    public const uint DefaultInteriorFlags = 0xAAAAA044;

    // Two bits per flag, "10" = not set.
    public const uint UnsetFlags = 0xAAAAAAAA;

    public static readonly uint NullSound = Joaat.Hash("null_sound");

    // Every room of the interior has settings in the game data.
    public static bool IsCovered(Archetype interior, AudioGameData data) =>
        data.Interior(interior.NameHash) is not null
        && interior.InteriorRooms.All(room => data.RoomOf(interior.NameHash, room.Name) is not null);

    public static (InteriorAudioSettings Interior, List<RoomAudioSettings> Rooms) Build(Archetype interior, AudioGameData? data)
    {
        uint source = data?.Interior(interior.NameHash) is not null ? interior.NameHash : interior.CopiedFromHash;
        var template = source != 0 ? data?.Interior(source) : null;
        if (template is null)
            source = 0;
        var rooms = new List<RoomAudioSettings>();
        var names = new HashSet<uint>();
        for (int i = 0; i < interior.InteriorRooms.Count; i++)
        {
            string name = interior.InteriorRooms[i].Name;
            uint roomName = Joaat.Hash(name);
            if (!names.Add(roomName))
                continue; // The game matches rooms by name: one item per name.
            var existing = source != 0 ? data!.RoomOf(source, name) : null;
            // A room of the interior itself keeps its item name, so that it replaces the game's item.
            uint item = existing is not null && source == interior.NameHash ? existing.NameHash : RoomItemName(interior.NameHash, name);
            rooms.Add(existing is not null
                ? existing with { NameHash = item, File = "" }
                : i == 0 ? Limbo(item, roomName) : Default(item, roomName));
        }
        var settings = new InteriorAudioSettings(interior.NameHash, template?.Flags ?? DefaultInteriorFlags,
            template?.WallaSoundSet ?? DefaultWallaSoundSet, template?.Reflections ?? 0, rooms.Select(r => r.NameHash).ToList(), "");
        return (settings, rooms);
    }

    public static uint RoomItemName(uint interior, string room) => Joaat.Hash($"nrs_{interior:x8}_{room}");

    // Room 0 (limbo), as in the game: no room tone, outside fully heard.
    public static RoomAudioSettings Limbo(uint item, uint roomName) =>
        new(item, UnsetFlags, roomName, 0, 0, 0, 0, 0, NullSound, 0, 1, 0, 0, 0, 0, 0, DefaultWallaSoundSet, "");

    public static RoomAudioSettings Default(uint item, uint roomName) =>
        new(item, UnsetFlags, roomName, 0, 0, 0.1f, 0.15f, 0.05f, NullSound, 0, 0, 0, 1, 5, 15, 0, DefaultWallaSoundSet, "");
}
