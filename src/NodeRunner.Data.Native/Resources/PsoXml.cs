using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace NodeRunner.Data.Native.Resources;

// Builds a PSO file (_manifest.ymf...) from the XML written by CodeWalker: one element per field, arrays of <Item>.
// Big-endian sections: PSIN (the data, in blocks of one type each, 16-byte aligned and padded with 0x70), PMAP (the
// blocks), PSCH (descriptions of the structures and enums used, copied from the game's files).
public static class PsoXml
{
    private const byte Bool = 0x00, SByte = 0x01, UByte = 0x02, SShort = 0x03, UShort = 0x04, SInt = 0x05, UInt = 0x06;
    private const byte Float = 0x07, Float2 = 0x08, Float3 = 0x09, Float4 = 0x0A, String = 0x0B, Structure = 0x0C, Array = 0x0D;
    private const byte Enum = 0x0E, Flags = 0x0F, Float3a = 0x14, Float4a = 0x15, HFloat = 0x1E, Long = 0x1F;

    private const uint ArrayInfo = 0x100; // Field describing the items of an array (or the enum of flags).
    private const uint StringBlock = 1;
    private const byte Padding = 0x70;

    public static byte[] Build(XElement root, PsoSchema schema)
    {
        var builder = new Builder(schema);
        uint type = MetaXml.Resolve(root.Name.LocalName, h => schema.Structure(h) is not null);
        var info = builder.Require(type, root.Name.LocalName);
        var (block, offset) = builder.Reserve(type, info.Length);
        builder.WriteStructure(root, info, block, offset);
        return builder.Finish(block);
    }

    private sealed class BlockData(uint type)
    {
        public uint Type { get; } = type;
        public byte[] Data = new byte[64];
        public int Length;
    }

    private sealed class Builder(PsoSchema schema)
    {
        private readonly List<BlockData> _blocks = [];
        private readonly Dictionary<uint, int> _open = new();
        private readonly List<uint> _structures = [];
        private readonly List<uint> _enums = [];

        public PsoSchema.StructureInfo Require(uint hash, string name)
        {
            var info = schema.Structure(hash) ?? throw new InvalidDataException($"structure {name} inconnue (absente des fichiers du jeu)");
            if (!_structures.Contains(hash))
                _structures.Add(hash);
            return info;
        }

        private PsoSchema.EnumInfo? EnumInfo(uint hash)
        {
            var info = schema.Enum(hash);
            if (info is not null && !_enums.Contains(hash))
                _enums.Add(hash);
            return info;
        }

        public (int Block, int Offset) Reserve(uint type, int length)
        {
            if (!_open.TryGetValue(type, out int index))
            {
                index = _blocks.Count;
                _blocks.Add(new BlockData(type));
                _open[type] = index;
            }
            var block = _blocks[index];
            int offset = block.Length;
            if (offset + length > block.Data.Length)
                System.Array.Resize(ref block.Data, Math.Max(block.Data.Length * 2, offset + length));
            block.Length += length;
            return (index, offset);
        }

        private Span<byte> At(int block, int offset, int length) => _blocks[block].Data.AsSpan(offset, length);

        private static uint Pointer(int block, int offset) => (uint)(block + 1) | (uint)offset << 12;

        public void WriteStructure(XElement element, PsoSchema.StructureInfo info, int block, int offset)
        {
            var fields = new Dictionary<uint, XElement>();
            foreach (var child in element.Elements())
                fields.TryAdd(MetaXml.Resolve(child.Name.LocalName, h => info.Fields.Any(f => f.Name == h)), child);
            foreach (var field in info.Fields)
            {
                // Enums (of values or of flags) are listed even when the value is absent, as the game's files do.
                if (field.Type == Enum)
                    EnumInfo(field.Reference);
                if (field.Name == ArrayInfo || !fields.TryGetValue(field.Name, out var value))
                    continue;
                try
                {
                    WriteField(value, info, field, block, offset + field.Offset);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException($"<{value.Name.LocalName}> : {ex.Message}");
                }
            }
        }

