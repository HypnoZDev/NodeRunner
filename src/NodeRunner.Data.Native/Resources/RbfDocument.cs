using System.Buffers.Binary;
using System.Text;

namespace NodeRunner.Data.Native.Resources;

// reader for RBF files ("RBF0"): a binary form of XML used by some .ymt files (e.g. gtxd.ymt). Format documented by
// the GTA V modding community. The file is a stream of tags. Each tag starts with a descriptor index (a name,
// declared inline the first time it is used) and a type: 0 opens an element, 0x10 uint, 0x20/0x30 true/false, 0x40
// float, 0x50 three floats, 0x60 string. 0xFD 0xFF introduces raw bytes (element text), 0xFF 0xFF closes an element.
internal static class RbfDocument
{
    public const uint Magic = 0x30464252; // "RBF0"

    public static bool IsRbf(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    // Reads the whole file and returns its root element.
    public static RbfElement Parse(ReadOnlySpan<byte> data)
    {
        if (!IsRbf(data))
            throw new InvalidDataException("Ce n'est pas un fichier RBF.");

        var names = new List<string>();
        var stack = new Stack<RbfElement>();
        RbfElement? current = null;
        int position = 4;

        while (position < data.Length)
        {
            byte index = ReadByte(data, ref position);
            if (index == 0xFF)
            {
                Expect(ReadByte(data, ref position), 0xFF);
                if (current is null)
                    throw new InvalidDataException("RBF : fermeture sans élément ouvert.");
                if (stack.Count == 0)
                    return current;
                current = stack.Pop();
                continue;
            }

            if (index == 0xFD)
            {
                Expect(ReadByte(data, ref position), 0xFF);
                int length = ReadInt32(data, ref position);
                var bytes = ReadBytes(data, ref position, length);
                (current ?? throw new InvalidDataException("RBF : données hors élément.")).Bytes = bytes.ToArray();
                continue;
            }

            byte type = ReadByte(data, ref position);
            if (index == names.Count)
            {
                int nameLength = ReadInt16(data, ref position);
                names.Add(Encoding.ASCII.GetString(ReadBytes(data, ref position, nameLength)));
            }
            else if (index > names.Count)
            {
                throw new InvalidDataException($"RBF : nom inconnu (index {index}).");
            }

            var element = new RbfElement(names[index]);
            switch (type)
            {
                case 0x00:
                    position += 6; // Unknown values and attribute count: not needed.
                    if (current is not null)
                    {
                        current.Children.Add(element);
                        stack.Push(current);
                    }
                    current = element;
                    continue;
                case 0x10:
                    element.Value = BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(data, ref position, 4));
                    break;
                case 0x20:
                    element.Value = true;
                    break;
                case 0x30:
                    element.Value = false;
                    break;
                case 0x40:
                    element.Value = BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(data, ref position, 4));
                    break;
                case 0x50:
                    var xyz = ReadBytes(data, ref position, 12);
                    element.Value = new System.Numerics.Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(xyz),
                        BinaryPrimitives.ReadSingleLittleEndian(xyz[4..]),
                        BinaryPrimitives.ReadSingleLittleEndian(xyz[8..]));
                    break;
                case 0x60:
                    int textLength = ReadInt16(data, ref position);
                    element.Value = Encoding.ASCII.GetString(ReadBytes(data, ref position, textLength));
                    break;
                default:
                    throw new InvalidDataException($"RBF : type de donnée inconnu 0x{type:X2}.");
            }
            (current ?? throw new InvalidDataException("RBF : valeur hors élément.")).Children.Add(element);
        }

        throw new InvalidDataException("RBF : fin de fichier inattendue.");
    }

    private static void Expect(byte value, byte expected)
    {
        if (value != expected)
            throw new InvalidDataException($"RBF : 0x{expected:X2} attendu, 0x{value:X2} trouvé.");
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int position) => ReadBytes(data, ref position, 1)[0];

    private static short ReadInt16(ReadOnlySpan<byte> data, ref int position) =>
        BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(data, ref position, 2));

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int position) =>
        BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(data, ref position, 4));

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int position, int count)
    {
        if (count < 0 || position + count > data.Length)
            throw new InvalidDataException("RBF : fin de fichier inattendue.");
        var bytes = data.Slice(position, count);
        position += count;
        return bytes;
    }
}

// An element of an RBF file: a name, child elements, and either raw bytes (text) or a typed value.
internal sealed class RbfElement(string name)
{
    public string Name { get; } = name;
    public List<RbfElement> Children { get; } = new();
    public byte[]? Bytes { get; set; }
    public object? Value { get; set; }

    public string? Text => Bytes is not null ? Encoding.ASCII.GetString(Bytes).TrimEnd('\0') : Value as string;

    public RbfElement? Child(string name) =>
        Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
