using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Resources;

// Reader for PSO files ("PSIN"): the binary form of the game's .meta files used by some .ymt (carcols.ymt,
// carvariations.ymt...). Big-endian sections: PSIN (the data), PMAP (blocks of the data, each of one structure type),
// PSCH (the layout of every structure). Names are only hashes: the result is an XML tree named with the known names
// given, "_0x1234ABCD" for the others, so the same code reads the XML .meta files of the DLC packs.
internal static class PsoDocument
{
    private const uint DataSection = 0x5053494E;   // "PSIN"
    private const uint MapSection = 0x504D4150;    // "PMAP"
    private const uint SchemaSection = 0x50534348; // "PSCH"

    public static bool IsPso(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(data) == DataSection;

    private enum FieldType : byte
    {
        Bool = 0x00, SByte = 0x01, UByte = 0x02, SShort = 0x03, UShort = 0x04, SInt = 0x05, UInt = 0x06, Float = 0x07,
        Float2 = 0x08, Float3 = 0x09, Float4 = 0x0A, String = 0x0B, Structure = 0x0C, Array = 0x0D, Enum = 0x0E,
        Flags = 0x0F, Map = 0x10, Float3a = 0x14, Float4a = 0x15, HFloat = 0x1E, Long = 0x1F,
    }

    // Field: name hash, type, subtype (layout of arrays, strings, enums), offset in the structure, reference (array:
    // index of the field describing its items + count << 16; structure: its name hash).
    private readonly record struct Field(uint Name, FieldType Type, byte Subtype, int Offset, uint Reference);

    private sealed record Structure(int Length, Field[] Fields);

    private readonly record struct Block(uint Type, int Offset, int Length);

    private sealed class Context(byte[] data, Block[] blocks, Dictionary<uint, Structure> structures, Dictionary<uint, string> names)
    {
        public byte[] Data { get; } = data;
        public Block[] Blocks { get; } = blocks;
        public Dictionary<uint, Structure> Structures { get; } = structures;
        public string Name(uint hash) => names.TryGetValue(hash, out var name) ? name : $"_0x{hash:X8}";
    }

    // The whole file as XML, the root named after its structure.
    // aliases: names of fields whose hash is not the hash of their name.
    public static XElement Parse(byte[] data, IEnumerable<string> knownNames, IReadOnlyDictionary<uint, string>? aliases = null)
    {
        if (!IsPso(data))
            throw new InvalidDataException("Ce n'est pas un fichier PSO.");
        var names = aliases is null ? new Dictionary<uint, string>() : new Dictionary<uint, string>(aliases);
        foreach (var name in knownNames)
            names.TryAdd(Joaat.Hash(name), name);

        int? map = null, schema = null;
        for (int at = 0; at + 8 <= data.Length;)
        {
            uint ident = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at + 4));
            if (length < 8 || at + length > data.Length)
                break;
            if (ident == MapSection)
                map = at;
            else if (ident == SchemaSection)
                schema = at;
            at += length;
        }
        if (map is not { } m || schema is not { } s)
            throw new InvalidDataException("PSO : sections PMAP ou PSCH absentes.");

