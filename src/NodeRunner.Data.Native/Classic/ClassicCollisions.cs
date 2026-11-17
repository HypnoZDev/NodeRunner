using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace NodeRunner.Data.Native.Classic;

// The box and sphere around a model, as its collision gives them.
public readonly record struct ClassicBounds(string Model, Vector3 Min, Vector3 Max, Vector3 Center, float Radius);

// Collision files of the first trilogy (".col"): one entry per model, one after the other. Each starts with a
// four-letter version ("COLL" GTA III / Vice City, "COL2" / "COL3" / "COL4" San Andreas), its size, the model name
// (22 bytes, then a model number on 2 bytes from COL2 on) and the bounds: radius, centre, box for COLL; box, centre,
// radius for the later versions. Only the bounds are read here.
public static class ClassicCollisions
{
    private const uint Coll = 0x4C4C4F43, Col2 = 0x324C4F43, Col3 = 0x334C4F43, Col4 = 0x344C4F43;
    private const int HeaderSize = 8, NameSize = 24, BoundsSize = 40;

    public static List<ClassicBounds> ReadBounds(ReadOnlySpan<byte> data)
    {
        var bounds = new List<ClassicBounds>();
        int at = 0;
        while (at + HeaderSize + NameSize + BoundsSize <= data.Length)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if (magic is not (Coll or Col2 or Col3 or Col4))
                break;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (size < NameSize + BoundsSize || at + HeaderSize + size > data.Length)
                break;
            var name = data.Slice(at + HeaderSize, 22);
            int end = name.IndexOf((byte)0);
            string model = Encoding.ASCII.GetString(end < 0 ? name : name[..end]).Trim();
            var b = data.Slice(at + HeaderSize + NameSize, BoundsSize);
            if (magic == Coll)
            {
                float radius = Float(b);
                bounds.Add(new ClassicBounds(model, Vec(b[16..]), Vec(b[28..]), Vec(b[4..]), radius));
            }
            else
            {
                bounds.Add(new ClassicBounds(model, Vec(b), Vec(b[12..]), Vec(b[24..]), Float(b[36..])));
            }
            at += HeaderSize + (int)size;
        }
        return bounds;
    }

    private static float Float(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadSingleLittleEndian(s);

    private static Vector3 Vec(ReadOnlySpan<byte> s) => new(Float(s), Float(s[4..]), Float(s[8..]));
}
