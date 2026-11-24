using System.Numerics;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Recordings;

namespace NodeRunner.Data.Native.Recordings;

// Reads a vehicle recording (.yvr, resource version 1): a block with a simple 64-bit list of 32-byte samples.
public static class VehicleRecordingReader
{
    private const int SampleSize = 0x20;
    // Recorded velocities are stored as 16-bit values over +-120 m/s.
    private const float VelocityScale = 32767f / 120f;
    private const float SteeringScale = 20f;
    private const float WorldLimit = 50000f;

    public static VehicleRecording Read(byte[] data, string name)
    {
        var resource = new ResourceData(data);
        ulong pointer = resource.UInt64(0x10);
        int count = resource.UInt16(0x18);
        int start = resource.ResolveArray(pointer, count, SampleSize, "échantillons de l'enregistrement");
        var frames = new List<VehicleRecordingFrame>(count);
        for (int i = 0; i < count; i++)
        {
            int at = start + i * SampleSize;
            var position = new Vector3(Single(resource, at + 20), Single(resource, at + 24), Single(resource, at + 28));
            // A few game files end with garbage (agencyprep001): points far outside the world are dropped.
            if (!(Math.Abs(position.X) < WorldLimit && Math.Abs(position.Y) < WorldLimit && Math.Abs(position.Z) < WorldLimit))
                continue;
            float time = resource.UInt32(at) / 1000f;
            // Samples are in time order; a sample going back is dropped so that searching by time stays valid.
            if (frames.Count > 0 && time < frames[^1].Time)
                continue;
            frames.Add(new VehicleRecordingFrame(
                time,
                position,
                new Vector3(resource.Int16(at + 4), resource.Int16(at + 6), resource.Int16(at + 8)) / VelocityScale,
                new Vector3(SByte(resource, at + 10), SByte(resource, at + 11), SByte(resource, at + 12)) / 127f,
                new Vector3(SByte(resource, at + 13), SByte(resource, at + 14), SByte(resource, at + 15)) / 127f,
                SByte(resource, at + 16) / SteeringScale,
                SByte(resource, at + 17) / 100f,
                SByte(resource, at + 18) / 100f,
                resource.Byte(at + 19) != 0));
        }
        var (group, number) = VehicleRecording.SplitName(name);
        return new VehicleRecording { Name = name, Group = group, Number = number, Frames = frames };
    }

    private static sbyte SByte(ResourceData resource, int offset) => unchecked((sbyte)resource.Byte(offset));

    private static float Single(ResourceData resource, int offset) => BitConverter.Int32BitsToSingle((int)resource.UInt32(offset));
}
