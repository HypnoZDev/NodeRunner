using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Classic;

// A model placed on the map. Interior: 0 outside, otherwise the interior it belongs to (Vice City, San Andreas; such
// rooms float far from the city). Lod: San Andreas index of the lower detail instance (in the same file for a text
// ".ipl", in the main text ".ipl" of the area for a streamed one), -1 when there is none.
public readonly record struct ClassicInstance(int ModelId, string? ModelName, int Interior, Vector3 Position, Quaternion Rotation,
    Vector3 Scale, int Lod);

// Placement files (".ipl") of the first trilogy:
// - text, "inst" section: GTA III "id, model, position, scale, rotation"; Vice City adds the interior after the model;
//   San Andreas "id, model, interior, position, rotation, lod" (no scale);
// - binary (San Andreas streamed files, "bnry"): a header of section counts and offsets, then 40-byte instances
//   (position, rotation, model number, interior, lod).
// Rotations are stored inverted.
public static class ClassicPlacements
{
    public const uint BinaryMagic = 0x79726E62; // "bnry"
    private const int BinaryHeaderSize = 76, BinaryInstanceSize = 40;

    public static bool IsBinary(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == BinaryMagic;

    public static ClassicInstance[] ReadBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < BinaryHeaderSize || !IsBinary(data))
            throw new InvalidDataException("pas un fichier de placement binaire (bnry)");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[28..]);
        if (count < 0 || offset < 0 || offset + (long)count * BinaryInstanceSize > data.Length)
            throw new InvalidDataException("fichier de placement binaire invalide");
        var instances = new ClassicInstance[count];
        for (int i = 0; i < count; i++)
        {
            var s = data.Slice(offset + i * BinaryInstanceSize, BinaryInstanceSize);
            var position = new Vector3(Float(s), Float(s[4..]), Float(s[8..]));
            var rotation = Rotation(Float(s[12..]), Float(s[16..]), Float(s[20..]), Float(s[24..]));
            instances[i] = new ClassicInstance(BinaryPrimitives.ReadInt32LittleEndian(s[28..]), null,
                BinaryPrimitives.ReadInt32LittleEndian(s[32..]), position, rotation, Vector3.One,
                BinaryPrimitives.ReadInt32LittleEndian(s[36..]));
        }
        return instances;
    }

    // The "inst" section of a text file; bad counts the lines that could not be read.
    public static ClassicInstance[] ReadText(string text, ClassicGame game, out int bad)
    {
        bad = 0;
        var instances = new List<ClassicInstance>();
        string section = "";
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment].TrimEnd();
            if (line.IsEmpty)
                continue;
            if (line.IndexOf(',') < 0 && line.IndexOfAny(' ', '\t') < 0)
            {
                section = line.Equals("end", StringComparison.OrdinalIgnoreCase) ? "" : line.ToString().ToLowerInvariant();
                continue;
            }
            if (section != "inst")
                continue;
            if (ReadInstance(ClassicDefinitions.Split(line.ToString()), game) is { } instance)
                instances.Add(instance);
            else
                bad++;
        }
        return [.. instances];
    }

    private static ClassicInstance? ReadInstance(string[] p, ClassicGame game)
    {
        int fields = game switch
        {
            ClassicGame.Gta3 => 12,
            ClassicGame.ViceCity => 13,
            _ => 11,
        };
        if (p.Length < fields || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            return null;
        int interior = 0;
        int next = 2;
        if (game != ClassicGame.Gta3)
        {
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out interior))
                return null;
            next = 3;
        }
        Span<float> v = stackalloc float[10];
        int values = game == ClassicGame.SanAndreas ? 7 : 10;
        for (int i = 0; i < values; i++)
            if (!ClassicDefinitions.TryFloat(p[next + i], out v[i]))
                return null;
        var position = new Vector3(v[0], v[1], v[2]);
        if (game == ClassicGame.SanAndreas)
        {
            if (!int.TryParse(p[next + 7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lod))
                return null;
            return new ClassicInstance(id, p[1], interior, position, Rotation(v[3], v[4], v[5], v[6]), Vector3.One, lod);
        }
        return new ClassicInstance(id, p[1], interior, position, Rotation(v[6], v[7], v[8], v[9]), new Vector3(v[3], v[4], v[5]), -1);
    }

    private static Quaternion Rotation(float x, float y, float z, float w)
    {
        var q = new Quaternion(x, y, z, w);
        return q.LengthSquared() > 1e-6f ? Quaternion.Normalize(Quaternion.Conjugate(q)) : Quaternion.Identity;
    }

    private static float Float(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadSingleLittleEndian(s);
}
