using System.Buffers.Binary;

namespace NodeRunner.Data.Native.Resources;

// Descriptions of PSO structures and enums (section PSCH), gathered from game files: what PsoXml needs to write a PSO
// file (_manifest.ymf...) from XML. Each description is kept as stored, to be written back as it is.
public sealed class PsoSchema
{
    private const uint SchemaSection = 0x50534348; // "PSCH"

    // A structure: its fields (12 bytes each: name hash, type, subtype, offset, reference) and its length.
    public sealed record StructureInfo(uint Name, int Length, Field[] Fields, byte[] Raw);

    public readonly record struct Field(uint Name, byte Type, byte Subtype, int Offset, uint Reference);

    // An enum: name hash -> value.
    public sealed record EnumInfo(uint Name, (uint Name, int Value)[] Values, byte[] Raw);

    private readonly Dictionary<uint, StructureInfo> _structures = new();
    private readonly Dictionary<uint, EnumInfo> _enums = new();

    public int Count => _structures.Count + _enums.Count;

    public StructureInfo? Structure(uint hash) => _structures.GetValueOrDefault(hash);

    public EnumInfo? Enum(uint hash) => _enums.GetValueOrDefault(hash);

    // Adds the descriptions of a PSO file (other files are ignored).
    public void Add(ReadOnlySpan<byte> file)
    {
        for (int at = 0; at + 8 <= file.Length;)
        {
            uint ident = BinaryPrimitives.ReadUInt32BigEndian(file[at..]);
            int length = BinaryPrimitives.ReadInt32BigEndian(file[(at + 4)..]);
            if (length < 8 || at + length > file.Length)
                return;
            if (ident == SchemaSection)
            {
                AddSection(file.Slice(at, length));
                return;
            }
            at += length;
        }
    }

    private void AddSection(ReadOnlySpan<byte> section)
    {
        int count = BinaryPrimitives.ReadInt32BigEndian(section[8..]);
        for (int i = 0; i < count && 12 + i * 8 + 8 <= section.Length; i++)
        {
            uint name = BinaryPrimitives.ReadUInt32BigEndian(section[(12 + i * 8)..]);
            int at = BinaryPrimitives.ReadInt32BigEndian(section[(16 + i * 8)..]);
            if (at < 0 || at + 4 > section.Length)
                continue;
            uint head = BinaryPrimitives.ReadUInt32BigEndian(section[at..]);
            if (head >> 24 == 0)
            {
                // Structure: head (0, flag, field count), length, a word (and one more if the flag is 1), fields.
                int fieldCount = (int)(head & 0xFFFF);
                int fieldsAt = 12 + (((head >> 16) & 0xFF) == 1 ? 4 : 0);
                int size = fieldsAt + fieldCount * 12;
                if (at + size > section.Length || _structures.ContainsKey(name))
                    continue;
                var raw = section.Slice(at, size).ToArray();
                var fields = new Field[fieldCount];
                for (int f = 0; f < fieldCount; f++)
                {
                    var e = raw.AsSpan(fieldsAt + f * 12);
                    fields[f] = new Field(BinaryPrimitives.ReadUInt32BigEndian(e), e[4], e[5], BinaryPrimitives.ReadUInt16BigEndian(e[6..]),
                        BinaryPrimitives.ReadUInt32BigEndian(e[8..]));
                }
                _structures[name] = new StructureInfo(name, BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4)), fields, raw);
            }
            else
            {
                // Enum: head (1, value count), then (name hash, value) pairs.
                int valueCount = (int)(head & 0xFFFFFF);
                int size = 4 + valueCount * 8;
                if (at + size > section.Length || _enums.ContainsKey(name))
                    continue;
                var raw = section.Slice(at, size).ToArray();
                var values = new (uint, int)[valueCount];
                for (int v = 0; v < valueCount; v++)
                    values[v] = (BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4 + v * 8)), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(8 + v * 8)));
                _enums[name] = new EnumInfo(name, values, raw);
            }
        }
    }
}
