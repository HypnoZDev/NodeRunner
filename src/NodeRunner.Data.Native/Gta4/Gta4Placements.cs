using System.Buffers.Binary;
using System.Numerics;

namespace NodeRunner.Data.Native.Gta4;

// A model placed on the map. LodParent: index of the lower detail instance (in the same file for a main ".wpl", in
// the main ".wpl" of the area for a streamed one), -1 when there is none. LodDistance: -1 = the model's own distance.
public readonly record struct Gta4Instance(uint ModelHash, Vector3 Position, Quaternion Orientation, uint Flags, int LodParent,
    float LodDistance);

// Binary placement file of GTA IV (".wpl", version 3): a header (version, then 16 section counts, instances first),
// then 48-byte instances: position, rotation (stored inverted), model hash, flags, LOD parent, unknown, LOD distance.
// The other sections (cars, LOD culling...) follow the instances and are not read.
public static class Gta4Placements
{
    public const int Version = 3;
    private const int HeaderSize = 68, InstanceSize = 48;

    public static Gta4Instance[] Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadInt32LittleEndian(data) != Version)
            throw new InvalidDataException("pas un fichier de placement .wpl de GTA IV (version 3)");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (count < 0 || HeaderSize + (long)count * InstanceSize > data.Length)
            throw new InvalidDataException("fichier de placement .wpl invalide");

        var instances = new Gta4Instance[count];
        for (int i = 0; i < count; i++)
        {
            var s = data.Slice(HeaderSize + i * InstanceSize, InstanceSize);
            var position = new Vector3(Float(s), Float(s[4..]), Float(s[8..]));
            var rotation = new Quaternion(Float(s[12..]), Float(s[16..]), Float(s[20..]), Float(s[24..]));
            rotation = rotation.LengthSquared() > 1e-6f ? Quaternion.Normalize(Quaternion.Conjugate(rotation)) : Quaternion.Identity;
            instances[i] = new Gta4Instance(BinaryPrimitives.ReadUInt32LittleEndian(s[28..]), position, rotation,
                BinaryPrimitives.ReadUInt32LittleEndian(s[32..]), BinaryPrimitives.ReadInt32LittleEndian(s[36..]), Float(s[44..]));
        }
        return instances;
    }

    private static float Float(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadSingleLittleEndian(s);
}
