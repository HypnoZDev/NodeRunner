using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Audio;

namespace NodeRunner.Data.Native.Audio;

// Writes a game data file (.dat151.rel) holding interior (44) and room (46) items, laid out like the game's files:
// rooms then interiors, an empty name table, the index sorted as the game sorts it, then the offsets of the fields that
// name other items (room list of an interior, ambient zone of a room), and no wave pack.
public static class GameDataWriter
{
    public static byte[] Write(IReadOnlyList<InteriorAudioSettings> interiors, IReadOnlyList<RoomAudioSettings> rooms)
    {
        var data = new List<byte>(new byte[4]);
        var index = new List<(uint Name, int Offset, int Length)>();
        var references = new List<int>();

        foreach (var room in rooms)
        {
            int at = data.Count;
            var item = new byte[GameDataReader.RoomLength];
            item[0] = GameDataReader.RoomType;
            Put(item, 4, room.Flags);
            Put(item, 8, room.RoomName);
            Put(item, 12, room.AmbientZone);
            Put(item, 16, room.InteriorType);
            Put(item, 20, room.ReverbSmall);
            Put(item, 24, room.ReverbMedium);
            Put(item, 28, room.ReverbLarge);
            Put(item, 32, room.RoomTone);
            Put(item, 36, room.RainType);
            Put(item, 40, room.ExteriorAudibility);
            Put(item, 44, room.RoomOcclusionDamping);
            Put(item, 48, room.NonMarkedPortalOcclusion);
            Put(item, 52, room.DistanceFromPortalForOcclusion);
            Put(item, 56, room.DistanceFromPortalFadeDistance);
            Put(item, 60, room.WeaponMetrics);
            Put(item, 64, room.WallaSoundSet);
            data.AddRange(item);
            index.Add((room.NameHash, at, item.Length));
            references.Add(at + 12);
        }
        foreach (var interior in interiors)
        {
            int at = data.Count;
            var item = new byte[20 + interior.Rooms.Count * 4];
            item[0] = GameDataReader.InteriorType;
            Put(item, 4, interior.Flags);
            Put(item, 8, interior.WallaSoundSet);
            Put(item, 12, interior.Reflections);
            Put(item, 16, (uint)interior.Rooms.Count);
            for (int r = 0; r < interior.Rooms.Count; r++)
            {
                Put(item, 20 + r * 4, interior.Rooms[r]);
                references.Add(at + 20 + r * 4);
            }
            data.AddRange(item);
            index.Add((interior.NameHash, at, item.Length));
        }

        var file = new List<byte>(data.Count + index.Count * 12 + references.Count * 4 + 32);
        Add(file, GameDataReader.GameDataType);
        Add(file, data.Count);
        file.AddRange(data);
        Add(file, 4);
        Add(file, 0);
        Add(file, index.Count);
        foreach (var (name, offset, length) in index.OrderBy(i => BitOperations.RotateRight(i.Name, 8)))
        {
            Add(file, (int)name);
            Add(file, offset);
            Add(file, length);
        }
        Add(file, references.Count);
        foreach (int offset in references)
            Add(file, 8 + offset); // From the start of the file.
        Add(file, 0);
        return file.ToArray();
    }

    private static void Put(byte[] item, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(at), value);

    private static void Put(byte[] item, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(at), value);

    private static void Add(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }
}
