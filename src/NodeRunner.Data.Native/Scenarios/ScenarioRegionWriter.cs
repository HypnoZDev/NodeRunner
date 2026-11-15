using System.Buffers.Binary;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Scenarios;
using R = NodeRunner.Data.Native.Scenarios.ScenarioRegionReader;

namespace NodeRunner.Data.Native.Scenarios;

// Writes the values of a region's points back into its file content (decompressed), in place: same points, same
// layout. A point's type must be one the file lists.
public static class ScenarioRegionWriter
{
    public static byte[] Patch(ReadOnlySpan<byte> content, ScenarioRegion region)
    {
        var result = content.ToArray();
        List<int> offsets;
        int typeList, typeCount;
        {
            var meta = new MetaResource(content);
            int start = R.Region(meta);
            offsets = R.PointOffsets(meta, start);
            (typeList, typeCount) = R.TypeList(meta, start);
        }
        if (offsets.Count != region.Points.Count)
            throw new InvalidDataException($"{offsets.Count} points dans le fichier, {region.Points.Count} attendus");
        var types = new Dictionary<uint, byte>();
        for (int i = typeCount - 1; i >= 0; i--)
            types[BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(typeList + i * 4))] = (byte)i;

        var span = result.AsSpan();
        for (int i = 0; i < offsets.Count; i++)
        {
            int o = offsets[i];
            var v = region.Points[i].Values;
            if (!types.TryGetValue(v.TypeHash, out byte type))
                throw new InvalidDataException($"point {i} : type absent de ce fichier");
            span[o + R.PointType] = type;
            span[o + R.PointProbability] = v.Probability;
            span[o + R.PointAvailability] = v.Availability;
            span[o + R.PointStartHour] = v.StartHour;
            span[o + R.PointEndHour] = v.EndHour;
            span[o + R.PointRadius] = v.Radius;
            span[o + R.PointTimeTillPedLeaves] = v.TimeTillPedLeaves;
            BinaryPrimitives.WriteUInt32LittleEndian(span[(o + R.PointFlags)..], v.Flags);
            int p = o + R.PointPositionAndDirection;
            BinaryPrimitives.WriteSingleLittleEndian(span[p..], v.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(span[(p + 4)..], v.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span[(p + 8)..], v.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(span[(p + 12)..], v.Heading);
        }
        return result;
    }
}
