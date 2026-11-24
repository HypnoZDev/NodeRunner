using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Native.Resources;

// A Meta resource written out as XML, in the layout Build reads back (and Sollumz / CodeWalker read): the file's own
// descriptions give the fields, names come from the caller since the file only keeps their hashes.
public static partial class MetaXml
{
    // names: structures, fields and enum values; values: what hash fields hold (model names...). Unknown hashes are
    // written "hash_XXXXXXXX".
    public static string Write(MetaDocument document, Func<uint, string?> names, Func<uint, string?> values)
    {
        var writer = new Writer(document, names, values);
        var xml = new XmlText();
        int root = document.RootBlock;
        if (root < 0 || root >= document.Blocks.Count)
            throw new InvalidDataException("bloc principal introuvable");
        uint type = document.Blocks[root].Structure;
        string name = writer.Name(type);
        xml.Open(name, document.Name is { Length: > 0 } n ? $"name=\"{XmlText.Escape(n)}\"" : null);
        writer.WriteFields(xml, type, root, 0);
        xml.Close(name);
        return xml.ToString();
    }

    private sealed class Writer(MetaDocument document, Func<uint, string?> names, Func<uint, string?> values)
    {
        private readonly Dictionary<uint, Entry[]> _entries = new();

        public string Name(uint hash) => names(hash) ?? HashName(hash);

        private static string HashName(uint hash) => $"hash_{hash:X8}";

        // Written back through HashValue, which hashes in lower case: a name hashed otherwise stays a hash.
        private string HashText(uint hash)
        {
            if (hash == 0)
                return "";
            foreach (string? name in new[] { values(hash), names(hash) })
            {
                if (name is { Length: > 0 } && Joaat.Hash(name) == hash)
                    return name;
            }
            return HashName(hash);
        }