        private void WriteField(XElement element, PsoSchema.StructureInfo owner, PsoSchema.Field field, int block, int at)
        {
            switch (field.Type)
            {
                case Structure when field.Subtype == 0:
                    WriteStructure(element, Require(field.Reference, $"0x{field.Reference:X8}"), block, at);
                    return;
                case Structure:
                {
                    if (!element.HasElements && element.Attribute("type") is null)
                        return; // A null pointer.
                    string? typeName = element.Attribute("type")?.Value;
                    uint type = typeName is null ? field.Reference : MetaXml.Resolve(typeName, h => schema.Structure(h) is not null);
                    var info = Require(type, typeName ?? $"0x{type:X8}");
                    var item = Reserve(type, info.Length);
                    WriteStructure(element, info, item.Block, item.Offset);
                    BinaryPrimitives.WriteUInt32BigEndian(At(block, at, 4), Pointer(item.Block, item.Offset));
                    return;
                }
                case Array:
                    WriteArray(element, owner, field, block, at);
                    return;
                default:
                    WriteValue(element, owner, field, block, at);
                    return;
            }
        }

        private void WriteArray(XElement element, PsoSchema.StructureInfo owner, PsoSchema.Field field, int block, int at)
        {
            int itemIndex = (int)(field.Reference & 0xFFF);
            if (itemIndex >= owner.Fields.Length)
                throw new InvalidDataException("description de tableau invalide");
            var item = owner.Fields[itemIndex];
            var items = Items(element, item);
            int size = item.Type == Structure && item.Subtype == 0 ? Require(item.Reference, $"0x{item.Reference:X8}").Length : ItemSize(item);
            bool inline = field.Subtype is 1 or 2 or 129;
            if (inline)
            {
                int capacity = (int)(field.Reference >> 16);
                for (int i = 0; i < Math.Min(capacity, items.Count); i++)
                    WriteItem(items[i], owner, item, block, at + i * size);
                return;
            }
            if (field.Subtype is not (0 or 4))
                throw new InvalidDataException($"type de tableau {field.Subtype} non pris en charge");
            if (items.Count == 0)
                return;
            uint blockType = item.Type switch
            {
                Structure when item.Subtype == 0 => item.Reference,
                String when item.Subtype is 7 or 8 => UInt,
                _ => item.Type,
            };
            var start = Reserve(blockType, items.Count * size);
            for (int i = 0; i < items.Count; i++)
                WriteItem(items[i], owner, item, start.Block, start.Offset + i * size);
            BinaryPrimitives.WriteUInt32BigEndian(At(block, at, 4), Pointer(start.Block, start.Offset));
            BinaryPrimitives.WriteUInt16BigEndian(At(block, at + 8, 2), (ushort)items.Count);
            BinaryPrimitives.WriteUInt16BigEndian(At(block, at + 10, 2), (ushort)items.Count);
        }

        // The items of an array: <Item> elements, or values in the text for plain numbers.
        private static List<XElement> Items(XElement element, PsoSchema.Field item)
        {
            var list = element.Elements("Item").ToList();
            if (list.Count > 0 || item.Type is Structure or String or Float2 or Float3 or Float4 or Float3a or Float4a)
                return list;
            return [.. MetaXml.Tokens(element.Value).Select(t => new XElement("Item", new XAttribute("value", t)))];
        }

        private void WriteItem(XElement element, PsoSchema.StructureInfo owner, PsoSchema.Field item, int block, int at)
        {
            if (item.Type == Structure && item.Subtype == 0)
                WriteStructure(element, Require(item.Reference, $"0x{item.Reference:X8}"), block, at);
            else
                WriteField(element, owner, item, block, at);
        }

