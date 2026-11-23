using System.Buffers.Binary;
using NodeRunner.Data.Audio;

namespace NodeRunner.Data.Native.Audio;

// Reader for the audio game data files (.dat151.rel): only the interior (44), room (46), ambient zone (37) and static
// emitter (6) items.
// Layout: type (151), data length, data; name table length + content; item count, then per item its name hash, offset
// in the data and length. An item starts with its type byte, 3 bytes of name table offset, then 32-bit flags.
public sealed class GameDataReader : IAudioGameDataReader
{
    internal const int GameDataType = 151;
    internal const byte InteriorType = 44;
    internal const byte RoomType = 46;
    internal const int RoomLength = 68;
    internal const byte AmbientZoneType = 37;
    internal const int AmbientZoneLength = 232;
    internal const byte StaticEmitterType = 6;
    internal const int StaticEmitterLength = 84;

    public GameDataContent Read(byte[] data, string file)
    {
        var span = data.AsSpan();
        if (span.Length < 16 || I32(span, 0) != GameDataType)
            throw new InvalidDataException("Ce n'est pas un fichier de données audio .dat151.");
        int dataLength = I32(span, 4);
        int at = 8 + dataLength;
        Check(span, at, 4);
        at += 4 + I32(span, at);
        Check(span, at, 4);
        int count = I32(span, at);
        at += 4;
        Check(span, at, count * 12L);

        var interiors = new List<InteriorAudioSettings>();
        var rooms = new List<RoomAudioSettings>();
        var zones = new List<AmbientZone>();
        var emitters = new List<StaticEmitter>();
        for (int i = 0; i < count; i++, at += 12)
        {
            uint name = U32(span, at);
            int offset = I32(span, at + 4), length = I32(span, at + 8);
            if (offset < 0 || length < 1 || offset + (long)length > dataLength)
                throw new InvalidDataException($"Élément {i} hors des données.");
            var item = span.Slice(8 + offset, length);
            if (item[0] == InteriorType && length >= 20)
            {
                int roomCount = I32(item, 16);
                if (roomCount < 0 || 20 + roomCount * 4L > length)
                    throw new InvalidDataException($"Intérieur 0x{name:X8} : nombre de salles invalide.");
                var list = new uint[roomCount];
                for (int r = 0; r < roomCount; r++)
                    list[r] = U32(item, 20 + r * 4);
                interiors.Add(new InteriorAudioSettings(name, U32(item, 4), U32(item, 8), U32(item, 12), list, file));
            }
            else if (item[0] == RoomType && length >= RoomLength)
            {
                rooms.Add(new RoomAudioSettings(name, U32(item, 4), U32(item, 8), U32(item, 12), U32(item, 16),
                    F32(item, 20), F32(item, 24), F32(item, 28), U32(item, 32), U32(item, 36), F32(item, 40), F32(item, 44),
                    F32(item, 48), F32(item, 52), F32(item, 56), U32(item, 60), U32(item, 64), file));
            }
            else if (item[0] == AmbientZoneType && length >= AmbientZoneLength)
                zones.Add(ReadZone(name, item, file));
            else if (item[0] == StaticEmitterType && length >= StaticEmitterLength)
                emitters.Add(new StaticEmitter(name, U32(item, 4), U32(item, 8), U32(item, 12), V3(item, 16), F32(item, 28),
                    F32(item, 32), I32(item, 36), U32(item, 48), U32(item, 52), U16(item, 80), U16(item, 82), file));
        }
        return new GameDataContent(interiors, rooms) { AmbientZones = zones, StaticEmitters = emitters };
    }

    // Item 37: flags, shape, activation then positioning volume (centre, size, offset, scale, angle; 80 bytes each),
    // settings, then the rules and the directional ambiences.
    private static AmbientZone ReadZone(uint name, ReadOnlySpan<byte> item, string file)
    {
        static AudioZoneVolume Volume(ReadOnlySpan<byte> s, int at) => new(V3(s, at), V3(s, at + 16), U16(s, at + 64));

        int ruleCount = item[230];
        int at = AmbientZoneLength + ruleCount * 4;
        if (at + 4 > item.Length)
            throw new InvalidDataException($"Zone audio 0x{name:X8} : nombre de règles invalide.");
        var rules = new uint[ruleCount];
        for (int i = 0; i < ruleCount; i++)
            rules[i] = U32(item, AmbientZoneLength + i * 4);
        int directionalCount = item[at];
        at += 4;
        var directional = new List<DirectionalAmbience>();
        for (int i = 0; i < directionalCount && at + 8 <= item.Length; i++, at += 8)
            directional.Add(new DirectionalAmbience(U32(item, at), F32(item, at + 4)));
        return new AmbientZone(name, U32(item, 4), (AudioZoneShape)item[8], Volume(item, 16), Volume(item, 96),
            F32(item, 176), F32(item, 180), F32(item, 184), U32(item, 204), U32(item, 208), U32(item, 212), U32(item, 220),
            item[228], rules, directional, file);
    }

    private static void Check(ReadOnlySpan<byte> span, int at, long length)
    {
        if (at < 0 || at + length > span.Length)
            throw new InvalidDataException("Fichier tronqué.");
    }

    private static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    private static float F32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);

    private static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    private static System.Numerics.Vector3 V3(ReadOnlySpan<byte> s, int at) => new(F32(s, at), F32(s, at + 4), F32(s, at + 8));
}
