using System.Buffers.Binary;
using NodeRunner.Data.Audio;

namespace NodeRunner.Data.Native.Audio;

// Reader for the sound banks (.awc). Layout: "ADAT", version (16 bits), flags (16 bits), stream count, data offset;
// [first chunk index of each stream if flag 1]; per stream 32 bits (chunk count << 29 | id); per chunk 64 bits
// (type << 56 | size << 28 | offset). A whole file can be encrypted, or only the sample data (flag 2 single-channel
// streams, flag 8 blocks of a multi-channel stream, flag 4). Samples: 16-bit PCM, or IMA ADPCM in 2048-byte frames.
public sealed class SoundBankReader(byte[]? key) : ISoundBankReader
{
    private const uint Magic = 0x54414441;        // "ADAT"
    private const byte DataChunk = 0x55;
    private const byte FormatChunk = 0xFA;
    private const byte StreamFormatChunk = 0x48;
    private const int CodecPcm = 0;
    private const int CodecAdpcm = 4;
    private const int AdpcmFrame = 2048;

    private readonly uint[]? _key = key is null ? null : ToKey(key);

    public SoundBank Read(byte[] data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("Fichier son trop court.");
        bool wholeFileEncrypted = false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            if (_key is null)
                throw new InvalidDataException("Son chiffré : clé audio absente.");
            data = (byte[])data.Clone();
            Decrypt(data, _key);
            if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
                throw new InvalidDataException("Ce fichier n'est pas un son du jeu (.awc).");
            wholeFileEncrypted = true;
        }

        int flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        int streamCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        bool sampleEncrypted = (flags & 2) != 0 && !wholeFileEncrypted;
        bool multiChannel = (flags & 4) != 0;
        bool blockEncrypted = (flags & 8) != 0 && !wholeFileEncrypted;
        int position = 16 + ((flags & 1) != 0 ? streamCount * 2 : 0);
        if (streamCount < 0 || position + (long)streamCount * 4 > data.Length)
            throw new InvalidDataException("Fichier son abîmé.");

