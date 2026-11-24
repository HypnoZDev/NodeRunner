using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.Native.Recordings;
using NodeRunner.Data.Recordings;

namespace NodeRunner.Tests;

public class VehicleRecordingTests
{
    [Theory]
    [InlineData("fam1001", "fam1", 1)]
    [InlineData("fam1cut005", "fam1cut", 5)]
    [InlineData("bb_finale000", "bb_finale", 0)]
    [InlineData("carrec", "carrec", 0)]
    public void SplitName_TakesTheLastThreeDigits(string name, string group, int number)
    {
        Assert.Equal((group, number), VehicleRecording.SplitName(name));
    }

    [Fact]
    public void Read_DecodesSamplesAndDropsGarbage()
    {
        var data = File([
            (0u, new Vector3(10, 20, 30), 0f),
            (1000u, new Vector3(30, 20, 30), 20f),
            (2000u, new Vector3(2e31f, 0, 0), 0f),
        ]);
        var recording = VehicleRecordingReader.Read(data, "fam1002");

        Assert.Equal("fam1", recording.Group);
        Assert.Equal(2, recording.Number);
        Assert.Equal(2, recording.Frames.Count);
        Assert.Equal(1f, recording.Duration);
        var last = recording.Frames[1];
        Assert.Equal(20f, last.Velocity.X, 0.01f);
        Assert.Equal(0.5f, last.Gas, 0.001f);
        Assert.Equal(0.5f, last.Steering, 0.001f);
        Assert.True(last.Handbrake);
    }

    [Fact]
    public void Sample_FollowsThePathAndFacesForward()
    {
        var recording = VehicleRecordingReader.Read(File([
            (0u, new Vector3(0, 0, 0), 20f),
            (1000u, new Vector3(20, 0, 0), 20f),
        ]), "test001");

        var middle = recording.Sample(0.5f);
        Assert.Equal(10f, middle.Position.X, 0.01f);
        Assert.Equal(20f, middle.Speed, 0.05f);
        // Forward is +X in the file: the model's +Y axis must point there.
        var forward = Vector3.Transform(Vector3.UnitY, middle.Rotation);
        Assert.Equal(1f, forward.X, 0.02f);
        Assert.Equal(new Vector3(20, 0, 0), recording.Sample(5f).Position);
    }

    // A .yvr resource: a 0x20-byte header pointing to the 32-byte samples. Each sample faces +X, half gas, steering
    // 10/20 rad, handbrake on.
    private static byte[] File((uint Time, Vector3 Position, float Speed)[] samples)
    {
        var data = new byte[0x20 + samples.Length * 0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x10), 0x50000020);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x18), (ushort)samples.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x1A), (ushort)samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            var s = data.AsSpan(0x20 + i * 0x20);
            BinaryPrimitives.WriteUInt32LittleEndian(s, samples[i].Time);
            BinaryPrimitives.WriteInt16LittleEndian(s[4..], (short)Math.Round(samples[i].Speed * 32767f / 120f));
            s[11] = unchecked((byte)-127); // right = -Y
            s[13] = 127; // forward = +X
            s[16] = 10;
            s[17] = 50;
            s[19] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(s[20..], samples[i].Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(s[24..], samples[i].Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[28..], samples[i].Position.Z);
        }
        return data;
    }
}
