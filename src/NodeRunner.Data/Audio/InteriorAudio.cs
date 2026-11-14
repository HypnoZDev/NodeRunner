using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Audio;

// Audio settings of an interior archetype (game data item 44 of the .dat151.rel files, named after the archetype).
public sealed record InteriorAudioSettings(uint NameHash, uint Flags, uint WallaSoundSet, uint Reflections, IReadOnlyList<uint> Rooms,
    string File);

// Audio settings of one room (item 46), listed by its interior; RoomName is the hash of the room's name in the archetype.
public sealed record RoomAudioSettings(uint NameHash, uint Flags, uint RoomName, uint AmbientZone, uint InteriorType,
    float ReverbSmall, float ReverbMedium, float ReverbLarge, uint RoomTone, uint RainType, float ExteriorAudibility,
    float RoomOcclusionDamping, float NonMarkedPortalOcclusion, float DistanceFromPortalForOcclusion,
    float DistanceFromPortalFadeDistance, uint WeaponMetrics, uint WallaSoundSet, string File);

public sealed record GameDataContent(IReadOnlyList<InteriorAudioSettings> Interiors, IReadOnlyList<RoomAudioSettings> Rooms);

public interface IAudioGameDataReader
{
    // The interior and room items of a game data file (.dat151.rel); file: shown to the user.
    GameDataContent Read(byte[] data, string file);
}

// Interior and room audio settings of every game data file, later files overriding earlier ones by name.
public sealed class AudioGameData
{
    private readonly Dictionary<uint, InteriorAudioSettings> _interiors;
    private readonly Dictionary<uint, RoomAudioSettings> _rooms;
    private readonly Dictionary<uint, string> _names;
    private readonly Dictionary<(uint Interior, uint RoomName), RoomAudioSettings> _byRoomName = new();

    private AudioGameData(Dictionary<uint, InteriorAudioSettings> interiors, Dictionary<uint, RoomAudioSettings> rooms,
        Dictionary<uint, string> names, IReadOnlyList<string> errors)
    {
        _interiors = interiors;
        _rooms = rooms;
        _names = names;
        Errors = errors;
        foreach (var interior in interiors.Values)
            foreach (uint item in interior.Rooms)
                if (rooms.TryGetValue(item, out var room))
                    _byRoomName.TryAdd((interior.NameHash, room.RoomName), room);
    }

    public IReadOnlyList<string> Errors { get; }

    public int InteriorCount => _interiors.Count;

    public InteriorAudioSettings? Interior(uint archetypeHash) => _interiors.GetValueOrDefault(archetypeHash);

    public RoomAudioSettings? Room(uint itemHash) => _rooms.GetValueOrDefault(itemHash);

    // The audio settings of the room of this interior named so (rooms are matched by name, as the game does).
    public RoomAudioSettings? RoomOf(uint archetypeHash, string roomName) =>
        _byRoomName.GetValueOrDefault((archetypeHash, Joaat.Hash(roomName)));

    // Name of an audio item, from the .nametable files the DLC packs ship beside their game data.
    public string? Name(uint hash) => _names.GetValueOrDefault(hash);

    public static AudioGameData Load(GameContent content, IAudioGameDataReader reader)
    {
        var interiors = new Dictionary<uint, InteriorAudioSettings>();
        var rooms = new Dictionary<uint, RoomAudioSettings>();
        var names = new Dictionary<uint, string>();
        var errors = new List<string>();
        foreach (var path in content.EnumerateAllVersions(".dat151.rel"))
        {
            try
            {
                if (content.ReadFile(path) is not { } data)
                    continue;
                var read = reader.Read(data, path);
                foreach (var interior in read.Interiors)
                    interiors[interior.NameHash] = interior;
                foreach (var room in read.Rooms)
                    rooms[room.NameHash] = room;
                if (content.ReadFile(path[..^".rel".Length] + ".nametable") is { } table)
                    foreach (var name in System.Text.Encoding.ASCII.GetString(table).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        names.TryAdd(Joaat.Hash(name), name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(path)} : {ex.Message}");
            }
        }
        return new AudioGameData(interiors, rooms, names, errors);
    }
}
