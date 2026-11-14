using System.Buffers.Binary;
using NodeRunner.Data.Audio;

namespace NodeRunner.Data.Native.Audio;

// Reader for the audio game data files (.dat151.rel): only the interior (44) and room (46) items.
// Layout: type (151), data length, data; name table length + content; item count, then per item its name hash, offset
// in the data and length. An item starts with its type byte, 3 bytes of name table offset, then 32-bit flags.
public sealed class GameDataReader : IAudioGameDataReader
{
    internal const int GameDataType = 151;
    internal const byte InteriorType = 44;
    internal const byte RoomType = 46;
    internal const int RoomLength = 68;

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
        }
        return new GameDataContent(interiors, rooms);
    }

    private static void Check(ReadOnlySpan<byte> span, int at, long length)
    {
        if (at < 0 || at + length > span.Length)
            throw new InvalidDataException("Fichier tronqué.");
    }

    private static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    private static float F32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);
}
