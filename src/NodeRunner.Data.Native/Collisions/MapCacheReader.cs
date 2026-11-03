using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NodeRunner.Data.Collisions;

namespace NodeRunner.Data.Native.Collisions;

/// <summary>
/// Reads the map cache files of the game (gta5_cache_y.dat and those of the DLC packs).
/// Layout: a 100-byte header (version text), then text lines ending with '\n'. Some lines name a module whose
/// data follows in binary: a 32-bit length, then fixed-size records. The text ends at a zero byte.
/// Only the "BoundsStore" module is decoded: one 32-byte record per collision file
/// (name hash, box min, box max, layer).
/// </summary>
public sealed class MapCacheReader : IMapCacheReader
{
    private const int HeaderLength = 100;
    private const int BoundsRecordLength = 32;

    // Modules followed by binary data (to skip or read), as found in the game's files.
    private static readonly HashSet<string> BinaryModules = ["fwMapDataStore", "CInteriorProxy", "BoundsStore"];

    public IReadOnlyList<CollisionBox> ReadCollisionBoxes(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
            throw new InvalidDataException("Fichier cache trop court.");

        var boxes = new List<CollisionBox>();
        var line = new StringBuilder();
        for (int i = HeaderLength; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0)
                break;
            if (b != '\n')
            {
                line.Append((char)b);
                continue;
            }

            string name = line.ToString().TrimEnd('\r');
            line.Clear();
            if (!BinaryModules.Contains(name))
                continue;

            if (i + 5 > data.Length)
                throw new InvalidDataException($"Module {name} tronqué.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[(i + 1)..]);
            int start = i + 5;
            if (length < 0 || start + length > data.Length)
                throw new InvalidDataException($"Module {name} tronqué.");

            if (name == "BoundsStore")
            {
                for (int offset = start; offset + BoundsRecordLength <= start + length; offset += BoundsRecordLength)
                    boxes.Add(ReadBox(data[offset..]));
            }
            i = start + length - 1; // the loop moves to the byte after the module
        }
        return boxes;
    }

    private static CollisionBox ReadBox(ReadOnlySpan<byte> record) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(record),
        ReadVector(record[4..]),
        ReadVector(record[16..]));

    private static Vector3 ReadVector(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data),
        BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(data[8..]));
}
