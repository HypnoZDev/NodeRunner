using System.Buffers.Binary;

namespace NodeRunner.Data.Native.Classic;

// A section of a RenderWare file: 12-byte header (type, size, library stamp), then Size bytes holding either data or
// other sections. Version: the RenderWare version (0x31000 GTA III, 0x33002 Vice City, 0x36003 San Andreas).
public readonly record struct RwChunk(uint Type, int Offset, int Size, uint Version)
{
    public const uint Struct = 0x01, String = 0x02, Extension = 0x03, Texture = 0x06, Material = 0x07,
        MaterialList = 0x08, FrameList = 0x0E, Geometry = 0x0F, Clump = 0x10, Atomic = 0x14, TextureNative = 0x15,
        TextureDictionary = 0x16, GeometryList = 0x1A, FrameName = 0x253F2FE;

    public int End => Offset + Size;

    public static RwChunk? At(byte[] data, int at, int limit)
    {
        if (at < 0 || at + 12 > limit || limit > data.Length)
            return null;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
        uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8));
        if (at + 12 + size > limit)
            return null;
        return new RwChunk(type, at + 12, (int)size, DecodeVersion(stamp));
    }

    public static uint DecodeVersion(uint stamp) =>
        (stamp & 0xFFFF0000) != 0 ? (((stamp >> 14) & 0x3FF00) + 0x30000) | ((stamp >> 16) & 0x3F) : stamp << 8;

    // The sections directly inside this one.
    public List<RwChunk> Children(byte[] data)
    {
        var children = new List<RwChunk>();
        int at = Offset;
        while (At(data, at, End) is { } child)
        {
            children.Add(child);
            at = child.End;
        }
        return children;
    }

    public RwChunk? Child(byte[] data, uint type)
    {
        foreach (var child in Children(data))
            if (child.Type == type)
                return child;
        return null;
    }

    public string ReadString(byte[] data)
    {
        var span = data.AsSpan(Offset, Size);
        int end = span.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
    }
}