        var streams = new (uint Id, int ChunkCount)[streamCount];
        for (int i = 0; i < streamCount; i++, position += 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            streams[i] = (value & 0x1FFFFFFF, (int)(value >> 29));
        }
        var chunks = new Dictionary<byte, (int Offset, int Size)>[streamCount];
        for (int i = 0; i < streamCount; i++)
        {
            chunks[i] = [];
            for (int c = 0; c < streams[i].ChunkCount; c++, position += 8)
            {
                if (position + 8 > data.Length)
                    throw new InvalidDataException("Fichier son abîmé.");
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position));
                int offset = (int)(value & 0x0FFFFFFF), size = (int)((value >> 28) & 0x0FFFFFFF);
                if ((long)offset + size > data.Length)
                    throw new InvalidDataException("Fichier son abîmé.");
                chunks[i][(byte)(value >> 56)] = (offset, size);
            }
        }

        var tracks = new List<SoundTrack>();
        if (multiChannel)
            ReadMultiChannel(data, streams, chunks, blockEncrypted, tracks);
        for (int i = 0; i < streamCount; i++)
        {
            if (!chunks[i].TryGetValue(FormatChunk, out var format) || format.Size < 20
                || !chunks[i].TryGetValue(DataChunk, out var samples))
                continue;
            var f = data.AsSpan(format.Offset);
            int codec = f[19];
            if (codec is not (CodecPcm or CodecAdpcm))
                continue;
            int sampleCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(f), int.MaxValue);
            int loop = BinaryPrimitives.ReadInt32LittleEndian(f[4..]);
            var bytes = data.AsMemory(samples.Offset, samples.Size);
            uint[]? key = sampleEncrypted ? _key : null;
            if (sampleEncrypted && key is null)
                continue;
            tracks.Add(new SoundTrack
            {
                Id = streams[i].Id,
                SampleCount = sampleCount,
                SampleRate = BinaryPrimitives.ReadUInt16LittleEndian(f[8..]),
                Headroom = BinaryPrimitives.ReadInt16LittleEndian(f[10..]),
                LoopStart = loop >= 0 && loop < sampleCount ? loop : -1,
                IsCompressed = codec == CodecAdpcm,
                Decode = () => DecodeSingle(bytes, key, codec, sampleCount),
            });
        }
        return new SoundBank(tracks);
    }

    // The stream holding the stream format and the data cuts the samples of every channel into blocks; each block
    // starts with 24 bytes per channel (first frame, frame count, -, sample count, -, -), then each channel's frame
    // offsets, padding to 2048 bytes, then each channel's frames in turn. Channel streams are the other streams.
    private void ReadMultiChannel(byte[] data, (uint Id, int ChunkCount)[] streams,
        Dictionary<byte, (int Offset, int Size)>[] chunks, bool blockEncrypted, List<SoundTrack> tracks)
    {
        int source = Array.FindIndex(chunks, c => c.ContainsKey(StreamFormatChunk) && c.ContainsKey(DataChunk));
        if (source < 0 || (blockEncrypted && _key is null))
            return;
        var formatChunk = chunks[source][StreamFormatChunk];
        var f = data.AsSpan(formatChunk.Offset, formatChunk.Size);
        if (f.Length < 12)
            return;
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(f);
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(f[4..]);
        int channelCount = BinaryPrimitives.ReadInt32LittleEndian(f[8..]);
        if (blockCount <= 0 || blockSize <= 0 || channelCount <= 0 || f.Length < 12 + channelCount * 16)
            return;
        var samples = data.AsMemory(chunks[source][DataChunk].Offset, chunks[source][DataChunk].Size);
        var key = blockEncrypted ? _key : null;
        var layout = new MultiChannel(samples, blockCount, blockSize, channelCount, key);

        for (int channel = 0; channel < channelCount; channel++)
        {
            var entry = f.Slice(12 + channel * 16, 16);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(entry) & 0x1FFFFFFF;
            int codec = entry[12];
            if (codec is not (CodecPcm or CodecAdpcm))
                continue;
            int sampleCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]), int.MaxValue);
            int index = channel;
            tracks.Add(new SoundTrack
            {
                Id = id,
                SampleCount = sampleCount,
                Headroom = BinaryPrimitives.ReadInt16LittleEndian(entry[8..]),
                SampleRate = BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]),
                Group = source,
                IsCompressed = codec == CodecAdpcm,
                Decode = () => layout.Decode(index, codec, sampleCount),
            });
        }
    }

    private sealed class MultiChannel(ReadOnlyMemory<byte> data, int blockCount, int blockSize, int channelCount, uint[]? key)
    {
        public short[] Decode(int channel, int codec, int sampleCount)
        {
            var output = new short[sampleCount];
            int written = 0;
            for (int b = 0; b < blockCount && written < sampleCount; b++)
            {
                int start = b * blockSize;
                if (start >= data.Length)
                    break;
                byte[] block = data.Slice(start, Math.Min(blockSize, data.Length - start)).ToArray();
                if (key is not null)
                    Decrypt(block, key);
                if (block.Length < channelCount * 24)
                    break;
                int offsetsStart = channelCount * 24;
                int framesBefore = 0, frames = 0, blockSamples = 0, offsetCount = 0;
                for (int c = 0; c < channelCount; c++)
                {
                    int count = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(c * 24 + 4));
                    if (count < 0 || count > block.Length / AdpcmFrame)
                        throw new InvalidDataException("Fichier son abîmé.");
                    if (c < channel)
                        framesBefore += count;
                    if (c == channel)
                    {
                        frames = count;
                        blockSamples = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(c * 24 + 12));
                    }
                    offsetCount += count;
                }
                int header = offsetsStart + offsetCount * 4;
                header += (AdpcmFrame - header % AdpcmFrame) % AdpcmFrame;
                int from = header + framesBefore * AdpcmFrame;
                int length = Math.Min(frames * AdpcmFrame, block.Length - from);
                if (length <= 0)
                    continue;
                int wanted = Math.Clamp(blockSamples, 0, sampleCount - written);
                var span = block.AsSpan(from, length);
                written += codec == CodecAdpcm
                    ? DecodeAdpcm(span, output.AsSpan(written, wanted))
                    : CopyPcm(span, output.AsSpan(written, wanted));
            }
            return output;
        }
    }

    private static short[] DecodeSingle(ReadOnlyMemory<byte> bytes, uint[]? key, int codec, int sampleCount)
    {
        byte[] data = bytes.ToArray();
        if (key is not null)
            Decrypt(data, key);
        var output = new short[sampleCount];
        if (codec == CodecAdpcm)
            DecodeAdpcm(data, output);
        else
            CopyPcm(data, output);
        return output;
    }

    private static int CopyPcm(ReadOnlySpan<byte> data, Span<short> output)
    {
        int count = Math.Min(output.Length, data.Length / 2);
        for (int i = 0; i < count; i++)
            output[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
        return count;
    }

    private static readonly int[] IndexSteps = [-1, -1, -1, -1, 2, 4, 6, 8];

    private static readonly short[] StepSizes =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
        6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767,
    ];

    // IMA ADPCM in 2048-byte frames: step index (byte), unused byte, starting sample (16 bits), then 2044 bytes of two
    // 4-bit codes each, low half first. Returns the number of samples written.
    internal static int DecodeAdpcm(ReadOnlySpan<byte> data, Span<short> output)
    {
        int written = 0;
        for (int frame = 0; frame < data.Length && written < output.Length; frame += AdpcmFrame)
        {
            var bytes = data.Slice(frame, Math.Min(AdpcmFrame, data.Length - frame));
            if (bytes.Length < 4)
                break;
            int step = Math.Clamp((int)bytes[0], 0, 88);
            int sample = BinaryPrimitives.ReadInt16LittleEndian(bytes[2..]);
            for (int i = 4; i < bytes.Length && written < output.Length; i++)
            {
                for (int half = 0; half < 2 && written < output.Length; half++)
                {
                    int code = half == 0 ? bytes[i] & 0x0F : bytes[i] >> 4;
                    int difference = (((code & 7) * 2 + 1) * StepSizes[step]) >> 3;
                    sample += (code & 8) != 0 ? -difference : difference;
                    sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                    step = Math.Clamp(step + IndexSteps[code & 7], 0, 88);
                    output[written++] = (short)sample;
                }
            }
        }
        return written;
    }

    private static uint[] ToKey(byte[] key)
    {
        if (key.Length != 16)
            throw new ArgumentException("Clé audio invalide.", nameof(key));
        var words = new uint[4];
        for (int i = 0; i < 4; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        return words;
    }

    private const uint Delta = 0x9E3779B9;
    private const uint KeyMask = 0x7B3A207F;

    // XXTEA with an extra constant mixed into the key; works on whole 32-bit words (trailing bytes stay as they are).
    internal static void Decrypt(byte[] data, uint[] key)
    {
        int n = data.Length / 4;
        if (n < 2)
            return;
        var v = new uint[n];
        for (int i = 0; i < n; i++)
            v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        uint sum = unchecked((uint)(6 + 52 / n) * Delta);
        uint y = v[0];
        while (sum != 0)
        {
            uint e = (sum >> 2) & 3;
            for (int p = n - 1; p >= 0; p--)
            {
                uint z = v[p > 0 ? p - 1 : n - 1];
                y = v[p] -= Mix(sum, y, z, key[(p & 3) ^ e]);
            }
            sum -= Delta;
        }
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), v[i]);
    }

    internal static void Encrypt(byte[] data, uint[] key)
    {
        int n = data.Length / 4;
        if (n < 2)
            return;
        var v = new uint[n];
        for (int i = 0; i < n; i++)
            v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        int rounds = 6 + 52 / n;
        uint sum = 0, z = v[n - 1];
        while (rounds-- > 0)
        {
            sum += Delta;
            uint e = (sum >> 2) & 3;
            for (int p = 0; p < n; p++)
            {
                uint y = v[(p + 1) % n];
                z = v[p] += Mix(sum, y, z, key[(p & 3) ^ e]);
            }
        }
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), v[i]);
    }

    private static uint Mix(uint sum, uint y, uint z, uint k) =>
        (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + ((k ^ KeyMask) ^ z));

    internal static uint[] KeyWords(byte[] key) => ToKey(key);
}
