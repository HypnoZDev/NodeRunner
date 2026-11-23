using System.Buffers.Binary;
using NodeRunner.Data.Audio;

namespace NodeRunner.Data.Native.Audio;

// Reader for the sounds files (.dat54.rel). Instead of reading the 35 kinds of sounds, it uses the two tables at the
// end of the file: the positions of every reference to another sound (hash table) and of every bank reference (pack
// table; in a simple sound, the wave name follows the bank).
// Layout: type (54), data length, data; name table length + content; item count, then per item its name hash, offset
// in the data and length; hash table count + positions in the file; pack table count + positions. An item starts with
// its type byte.
public sealed class SoundTreeReader : ISoundTreeReader
{
    internal const int SoundsType = 54;
    internal const byte SimpleSoundType = 12;

    public IEnumerable<(uint Name, SoundNode Sound)> Read(byte[] data)
    {
        var span = data.AsSpan();
        if (span.Length < 16 || I32(span, 0) != SoundsType)
            throw new InvalidDataException("Ce n'est pas un fichier de sons .dat54.");
        int dataLength = I32(span, 4);
        int at = 8 + dataLength;
        Check(span, at, 4);
        at += 4 + I32(span, at);
        Check(span, at, 4);
        int count = I32(span, at);
        at += 4;
        Check(span, at, count * 12L);

        var items = new (uint Name, int Offset, int Length)[count];
        for (int i = 0; i < count; i++, at += 12)
        {
            items[i] = (U32(span, at), I32(span, at + 4), I32(span, at + 8));
            if (items[i].Offset < 0 || items[i].Length < 1 || items[i].Offset + (long)items[i].Length > dataLength)
                throw new InvalidDataException($"Élément {i} hors des données.");
        }
        var byOffset = items.OrderBy(i => i.Offset).ToArray();
        var offsets = byOffset.Select(i => i.Offset).ToArray();
        var children = new List<uint>?[count];
        var banks = new (uint Bank, uint Wave)[count];

        // The item holding this position of the file, as an index in byOffset, or -1.
        int Owner(int position)
        {
            int inData = position - 8;
            int k = Array.BinarySearch(offsets, inData);
            if (k < 0)
                k = ~k - 1;
            return k >= 0 && inData + 4 <= byOffset[k].Offset + byOffset[k].Length ? k : -1;
        }

        Check(span, at, 4);
        int hashCount = I32(span, at);
        at += 4;
        Check(span, at, hashCount * 4L);
        for (int i = 0; i < hashCount; i++, at += 4)
        {
            int position = I32(span, at);
            if (Owner(position) is var k and >= 0)
                (children[k] ??= []).Add(U32(span, position));
        }
        Check(span, at, 4);
        int packCount = I32(span, at);
        at += 4;
        Check(span, at, packCount * 4L);
        for (int i = 0; i < packCount; i++, at += 4)
        {
            int position = I32(span, at);
            if (Owner(position) is not (var k and >= 0))
                continue;
            bool simple = span[8 + byOffset[k].Offset] == SimpleSoundType && position + 8 <= 8 + byOffset[k].Offset + byOffset[k].Length;
            banks[k] = (U32(span, position), simple ? U32(span, position + 4) : 0);
        }

        var result = new (uint, SoundNode)[count];
        for (int k = 0; k < byOffset.Length; k++)
        {
            var item = byOffset[k];
            result[k] = (item.Name, new SoundNode(span[8 + item.Offset], children[k]?.ToArray() ?? [], banks[k].Bank, banks[k].Wave));
        }
        return result;
    }

    private static void Check(ReadOnlySpan<byte> span, int at, long length)
    {
        if (at < 0 || at + length > span.Length)
            throw new InvalidDataException("Fichier tronqué.");
    }

    private static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
}
