using System.Buffers.Binary;
using System.Security.Cryptography;
using NodeRunner.Data.Native.Audio;

namespace NodeRunner.Tests;

public class SoundBankTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(16);

    [Fact]
    public void Encryption_RoundTrips()
    {
        var data = RandomNumberGenerator.GetBytes(4 * 37 + 3);
        var copy = (byte[])data.Clone();
        var key = SoundBankReader.KeyWords(Key);
        SoundBankReader.Encrypt(copy, key);
        Assert.NotEqual(data, copy);
        Assert.Equal(data[^3..], copy[^3..]);
        SoundBankReader.Decrypt(copy, key);
        Assert.Equal(data, copy);
    }

    [Fact]
    public void Reads_PcmTrack_AlsoWhenTheWholeFileIsEncrypted()
    {
        short[] samples = [0, 1000, -1000, 32767, -32768, 5];
        var file = SingleTrackBank(0x1234567, samples, codec: 0, sampleRate: 22050);

        var track = Assert.Single(new SoundBankReader(null).Read(file).Tracks);
        Assert.Equal(0x1234567u, track.Id);
        Assert.Equal(22050, track.SampleRate);
        Assert.Equal(samples, track.Decode());

        SoundBankReader.Encrypt(file, SoundBankReader.KeyWords(Key));
        Assert.Throws<InvalidDataException>(() => new SoundBankReader(null).Read(file));
        Assert.Equal(samples, Assert.Single(new SoundBankReader(Key).Read(file).Tracks).Decode());
    }

    [Fact]
    public void DecodesAdpcm()
    {
        // Frame header: step index 0 (step 7), starting sample 100. Code 7: +13, index 8 (step 16); code F: -30,
        // index 16 (step 34); code 0: +4, index 15 (step 31); code 0: +3.
        var frame = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(2), 100);
        frame[4] = 0xF7;
        frame[5] = 0x00;
        var output = new short[4];

        Assert.Equal(4, SoundBankReader.DecodeAdpcm(frame, output));
        Assert.Equal([113, 83, 87, 90], output);
    }

    [Fact]
    public void Reads_MultiChannelStream()
    {
        short[] left = [1, 2, 3], right = [-1, -2, -3, -4];
        var file = MultiChannelBank(left, right);

        var tracks = new SoundBankReader(null).Read(file).Tracks;
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.Equal(0, t.Group));
        Assert.Equal(left, tracks.Single(t => t.Id == 0x11).Decode());
        Assert.Equal(right, tracks.Single(t => t.Id == 0x22).Decode());
    }

    // "ADAT", 1 stream, 2 chunks: format (20 bytes) and data.
    private static byte[] SingleTrackBank(uint id, short[] samples, byte codec, ushort sampleRate)
    {
        int headerLength = 16 + 4 + 16;
        int formatOffset = headerLength, dataOffset = formatOffset + 20, dataLength = samples.Length * 2;
        var file = new byte[dataOffset + dataLength + 2];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x54414441);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], headerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (2u << 29) | id);
        BinaryPrimitives.WriteUInt64LittleEndian(span[20..], Chunk(0xFA, 20, formatOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(span[28..], Chunk(0x55, dataLength, dataOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(span[formatOffset..], (uint)samples.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[(formatOffset + 4)..], -1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(formatOffset + 8)..], sampleRate);
        span[formatOffset + 19] = codec;
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(dataOffset + i * 2)..], samples[i]);
        return file;
    }

    // Flag 4; stream 0 holds the stream format and one block of PCM frames, streams 1 and 2 are the channels.
    private static byte[] MultiChannelBank(short[] left, short[] right)
    {
        int headerLength = 16 + 3 * 4 + 2 * 8;
        int formatOffset = headerLength, formatLength = 12 + 2 * 16;
        int dataOffset = formatOffset + formatLength;
        const int frame = 2048;
        var block = new byte[frame * 3]; // Headers + offsets + padding, then one frame per channel.
        short[][] channels = [left, right];
        for (int c = 0; c < 2; c++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(c * 24 + 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(c * 24 + 12), channels[c].Length);
            for (int i = 0; i < channels[c].Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(frame * (1 + c) + i * 2), channels[c][i]);
        }

        var file = new byte[dataOffset + block.Length];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x54414441);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], headerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 2u << 29);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0x22);
        BinaryPrimitives.WriteUInt64LittleEndian(span[28..], Chunk(0x48, formatLength, formatOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(span[36..], Chunk(0x55, block.Length, dataOffset));
        var f = span[formatOffset..];
        BinaryPrimitives.WriteInt32LittleEndian(f, 1);
        BinaryPrimitives.WriteInt32LittleEndian(f[4..], block.Length);
        BinaryPrimitives.WriteInt32LittleEndian(f[8..], 2);
        for (int c = 0; c < 2; c++)
        {
            var entry = f[(12 + c * 16)..];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, c == 0 ? 0x11u : 0x22u);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)channels[c].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[10..], 32000);
            entry[12] = 0;
        }
        block.CopyTo(span[dataOffset..]);
        return file;
    }

    private static ulong Chunk(byte type, int size, int offset) => ((ulong)type << 56) | ((ulong)size << 28) | (uint)offset;
}