        private static int ItemSize(PsoSchema.Field f) => f.Type switch
        {
            Bool or SByte or UByte => 1,
            SShort or UShort or HFloat => 2,
            SInt or UInt or Float => 4,
            Float2 or Long => 8,
            Float3 or Float4 or Float3a or Float4a => 16,
            Enum or Flags => f.Subtype switch { 1 => 2, 2 => 1, _ => 4 },
            String => f.Subtype switch { 0 => (int)(f.Reference >> 16), 1 or 2 => 8, 3 => 16, _ => 4 },
            Structure => 8, // Pointer (inline structures: their length).
            Array => 16,
            _ => throw new InvalidDataException($"type de champ 0x{f.Type:X2} non pris en charge"),
        };

        private void WriteValue(XElement element, PsoSchema.StructureInfo owner, PsoSchema.Field field, int block, int at)
        {
            string text = MetaXml.Value(element);
            switch (field.Type)
            {
                case Bool:
                    At(block, at, 1)[0] = MetaXml.ParseBoolean(text) ? (byte)1 : (byte)0;
                    break;
                case SByte:
                case UByte:
                    At(block, at, 1)[0] = (byte)MetaXml.ParseInteger(text);
                    break;
                case SShort:
                case UShort:
                case HFloat:
                    BinaryPrimitives.WriteUInt16BigEndian(At(block, at, 2), (ushort)MetaXml.ParseInteger(text));
                    break;
                case SInt:
                case UInt:
                    BinaryPrimitives.WriteUInt32BigEndian(At(block, at, 4), (uint)MetaXml.ParseInteger(text));
                    break;
                case Long:
                    BinaryPrimitives.WriteInt64BigEndian(At(block, at, 8), MetaXml.ParseInteger(text));
                    break;
                case Float:
                    BinaryPrimitives.WriteSingleBigEndian(At(block, at, 4), MetaXml.ParseFloat(text));
                    break;
                case Float2:
                case Float3:
                case Float4:
                case Float3a:
                case Float4a:
                {
                    int count = field.Type switch { Float2 => 2, Float4 => 4, _ => 3 };
                    var v = MetaXml.Vector(element, count, 0f);
                    for (int c = 0; c < count; c++)
                        BinaryPrimitives.WriteSingleBigEndian(At(block, at + c * 4, 4), v[c]);
                    break;
                }
                case String:
                    WriteString(element.Value.Trim(), field, block, at);
                    break;
                case Enum:
                {
                    long value = 0;
                    string name = element.Value.Trim();
                    if (name.Length > 0 && !MetaXml.TryParseInteger(name, out value))
                        value = Named(EnumInfo(field.Reference), name) ?? throw new InvalidDataException($"valeur « {name} » inconnue");
                    WriteSized(field.Subtype, value, block, at);
                    break;
                }
                case Flags:
                {
                    int enumIndex = (int)(field.Reference & 0xFFF);
                    var flagsEnum = enumIndex < owner.Fields.Length && owner.Fields[enumIndex].Name == ArrayInfo
                        ? EnumInfo(owner.Fields[enumIndex].Reference)
                        : null;
                    long value = 0;
                    foreach (string token in MetaXml.Tokens(text, '|'))
                    {
                        if (MetaXml.TryParseInteger(token, out long number))
                            value |= number;
                        else
                            value |= 1L << (int)(Named(flagsEnum, token) ?? throw new InvalidDataException($"valeur « {token} » inconnue"));
                    }
                    WriteSized(field.Subtype, value, block, at);
                    break;
                }
                default:
                    throw new InvalidDataException($"type de champ 0x{field.Type:X2} non pris en charge");
            }
        }

        private void WriteSized(byte subtype, long value, int block, int at)
        {
            switch (subtype)
            {
                case 1:
                    BinaryPrimitives.WriteUInt16BigEndian(At(block, at, 2), (ushort)value);
                    break;
                case 2:
                    At(block, at, 1)[0] = (byte)value;
                    break;
                default:
                    BinaryPrimitives.WriteUInt32BigEndian(At(block, at, 4), (uint)value);
                    break;
            }
        }

        private static long? Named(PsoSchema.EnumInfo? info, string name)
        {
            if (info is null)
                return null;
            uint hash = MetaXml.Resolve(name, h => info.Values.Any(v => v.Name == h));
            foreach (var (n, value) in info.Values)
            {
                if (n == hash)
                    return value;
            }
            return null;
        }

