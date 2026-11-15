using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Resources;

// Builds a Meta resource (.ytyp, .ymap...) from the XML written by CodeWalker and Sollumz: one element per field, named
// like it (or "hash_XXXXXXXX"), values in a "value" attribute, x / y / z / w attributes or text, arrays as <Item>
// lists or as text. Places and types of the fields come from the game's own descriptions (MetaSchema).
public static class MetaXml
{
    // Field types of the descriptions.
    private const byte Boolean = 0x01;
    private const byte Structure = 0x05;
    private const byte StructurePointer = 0x07;
    private const byte SignedByte = 0x10;
    private const byte UnsignedByte = 0x11;
    private const byte SignedShort = 0x12;
    private const byte UnsignedShort = 0x13;
    private const byte SignedInt = 0x14;
    private const byte UnsignedInt = 0x15;
    private const byte Float = 0x21;
    private const byte FloatXyz = 0x33;
    private const byte FloatXyzw = 0x34;
    private const byte ArrayOfChars = 0x40;
    private const byte CharPointer = 0x44;
    private const byte Hash = 0x4A;
    private const byte ArrayOfBytes = 0x50;
    private const byte Array = 0x52;
    private const byte DataBlockPointer = 0x59;
    private const byte ByteEnum = 0x60;
    private const byte IntEnum = 0x62;
    private const byte IntFlags1 = 0x63;
    private const byte ShortFlags = 0x64;
    private const byte IntFlags2 = 0x65;

    private const uint ArrayInfo = 0x100; // Entry describing the items of the array field next to it.
    private const uint StringBlock = 0x10;
    private const uint PointerBlock = 0x07;
    private const uint ByteBlock = 0x11;

    private readonly record struct Entry(uint Name, int Offset, byte Type, short RefIndex, uint RefKey);

    public static MetaDocument Build(XElement root, MetaSchema schema)
    {
        var builder = new Builder(schema);
        uint type = Resolve(root.Name.LocalName, h => schema.Structure(h) is not null);
        var info = builder.Require(type, root.Name.LocalName);
        var (block, offset) = builder.Reserve(type, info.Size);
        builder.WriteStructure(root, info, block, offset);
        return builder.Finish(block);
    }

    // An element or structure name: "hash_XXXXXXXX" (a name CodeWalker does not know), or the name itself.
    public static uint NameHash(string name) =>
        IsHashName(name, out uint hash) ? hash : Joaat.Hash(name);