        // PMAP: 0x08 root block (1-based), 0x0C block count, then 16 bytes per block (type, offset, 0, length).
        int root = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(m + 8));
        int blockCount = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(m + 12));
        var blocks = new Block[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            int e = m + 16 + i * 16;
            blocks[i] = new Block(U32(data, e), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(e + 4)),
                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(e + 12)));
        }

        // PSCH: 0x08 count, then (name hash, offset from the section) per structure or enum; a structure starts with
        // (type 0 << 24 | flag << 16 | field count), its length, a word (and one more word if the flag is 1), then
        // 12 bytes per field.
        var structures = new Dictionary<uint, Structure>();
        int count = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(s + 8));
        for (int i = 0; i < count; i++)
        {
            uint name = U32(data, s + 12 + i * 8);
            int at = s + BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(s + 16 + i * 8));
            uint head = U32(data, at);
            if (head >> 24 != 0)
                continue; // An enum.
            int fieldCount = (int)(head & 0xFFFF);
            int structLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at + 4));
            int fieldsAt = at + 12 + (((head >> 16) & 0xFF) == 1 ? 4 : 0);
            var fields = new Field[fieldCount];
            for (int f = 0; f < fieldCount; f++)
            {
                int e = fieldsAt + f * 12;
                fields[f] = new Field(U32(data, e), (FieldType)data[e + 4], data[e + 5],
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(e + 6)), U32(data, e + 8));
            }
            structures[name] = new Structure(structLength, fields);
        }

        var context = new Context(data, blocks, structures, names);
        if (root < 1 || root > blocks.Length)
            throw new InvalidDataException("PSO : bloc racine introuvable.");
        var rootBlock = blocks[root - 1];
        return ReadStructure(context, context.Name(rootBlock.Type), rootBlock.Type, rootBlock.Offset, 0);
    }

    private static XElement ReadStructure(Context c, string elementName, uint type, int at, int depth)
    {
        var element = new XElement(elementName);
        if (depth > 32 || !c.Structures.TryGetValue(type, out var structure))
            return element;
        foreach (var field in structure.Fields)
        {
            if (field.Name == 0x100)
                continue; // Describes the items of an array.
            if (ReadField(c, structure, field, at + field.Offset, depth) is { } value)
            {
                value.Name = c.Name(field.Name);
                element.Add(value);
            }
        }
        return element;
    }

    // One value (field or array item), as an element named later.
    private static XElement? ReadField(Context c, Structure owner, Field field, int at, int depth)
    {
        var data = c.Data;
        if (at < 0 || at >= data.Length)
            return null;
        string? text = field.Type switch
        {
            FieldType.Bool => data[at] != 0 ? "true" : "false",
            FieldType.SByte => ((sbyte)data[at]).ToString(CultureInfo.InvariantCulture),
            FieldType.UByte => data[at].ToString(CultureInfo.InvariantCulture),
            FieldType.SShort => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            FieldType.UShort => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            FieldType.SInt => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            FieldType.UInt => $"0x{U32(data, at):X8}",
            FieldType.Float => F32(data, at),
            FieldType.Float2 => $"{F32(data, at)} {F32(data, at + 4)}",
            FieldType.Float3 or FieldType.Float3a => $"{F32(data, at)} {F32(data, at + 4)} {F32(data, at + 8)}",
            FieldType.Float4 or FieldType.Float4a => $"{F32(data, at)} {F32(data, at + 4)} {F32(data, at + 8)} {F32(data, at + 12)}",
            FieldType.Enum or FieldType.Flags => field.Subtype switch
            {
                1 => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
                2 => data[at].ToString(CultureInfo.InvariantCulture),
                _ => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)).ToString(CultureInfo.InvariantCulture),
            },
            FieldType.String => ReadString(c, field, at),
            _ => null,
        };
        if (text is not null)
            return new XElement("value", text);

        if (field.Type == FieldType.Structure)
        {
            if (field.Subtype == 3)
                return Pointer(c, U32(data, at)) is { } p ? ReadStructure(c, "value", c.Blocks[p.Block].Type, p.Offset, depth + 1) : null;
            return ReadStructure(c, "value", field.Reference, at, depth + 1);
        }
        if (field.Type != FieldType.Array)
            return null;

        int itemIndex = (int)(field.Reference & 0xFFFF);
        if (itemIndex >= owner.Fields.Length)
            return null;
        var item = owner.Fields[itemIndex];
        int itemSize = Size(c, item);
        int count, start;
        if (field.Subtype is 0 or 3)
        {
            // atArray: pointer, 0, count, capacity.
            count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 8));
            if (count == 0 || Pointer(c, U32(data, at)) is not { } p)
                return new XElement("value");
            start = p.Offset;
        }
        else
        {
            // Fixed array inside the structure: its count after the item field index.
            count = (int)(field.Reference >> 16);
            start = at;
        }
        if (itemSize <= 0 || count <= 0)
            return new XElement("value");

        var array = new XElement("value");
        if (item.Type is FieldType.UByte or FieldType.SByte or FieldType.UShort or FieldType.SShort or FieldType.SInt
            or FieldType.Float && item.Subtype == 0)
        {
            // Plain numbers: one text, as in the XML files ("content=char_array").
            var numbers = new List<string>(count);
            for (int i = 0; i < count; i++)
                numbers.Add(ReadField(c, owner, item, start + i * itemSize, depth)?.Value ?? "");
            array.Value = string.Join(" ", numbers);
            return array;
        }
        for (int i = 0; i < count; i++)
        {
            if (ReadField(c, owner, item, start + i * itemSize, depth) is { } value)
            {
                value.Name = "Item";
                array.Add(value);
            }
        }
        return array;
    }

    private static string? ReadString(Context c, Field field, int at)
    {
        var data = c.Data;
        switch (field.Subtype)
        {
            case 0:
            {
                // Characters inside the structure.
                int length = (int)(field.Reference >> 16);
                var span = data.AsSpan(at, Math.Min(length, data.Length - at));
                int end = span.IndexOf((byte)0);
                return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
            }
            case 1 or 2 or 3:
            {
                // Pointer to the characters (then a length).
                if (Pointer(c, U32(data, at)) is not { } p)
                    return "";
                int length = Math.Max(0, Math.Min(c.Blocks[p.Block].Offset + c.Blocks[p.Block].Length, data.Length) - p.Offset);
                var span = data.AsSpan(p.Offset, length);
                int end = span.IndexOf((byte)0);
                return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
            }
            default:
            {
                // Hash of a name.
                uint hash = U32(data, at);
                return hash == 0 ? "" : c.Name(hash) is var name && !name.StartsWith("_0x", StringComparison.Ordinal) ? name : $"hash_{hash:X8}";
            }
        }
    }

    private static (int Block, int Offset)? Pointer(Context c, uint pointer)
    {
        int block = (int)(pointer & 0xFFF) - 1;
        if (block < 0 || block >= c.Blocks.Length)
            return null;
        return (block, c.Blocks[block].Offset + (int)((pointer >> 12) & 0xFFFFF));
    }

    private static int Size(Context c, Field f) => f.Type switch
    {
        FieldType.Bool or FieldType.SByte or FieldType.UByte => 1,
        FieldType.SShort or FieldType.UShort or FieldType.HFloat => 2,
        FieldType.SInt or FieldType.UInt or FieldType.Float => 4,
        FieldType.Float2 or FieldType.Long => 8,
        FieldType.Float3 => 12,
        FieldType.Float4 or FieldType.Float3a or FieldType.Float4a => 16,
        FieldType.Enum or FieldType.Flags => f.Subtype switch { 1 => 2, 2 => 1, _ => 4 },
        FieldType.String => f.Subtype switch { 0 => (int)(f.Reference >> 16), 1 or 2 or 3 => 16, _ => 4 },
        FieldType.Structure => f.Subtype == 3 ? 8 : c.Structures.TryGetValue(f.Reference, out var s) ? s.Length : 0,
        FieldType.Array => 16,
        _ => 0,
    };

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

    private static string F32(byte[] data, int at) =>
        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(at)).ToString("R", CultureInfo.InvariantCulture);
}