        private Entry[]? Entries(uint structure)
        {
            if (_entries.TryGetValue(structure, out var entries))
                return entries;
            if (document.FindStructure(structure) is not { } info)
                return null;
            entries = new Entry[info.Entries.Length / 16];
            for (int i = 0; i < entries.Length; i++)
            {
                var e = info.Entries.AsSpan(i * 16);
                entries[i] = new Entry(BinaryPrimitives.ReadUInt32LittleEndian(e), BinaryPrimitives.ReadInt32LittleEndian(e[4..]), e[8],
                    BinaryPrimitives.ReadInt16LittleEndian(e[10..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
            }
            return _entries[structure] = entries;
        }

        private int Size(uint structure) => document.FindStructure(structure)?.Size ?? 0;

        private byte[]? Block(int block) => block >= 0 && block < document.Blocks.Count ? document.Blocks[block].Data : null;

        public void WriteFields(XmlText xml, uint structure, int block, int offset)
        {
            var entries = Entries(structure) ?? throw new InvalidDataException($"structure {Name(structure)} non décrite dans le fichier");
            var data = Block(block) ?? throw new InvalidDataException($"bloc {block + 1} introuvable");
            if (offset + Size(structure) > data.Length)
                throw new InvalidDataException($"{Name(structure)} dépasse de son bloc");
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Name == ArrayInfo)
                    continue;
                WriteField(xml, entry, ItemsOf(entries, i), data, block, offset + entry.Offset);
            }
        }

        // Same rule as Build: the entry the array names, or the one before it.
        private static Entry ItemsOf(Entry[] entries, int index)
        {
            var entry = entries[index];
            if (entry.RefIndex >= 0 && entry.RefIndex < entries.Length && entries[entry.RefIndex].Name == ArrayInfo)
                return entries[entry.RefIndex];
            return index > 0 ? entries[index - 1] : default;
        }

        private void WriteField(XmlText xml, Entry entry, Entry items, byte[] data, int block, int at)
        {
            string name = Name(entry.Name);
            var span = data.AsSpan(at);
            switch (entry.Type)
            {
                case Boolean:
                    xml.Value(name, span[0] != 0);
                    break;
                case SignedByte:
                    xml.Value(name, (sbyte)span[0]);
                    break;
                case UnsignedByte:
                case ByteEnum:
                    xml.Value(name, span[0]);
                    break;
                case SignedShort:
                    xml.Value(name, BinaryPrimitives.ReadInt16LittleEndian(span));
                    break;
                case UnsignedShort:
                    xml.Value(name, BinaryPrimitives.ReadUInt16LittleEndian(span));
                    break;
                case SignedInt:
                    xml.Value(name, BinaryPrimitives.ReadInt32LittleEndian(span));
                    break;
                case UnsignedInt:
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    // Colours read better in hexadecimal (0xAARRGGBB).
                    xml.Value(name, Name(entry.Name) == "color" ? $"0x{value:X8}" : value.ToString(CultureInfo.InvariantCulture));
                    break;
                }
                case Float:
                    xml.Value(name, BinaryPrimitives.ReadSingleLittleEndian(span));
                    break;
                case FloatXyz:
                    xml.Vector(name, new System.Numerics.Vector3(Single(span, 0), Single(span, 1), Single(span, 2)));
                    break;
                case FloatXyzw:
                    xml.Vector(name, new System.Numerics.Vector4(Single(span, 0), Single(span, 1), Single(span, 2), Single(span, 3)));
                    break;
                case Hash:
                    xml.Text(name, HashText(BinaryPrimitives.ReadUInt32LittleEndian(span)));
                    break;
                case IntEnum:
                    xml.Text(name, EnumText(entry.RefKey, BinaryPrimitives.ReadInt32LittleEndian(span), flags: false));
                    break;
                case IntFlags1:
                case IntFlags2:
                    xml.Text(name, EnumText(entry.RefKey, BinaryPrimitives.ReadInt32LittleEndian(span), flags: true));
                    break;
                case ShortFlags:
                    xml.Text(name, EnumText(entry.RefKey, BinaryPrimitives.ReadInt16LittleEndian(span), flags: false));
                    break;
                case ArrayOfChars:
                {
                    int length = Math.Min((int)(entry.RefKey & 0xFFFF), span.Length);
                    int end = span[..length].IndexOf((byte)0);
                    xml.Text(name, Encoding.ASCII.GetString(span[..(end < 0 ? length : end)]));
                    break;
                }
                case ArrayOfBytes:
                    WriteInlineArray(xml, name, entry, items, span);
                    break;
                case CharPointer:
                    xml.Text(name, ReadString(BinaryPrimitives.ReadUInt64LittleEndian(span), BinaryPrimitives.ReadUInt16LittleEndian(span[8..])));
                    break;
                case DataBlockPointer:
                {
                    var (target, _) = MetaDocument.DecodePointer(BinaryPrimitives.ReadUInt64LittleEndian(span));
                    var bytes = Block(target) ?? [];
                    xml.RawArray(name, bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)).ToList(), 32);
                    break;
                }
                case Structure:
                    xml.Open(name);
                    WriteFields(xml, entry.RefKey, block, at);
                    xml.Close(name);
                    break;
                case Array:
                    WriteArray(xml, name, items, span);
                    break;
                default:
                    // Single structure pointers: not used by map files (Build skips them too).
                    xml.Empty(name);
                    break;
            }
        }

        private static float Single(ReadOnlySpan<byte> span, int index) => BinaryPrimitives.ReadSingleLittleEndian(span[(index * 4)..]);

        private string ReadString(ulong pointer, int length)
        {
            if (pointer == 0)
                return "";
            var (block, offset) = MetaDocument.DecodePointer(pointer);
            var data = Block(block);
            if (data is null || offset >= data.Length)
                return "";
            var text = data.AsSpan(offset, Math.Min(length > 0 ? length : data.Length - offset, data.Length - offset));
            int end = text.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        }

        private string EnumText(uint enumHash, int value, bool flags)
        {
            var info = document.Enums.Find(e => BinaryPrimitives.ReadUInt32LittleEndian(e.Info) == enumHash);
            if (info is null)
                return value.ToString(CultureInfo.InvariantCulture);
            var known = new List<(uint Name, int Value)>();
            for (int i = 0; i + 8 <= info.Entries.Length; i += 8)
                known.Add((BinaryPrimitives.ReadUInt32LittleEndian(info.Entries.AsSpan(i)), BinaryPrimitives.ReadInt32LittleEndian(info.Entries.AsSpan(i + 4))));
            if (!flags)
            {
                foreach (var (n, v) in known)
                {
                    if (v == value)
                        return Name(n);
                }
                return value.ToString(CultureInfo.InvariantCulture);
            }
            var parts = new List<string>();
            int rest = value;
            foreach (var (n, v) in known)
            {
                if (v is < 0 or > 31 || (value & (1 << v)) == 0)
                    continue;
                parts.Add(Name(n));
                rest &= ~(1 << v);
            }
            // Bits no value names stay as a number (Build adds numbers as they are).
            if (rest != 0)
                parts.Add(rest.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }

        private void WriteInlineArray(XmlText xml, string name, Entry entry, Entry items, ReadOnlySpan<byte> span)
        {
            int count = (int)(entry.RefKey & 0xFFFF);
            var list = new List<string>(count);
            if (items.Type is SignedByte or UnsignedByte or SignedShort or UnsignedShort or SignedInt or UnsignedInt or Float)
            {
                int size = ValueSize(items.Type);
                for (int i = 0; i < count && (i + 1) * size <= span.Length; i++)
                    list.Add(ValueText(items.Type, span[(i * size)..]));
                xml.Text(name, string.Join(" ", list));
                return;
            }
            var hex = new StringBuilder(count * 2);
            for (int i = 0; i < count && i < span.Length; i++)
                hex.Append(span[i].ToString("X2", CultureInfo.InvariantCulture));
            xml.Text(name, hex.ToString());
        }

        private string ValueText(byte type, ReadOnlySpan<byte> span) => type switch
        {
            Boolean => span[0] != 0 ? "true" : "false",
            SignedByte => ((sbyte)span[0]).ToString(CultureInfo.InvariantCulture),
            UnsignedByte => span[0].ToString(CultureInfo.InvariantCulture),
            SignedShort => BinaryPrimitives.ReadInt16LittleEndian(span).ToString(CultureInfo.InvariantCulture),
            UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(span).ToString(CultureInfo.InvariantCulture),
            SignedInt => BinaryPrimitives.ReadInt32LittleEndian(span).ToString(CultureInfo.InvariantCulture),
            UnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(span).ToString(CultureInfo.InvariantCulture),
            Float => XmlText.Number(BinaryPrimitives.ReadSingleLittleEndian(span)),
            Hash => HashText(BinaryPrimitives.ReadUInt32LittleEndian(span)),
            _ => "",
        };

        private void WriteArray(XmlText xml, string name, Entry items, ReadOnlySpan<byte> span)
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(span);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            var (block, offset) = MetaDocument.DecodePointer(pointer);
            if (pointer == 0)
                count = 0;
            switch (items.Type)
            {
                case Structure:
                {
                    string attributes = $"itemType=\"{Name(items.RefKey)}\"";
                    if (count == 0)
                    {
                        xml.Empty(name, attributes);
                        return;
                    }
                    int size = Size(items.RefKey);
                    xml.Open(name, attributes);
                    for (int i = 0; i < count; i++)
                    {
                        // Long arrays go on in the next block.
                        if (i > 0 && Block(block) is { } data && offset >= data.Length)
                        {
                            block++;
                            offset = 0;
                        }
                        xml.Open("Item");
                        WriteFields(xml, items.RefKey, block, offset);
                        xml.Close("Item");
                        offset += size;
                    }
                    xml.Close(name);
                    return;
                }
                case StructurePointer:
                {
                    if (count == 0)
                    {
                        xml.Empty(name);
                        return;
                    }
                    var pointers = Block(block) ?? throw new InvalidDataException($"<{name}> : bloc introuvable");
                    xml.Open(name);
                    for (int i = 0; i < count; i++)
                    {
                        ulong item = offset + i * 8 + 8 <= pointers.Length ? BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(offset + i * 8)) : 0;
                        var (itemBlock, itemOffset) = MetaDocument.DecodePointer(item);
                        if (item == 0 || Block(itemBlock) is null)
                        {
                            xml.Empty("Item", "type=\"NULL\"");
                            continue;
                        }
                        uint type = document.Blocks[itemBlock].Structure;
                        xml.Open("Item", $"type=\"{Name(type)}\"");
                        WriteFields(xml, type, itemBlock, itemOffset);
                        xml.Close("Item");
                    }
                    xml.Close(name);
                    return;
                }
                case FloatXyz:
                {
                    var data = count == 0 ? null : Block(block);
                    if (data is null)
                    {
                        xml.Empty(name);
                        return;
                    }
                    xml.Open(name);
                    for (int i = 0; i < count && offset + (i + 1) * 16 <= data.Length; i++)
                    {
                        var v = data.AsSpan(offset + i * 16);
                        xml.Text("Item", $"{XmlText.Number(Single(v, 0))}, {XmlText.Number(Single(v, 1))}, {XmlText.Number(Single(v, 2))}, {XmlText.Number(Single(v, 3))}");
                    }
                    xml.Close(name);
                    return;
                }
                case Hash:
                {
                    var data = count == 0 ? null : Block(block);
                    if (data is null)
                    {
                        xml.Empty(name);
                        return;
                    }
                    xml.Open(name);
                    for (int i = 0; i < count && offset + (i + 1) * 4 <= data.Length; i++)
                        xml.Text("Item", HashText(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4))));
                    xml.Close(name);
                    return;
                }
                default:
                {
                    var data = count == 0 ? null : Block(block);
                    var list = new List<string>();
                    if (data is not null)
                    {
                        int size = ValueSize(items.Type);
                        for (int i = 0; i < count && offset + (i + 1) * size <= data.Length; i++)
                            list.Add(ValueText(items.Type, data.AsSpan(offset + i * size)));
                    }
                    xml.RawArray(name, list, 10);
                    return;
                }
            }
        }
    }
}