    private static bool IsHashName(string name, out uint hash) =>
        uint.TryParse(name.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) ? name.AsSpan(5) : [], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);

    // The game hashes some names as written (case kept), others in lower case: both are tried against what is known.
    internal static uint Resolve(string name, Func<uint, bool> known)
    {
        if (IsHashName(name, out uint hash))
            return hash;
        uint lower = Joaat.Hash(name);
        if (known(lower))
            return lower;
        uint literal = LiteralHash(name);
        return known(literal) ? literal : lower;
    }

    private static uint LiteralHash(string text)
    {
        uint hash = 0;
        foreach (char c in text)
        {
            hash += (byte)c;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    // A hash value: empty = 0, "hash_XXXXXXXX" or "0x..." (hexadecimal), else the hash of the text.
    public static uint HashValue(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return hex;
        return NameHash(text);
    }

    private sealed class BlockData(uint structure)
    {
        public uint Structure { get; } = structure;
        public byte[] Data = new byte[64];
        public int Length;
    }

    private sealed class Builder(MetaSchema schema)
    {
        private readonly List<BlockData> _blocks = [];
        private readonly Dictionary<uint, int> _open = new();
        private readonly List<uint> _structures = [];
        private readonly List<uint> _enums = [];
        private readonly Dictionary<uint, Entry[]> _entries = new();

        public MetaDocument.StructureInfo Require(uint hash, string name) =>
            schema.Structure(hash) ?? throw new InvalidDataException($"structure {name} inconnue (absente des fichiers du jeu)");

        // Room for items of a structure (or values of a basic type), in the open block of that kind while it stays
        // under the usual block size.
        public (int Block, int Offset) Reserve(uint structure, int length)
        {
            if (!_open.TryGetValue(structure, out int index) || _blocks[index].Length > 0 && _blocks[index].Length + length > MetaDocument.MaxBlockLength)
            {
                index = _blocks.Count;
                _blocks.Add(new BlockData(structure));
                _open[structure] = index;
            }
            var block = _blocks[index];
            int offset = block.Length;
            if (offset + length > block.Data.Length)
                System.Array.Resize(ref block.Data, Math.Max(block.Data.Length * 2, offset + length));
            block.Length += length;
            return (index, offset);
        }

        private Span<byte> At(int block, int offset, int length) => _blocks[block].Data.AsSpan(offset, length);

        private Entry[] Entries(MetaDocument.StructureInfo info)
        {
            if (_entries.TryGetValue(info.NameHash, out var entries))
                return entries;
            entries = new Entry[info.Entries.Length / 16];
            for (int i = 0; i < entries.Length; i++)
            {
                var e = info.Entries.AsSpan(i * 16);
                entries[i] = new Entry(BinaryPrimitives.ReadUInt32LittleEndian(e), BinaryPrimitives.ReadInt32LittleEndian(e[4..]), e[8],
                    BinaryPrimitives.ReadInt16LittleEndian(e[10..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
            }
            _entries[info.NameHash] = entries;
            if (!_structures.Contains(info.NameHash))
                _structures.Add(info.NameHash);
            foreach (var entry in entries)
            {
                if (entry.Type is ByteEnum or IntEnum or IntFlags1 or IntFlags2 or ShortFlags && !_enums.Contains(entry.RefKey))
                    _enums.Add(entry.RefKey);
            }
            return entries;
        }

        public void WriteStructure(XElement element, MetaDocument.StructureInfo info, int block, int offset)
        {
            var entries = Entries(info);
            var fields = new Dictionary<uint, XElement>();
            foreach (var child in element.Elements())
                fields.TryAdd(Resolve(child.Name.LocalName, h => entries.Any(e => e.Name == h)), child);

            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Name == ArrayInfo || !fields.TryGetValue(entry.Name, out var field))
                    continue;
                int at = offset + entry.Offset;
                try
                {
                    WriteField(field, entry, ItemsEntry(entries, i), block, at);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException($"<{field.Name.LocalName}> : {ex.Message}");
                }
            }
        }

        // The description of the items of an array field (the entry it names, or the one before it).
        private static Entry ItemsEntry(Entry[] entries, int index)
        {
            var entry = entries[index];
            if (entry.RefIndex >= 0 && entry.RefIndex < entries.Length && entries[entry.RefIndex].Name == ArrayInfo)
                return entries[entry.RefIndex];
            return index > 0 ? entries[index - 1] : default;
        }

        private void WriteField(XElement field, Entry entry, Entry items, int block, int at)
        {
            switch (entry.Type)
            {
                case Boolean:
                    At(block, at, 1)[0] = ParseBoolean(Value(field)) ? (byte)1 : (byte)0;
                    break;
                case SignedByte:
                case UnsignedByte:
                    At(block, at, 1)[0] = (byte)ParseInteger(Value(field));
                    break;
                case SignedShort:
                case UnsignedShort:
                    BinaryPrimitives.WriteUInt16LittleEndian(At(block, at, 2), (ushort)ParseInteger(Value(field)));
                    break;
                case SignedInt:
                case UnsignedInt:
                    BinaryPrimitives.WriteUInt32LittleEndian(At(block, at, 4), (uint)ParseInteger(Value(field)));
                    break;
                case Float:
                    BinaryPrimitives.WriteSingleLittleEndian(At(block, at, 4), ParseFloat(Value(field)));
                    break;
                case FloatXyz:
                case FloatXyzw:
                {
                    var v = Vector(field, entry.Type == FloatXyzw ? 4 : 3, 0f);
                    for (int c = 0; c < v.Length; c++)
                        BinaryPrimitives.WriteSingleLittleEndian(At(block, at + c * 4, 4), v[c]);
                    break;
                }
                case Hash:
                    BinaryPrimitives.WriteUInt32LittleEndian(At(block, at, 4), HashValue(field.Value));
                    break;
                case ByteEnum:
                    At(block, at, 1)[0] = (byte)EnumValue(Value(field), entry, flags: false);
                    break;
                case IntEnum:
                    BinaryPrimitives.WriteInt32LittleEndian(At(block, at, 4), (int)EnumValue(Value(field), entry, flags: false));
                    break;
                case IntFlags1:
                case IntFlags2:
                    BinaryPrimitives.WriteInt32LittleEndian(At(block, at, 4), (int)EnumValue(Value(field), entry, flags: true));
                    break;
                case ShortFlags:
                    BinaryPrimitives.WriteInt16LittleEndian(At(block, at, 2), (short)EnumValue(Value(field), entry, flags: false));
                    break;
                case ArrayOfChars:
                {
                    var bytes = Encoding.ASCII.GetBytes(field.Value.Trim());
                    int length = Math.Min(bytes.Length, (int)(entry.RefKey & 0xFFFF) - 1);
                    if (length > 0)
                        bytes.AsSpan(0, length).CopyTo(At(block, at, length));
                    break;
                }
                case ArrayOfBytes:
                    WriteInlineArray(field, entry, items, block, at);
                    break;
                case CharPointer:
                    WriteString(field.Value.Trim(), block, at);
                    break;
                case DataBlockPointer:
                {
                    var bytes = Tokens(field.Value).Select(t => byte.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                    if (bytes.Length == 0)
                        break;
                    var (dataBlock, _) = Reserve(ByteBlock, bytes.Length);
                    _open.Remove(ByteBlock); // The pointer names the whole block.
                    bytes.CopyTo(At(dataBlock, 0, bytes.Length));
                    BinaryPrimitives.WriteUInt64LittleEndian(At(block, at, 8), MetaDocument.Pointer(dataBlock, 0));
                    break;
                }
                case Structure:
                    WriteStructure(field, Require(entry.RefKey, $"0x{entry.RefKey:X8}"), block, at);
                    break;
                case Array:
                    WriteArray(field, items, block, at);
                    break;
                default:
                    // StructurePointer outside an array: not written by CodeWalker either.
                    break;
            }
        }

        private void WriteString(string text, int block, int at)
        {
            if (text.Length == 0)
                return;
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            var (stringBlock, offset) = Reserve(StringBlock, bytes.Length);
            bytes.CopyTo(At(stringBlock, offset, bytes.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(At(block, at, 8), MetaDocument.Pointer(stringBlock, offset));
            BinaryPrimitives.WriteUInt16LittleEndian(At(block, at + 8, 2), (ushort)text.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(At(block, at + 10, 2), (ushort)bytes.Length);
        }

        private void WriteArray(XElement field, Entry items, int block, int at)
        {
            int count;
            (int Block, int Offset) start;
            switch (items.Type)
            {
                case Structure:
                {
                    var list = field.Elements("Item").ToList();
                    count = list.Count;
                    // Empty arrays of types no game file describes (containerLods) need no description.
                    if (count == 0)
                        return;
                    var info = Require(items.RefKey, $"0x{items.RefKey:X8}");
                    start = Reserve(items.RefKey, count * info.Size);
                    for (int i = 0; i < count; i++)
                        WriteStructure(list[i], info, start.Block, start.Offset + i * info.Size);
                    break;
                }
                case StructurePointer:
                {
                    var list = field.Elements("Item").ToList();
                    count = list.Count;
                    if (count == 0)
                        return;
                    start = Reserve(PointerBlock, count * 8);
                    for (int i = 0; i < count; i++)
                    {
                        string? type = list[i].Attribute("type")?.Value;
                        if (string.IsNullOrEmpty(type) || type == "NULL")
                            continue;
                        uint hash = Resolve(type, h => schema.Structure(h) is not null);
                        var info = Require(hash, type);
                        var item = Reserve(hash, info.Size);
                        WriteStructure(list[i], info, item.Block, item.Offset);
                        BinaryPrimitives.WriteUInt64LittleEndian(At(start.Block, start.Offset + i * 8, 8), MetaDocument.Pointer(item.Block, item.Offset));
                    }
                    break;
                }
                default:
                {
                    int size = ValueSize(items.Type);
                    var values = Values(field, items.Type);
                    count = items.Type == FloatXyz ? values.Count / 4 : values.Count;
                    if (count == 0)
                        return;
                    start = Reserve(items.Type, count * size);
                    for (int i = 0; i < values.Count; i++)
                        WriteValue(items.Type, values[i], start.Block, start.Offset + i * (items.Type == FloatXyz ? 4 : size));
                    break;
                }
            }
            BinaryPrimitives.WriteUInt64LittleEndian(At(block, at, 8), MetaDocument.Pointer(start.Block, start.Offset));
            BinaryPrimitives.WriteUInt16LittleEndian(At(block, at + 8, 2), (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(At(block, at + 10, 2), (ushort)count);
        }

        // A fixed-size array inside the structure (ArrayOfBytes: RefKey items of the type described next to it).
        private void WriteInlineArray(XElement field, Entry entry, Entry items, int block, int at)
        {
            int count = (int)(entry.RefKey & 0xFFFF);
            if (items.Type is SignedByte or UnsignedByte or SignedShort or UnsignedShort or SignedInt or UnsignedInt or Float)
            {
                var values = Values(field, items.Type);
                int size = ValueSize(items.Type);
                for (int i = 0; i < Math.Min(count, values.Count); i++)
                    WriteValue(items.Type, values[i], block, at + i * size);
                return;
            }
            // Bytes in hexadecimal, back to back.
            string hex = string.Concat(field.Value.Where(Uri.IsHexDigit));
            for (int i = 0; i < Math.Min(count, hex.Length / 2); i++)
                At(block, at + i, 1)[0] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static int ValueSize(byte type) => type switch
        {
            SignedByte or UnsignedByte or Boolean => 1,
            SignedShort or UnsignedShort => 2,
            SignedInt or UnsignedInt or Float or Hash => 4,
            FloatXyz => 16,
            _ => throw new InvalidDataException($"type de tableau 0x{type:X2} non pris en charge"),
        };

        // The values of an array of a basic type: <Item> elements (vectors: 4 values each, a missing w being NaN as in
        // the game's portal corners), or text.
        private static List<string> Values(XElement field, byte type)
        {
            var values = new List<string>();
            var items = field.Elements("Item").ToList();
            if (type == FloatXyz)
            {
                foreach (var item in items)
                    values.AddRange(Vector(item, 4, float.NaN).Select(f => f.ToString("R", CultureInfo.InvariantCulture)));
                return values;
            }
            if (items.Count > 0)
            {
                foreach (var item in items)
                    values.Add(type == Hash ? item.Value.Trim() : Value(item));
                return values;
            }
            values.AddRange(Tokens(field.Value));
            return values;
        }

        private void WriteValue(byte type, string text, int block, int at)
        {
            switch (type)
            {
                case Boolean:
                    At(block, at, 1)[0] = ParseBoolean(text) ? (byte)1 : (byte)0;
                    break;
                case SignedByte:
                case UnsignedByte:
                    At(block, at, 1)[0] = (byte)ParseInteger(text);
                    break;
                case SignedShort:
                case UnsignedShort:
                    BinaryPrimitives.WriteUInt16LittleEndian(At(block, at, 2), (ushort)ParseInteger(text));
                    break;
                case SignedInt:
                case UnsignedInt:
                    BinaryPrimitives.WriteUInt32LittleEndian(At(block, at, 4), (uint)ParseInteger(text));
                    break;
                case Hash:
                    BinaryPrimitives.WriteUInt32LittleEndian(At(block, at, 4), HashValue(text));
                    break;
                case Float:
                case FloatXyz:
                    BinaryPrimitives.WriteSingleLittleEndian(At(block, at, 4), ParseFloat(text));
                    break;
            }
        }

        private long EnumValue(string text, Entry entry, bool flags)
        {
            long value = 0;
            foreach (string token in Tokens(text, '|'))
            {
                if (TryParseInteger(token, out long number))
                {
                    value |= number;
                    continue;
                }
                long named = Named(entry.RefKey, Resolve(token, h => Named(entry.RefKey, h) is not null))
                             ?? throw new InvalidDataException($"valeur « {token} » inconnue");
                value |= flags ? 1L << (int)named : named;
                if (!flags)
                    break;
            }
            return value;
        }

        private long? Named(uint enumHash, uint name)
        {
            if (schema.Enum(enumHash) is not { } info)
                return null;
            for (int i = 0; i + 8 <= info.Entries.Length; i += 8)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(info.Entries.AsSpan(i)) == name)
                    return BinaryPrimitives.ReadInt32LittleEndian(info.Entries.AsSpan(i + 4));
            }
            return null;
        }

        public MetaDocument Finish(int rootBlock)
        {
            var document = schema.Template.CreateEmpty();
            foreach (var block in _blocks)
                document.Blocks.Add(new MetaDocument.Block(block.Structure, block.Data.AsSpan(0, block.Length).ToArray()));
            document.RootBlock = rootBlock;
            foreach (uint structure in _structures)
                document.Structures.Add(schema.Structure(structure)!);
            foreach (uint e in _enums)
            {
                if (schema.Enum(e) is { } info)
                    document.Enums.Add(info);
            }
            return document;
        }
    }

    internal static string Value(XElement element) => element.Attribute("value")?.Value ?? element.Value;

    internal static IEnumerable<string> Tokens(string text, params char[] more) =>
        text.Split([' ', '\t', '\r', '\n', ',', .. more], StringSplitOptions.RemoveEmptyEntries);

    internal static float[] Vector(XElement element, int count, float missing)
    {
        string[] axes = ["x", "y", "z", "w"];
        var values = new float[count];
        System.Array.Fill(values, 0f);
        if (element.Attribute("x") is not null)
        {
            for (int c = 0; c < count; c++)
                values[c] = element.Attribute(axes[c]) is { } a ? ParseFloat(a.Value) : c == 3 ? missing : 0f;
            return values;
        }
        var tokens = Tokens(element.Value).ToList();
        for (int c = 0; c < count; c++)
            values[c] = c < tokens.Count ? ParseFloat(tokens[c]) : c == 3 ? missing : 0f;
        return values;
    }

    internal static bool ParseBoolean(string text)
    {
        text = text.Trim();
        if (bool.TryParse(text, out bool value))
            return value;
        return TryParseInteger(text, out long number) ? number != 0 : throw new FormatException($"« {text} » n'est pas vrai / faux");
    }

    internal static long ParseInteger(string text) =>
        TryParseInteger(text, out long value) ? value : throw new FormatException($"« {text.Trim()} » n'est pas un nombre entier");

    internal static bool TryParseInteger(string text, out long value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        // Some exporters write whole numbers as decimals ("1.0").
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == Math.Floor(d))
        {
            value = (long)d;
            return true;
        }
        return false;
    }

    internal static float ParseFloat(string text) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new FormatException($"« {text.Trim()} » n'est pas un nombre");
}