        private void WriteString(string text, PsoSchema.Field field, int block, int at)
        {
            switch (field.Subtype)
            {
                case 0:
                {
                    var bytes = Encoding.ASCII.GetBytes(text);
                    int length = Math.Min(bytes.Length, (int)(field.Reference >> 16) - 1);
                    if (length > 0)
                        bytes.AsSpan(0, length).CopyTo(At(block, at, length));
                    break;
                }
                case 1:
                case 2:
                case 3:
                {
                    if (text.Length == 0)
                        break;
                    var bytes = Encoding.ASCII.GetBytes(text + "\0");
                    var (stringBlock, offset) = Reserve(StringBlock, bytes.Length);
                    bytes.CopyTo(At(stringBlock, offset, bytes.Length));
                    BinaryPrimitives.WriteUInt32BigEndian(At(block, at, 4), Pointer(stringBlock, offset));
                    if (field.Subtype == 3)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(At(block, at + 8, 2), (ushort)text.Length);
                        BinaryPrimitives.WriteUInt16BigEndian(At(block, at + 10, 2), (ushort)bytes.Length);
                    }
                    break;
                }
                default:
                    BinaryPrimitives.WriteUInt32BigEndian(At(block, at, 4), MetaXml.HashValue(text));
                    break;
            }
        }

        public byte[] Finish(int rootBlock)
        {
            using var output = new MemoryStream();
            var head = new byte[16];
            output.Write(head);
            var offsets = new int[_blocks.Count];
            for (int i = 0; i < _blocks.Count; i++)
            {
                while (output.Length % 16 != 0)
                    output.WriteByte(Padding);
                offsets[i] = (int)output.Length;
                output.Write(_blocks[i].Data, 0, _blocks[i].Length);
            }
            int dataLength = (int)output.Length;

            // PMAP: root block (1-based), block count, then type, offset, 0, length of each block.
            var map = new byte[16 + _blocks.Count * 16];
            BinaryPrimitives.WriteUInt32BigEndian(map, 0x504D4150);
            BinaryPrimitives.WriteInt32BigEndian(map.AsSpan(4), map.Length);
            BinaryPrimitives.WriteInt32BigEndian(map.AsSpan(8), rootBlock + 1);
            BinaryPrimitives.WriteInt16BigEndian(map.AsSpan(12), (short)_blocks.Count);
            map[14] = Padding;
            map[15] = Padding;
            for (int i = 0; i < _blocks.Count; i++)
            {
                var e = map.AsSpan(16 + i * 16);
                BinaryPrimitives.WriteUInt32BigEndian(e, _blocks[i].Type);
                BinaryPrimitives.WriteInt32BigEndian(e[4..], offsets[i]);
                BinaryPrimitives.WriteInt32BigEndian(e[12..], _blocks[i].Length);
            }
            output.Write(map);

            // PSCH: index (name hash, offset in the section), then the descriptions as the game stores them.
            var elements = _structures.Select(s => (Name: s, schema.Structure(s)!.Raw))
                .Concat(_enums.Select(e => (Name: e, schema.Enum(e)!.Raw))).ToList();
            int indexLength = 12 + elements.Count * 8;
            var section = new byte[indexLength + elements.Sum(e => e.Raw.Length)];
            BinaryPrimitives.WriteUInt32BigEndian(section, 0x50534348);
            BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(4), section.Length);
            BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(8), elements.Count);
            int position = indexLength;
            for (int i = 0; i < elements.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(12 + i * 8), elements[i].Name);
                BinaryPrimitives.WriteInt32BigEndian(section.AsSpan(16 + i * 8), position);
                elements[i].Raw.CopyTo(section, position);
                position += elements[i].Raw.Length;
            }
            output.Write(section);

            var file = output.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(file, 0x5053494E);
            BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4), dataLength);
            file.AsSpan(8, 8).Fill(Padding);
            return file;
        }
    }
}
