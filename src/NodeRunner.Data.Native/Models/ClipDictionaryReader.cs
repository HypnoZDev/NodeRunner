using System.Buffers.Binary;
using System.Numerics;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Models;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Models;

// Reads clip dictionaries (.ycd): named clips, each playing one or more animations. Animations are stored in blocks
// of frames ("sequences"), each value of each bone (x, y, z, w) being a channel compressed on its own.
public sealed class ClipDictionaryReader : IAnimationReader
{
    private readonly IGameFileSystem _files;

    public ClipDictionaryReader(IGameFileSystem files)
    {
        _files = files;
    }

    public IReadOnlyDictionary<uint, AnimationClip> ReadClips(string path)
    {
        if (_files.ReadFile(path) is not { } data)
            return new Dictionary<uint, AnimationClip>();
        var header = _files.GetResourceHeader(path) ?? throw new InvalidDataException($"{path} n'est pas une ressource.");
        try
        {
            return Read(data, header);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    // Clip dictionary: 0x28 clip buckets, 0x30 bucket count. Each bucket is a chain of entries (0x00 name hash,
    // 0x08 clip, 0x10 next entry).
    public static Dictionary<uint, AnimationClip> Read(ReadOnlySpan<byte> data, ResourceHeader header)
    {
        var res = new ResourceData(data, ResourcePages.Size(header.SystemFlags));
        var clips = new Dictionary<uint, AnimationClip>();
        var animations = new Dictionary<ulong, AnimationData?>();
        int bucketCount = res.UInt16(0x30);
        int buckets = res.ResolveArray(res.UInt64(0x28), bucketCount, 8, "clips");
        for (int i = 0; i < bucketCount; i++)
        {
            ulong entry = res.UInt64(buckets + i * 8);
            for (int guard = 0; entry != 0 && guard < 0x10000; guard++)
            {
                int e = res.ResolveArray(entry, 1, 0x20, "entrée de clip");
                uint hash = res.UInt32(e);
                if (ReadClip(res, res.UInt64(e + 0x08), hash, animations) is { } clip)
                    clips.TryAdd(hash, clip);
                entry = res.UInt64(e + 0x10);
            }
        }
        return clips;
    }

    // Clip: 0x10 type (1: one animation, 2: a list), 0x18 name. One animation: 0x50 animation, 0x58 start, 0x5C end,
    // 0x60 rate. List: 0x50 entries (0x18 bytes: 0x00 start, 0x04 end, 0x08 rate, 0x10 animation), 0x58 count.
    private static AnimationClip? ReadClip(ResourceData res, ulong pointer, uint hash, Dictionary<ulong, AnimationData?> animations)
    {
        if (pointer == 0)
            return null;
        int c = res.ResolveArray(pointer, 1, 0x70, "clip");
        string? name = res.Text(res.UInt64(c + 0x18), "nom du clip");
        var pieces = new List<(ulong Animation, float Start, float End, float Rate)>();
        switch (res.UInt32(c + 0x10))
        {
            case 1:
                pieces.Add((res.UInt64(c + 0x50), F32(res, c + 0x58), F32(res, c + 0x5C), F32(res, c + 0x60)));
                break;
            case 2:
                int count = res.UInt16(c + 0x58);
                int list = res.ResolveArray(res.UInt64(c + 0x50), count, 0x18, "animations du clip");
                for (int i = 0; i < count; i++)
                {
                    int o = list + i * 0x18;
                    pieces.Add((res.UInt64(o + 0x10), F32(res, o), F32(res, o + 4), F32(res, o + 8)));
                }
                break;
            default:
                return null;
        }
        var parts = new List<ClipPart>();
        foreach (var (animation, start, end, rate) in pieces)
        {
            if (animation == 0)
                continue;
            if (!animations.TryGetValue(animation, out var data))
                animations[animation] = data = ReadAnimation(res, animation);
            if (data is not null)
                parts.Add(new ClipPart(data, start, end, rate));
        }
        return parts.Count == 0 ? null : new AnimationClip { NameHash = hash, Name = name, Parts = parts };
    }

    // Animation: 0x14 frame count, 0x16 frames per sequence, 0x18 duration, 0x40 sequences (pointers) and 0x48 count,
    // 0x50 tracks (4 bytes: bone tag, flags, kind) and 0x58 count.
    private static AnimationData? ReadAnimation(ResourceData res, ulong pointer)
    {
        int a = res.ResolveArray(pointer, 1, 0x60, "animation");
        int frameCount = res.UInt16(a + 0x14);
        int perSequence = res.UInt16(a + 0x16);
        float duration = F32(res, a + 0x18);
        int sequenceCount = res.UInt16(a + 0x48);
        int trackCount = res.UInt16(a + 0x58);
        if (frameCount == 0 || perSequence == 0 || sequenceCount == 0 || trackCount == 0)
            return null;
        int sequences = res.ResolveArray(res.UInt64(a + 0x40), sequenceCount, 8, "séquences de l'animation");
        int ids = res.ResolveArray(res.UInt64(a + 0x50), trackCount, 4, "pistes de l'animation");

        var frames = new Vector4[trackCount][];
        for (int t = 0; t < trackCount; t++)
            frames[t] = new Vector4[frameCount];
        for (int s = 0; s < sequenceCount; s++)
        {
            int first = s * perSequence;
            if (first >= frameCount)
                break;
            ulong block = res.UInt64(sequences + s * 8);
            if (block == 0)
                continue;
            int b = res.ResolveArray(block, 1, 0x20, "séquence");
            int length = (int)res.UInt32(b + 0x04);
            var sequence = new Sequence(
                res.Bytes(res.ResolveArray(block, 1, 0x20 + length, "séquence"), 0x20 + length)[0x20..],
                (int)res.UInt32(b + 0x0C), res.UInt16(b + 0x16), res.UInt16(b + 0x18), res.Byte(b + 0x1E));
            // A block usually holds one frame more than its share (the first of the next one): it gives the last
            // frame of the animation when no block follows.
            var values = sequence.Decode(trackCount);
            for (int t = 0; t < trackCount; t++)
            {
                if (values[t] is not { Length: > 0 } track)
                    continue;
                int count = Math.Min(Math.Max(track.Length, perSequence), frameCount - first);
                for (int f = 0; f < count; f++)
                    frames[t][first + f] = track[Math.Min(f, track.Length - 1)];
            }
        }

        var tracks = new AnimationTrack[trackCount];
        for (int t = 0; t < trackCount; t++)
            tracks[t] = new AnimationTrack { BoneTag = res.UInt16(ids + t * 4), Kind = res.Byte(ids + t * 4 + 3), Frames = frames[t] };
        return new AnimationData { FrameCount = frameCount, Duration = duration, Tracks = tracks };
    }

    private static float F32(ResourceData res, int offset) => BitConverter.UInt32BitsToSingle(res.UInt32(offset));

    // How a channel is stored, by the number the file gives it.
    private enum ChannelType
    {
        StaticQuaternion = 0, // x, y, z; w worked out (positive).
        StaticVector3 = 1,
        StaticFloat = 2,
        RawFloat = 3,         // 32 bits a frame.
        QuantizeFloat = 4,    // n bits a frame: offset + value x quantum.
        IndirectQuantizeFloat = 5, // n bits a frame: number of a value in a table.
        LinearFloat = 6,      // chunks of frames: a start value, then deltas of the slope.
        CachedQuaternion1 = 7, // the 4th component of a quaternion, worked out from the 3 others.
        CachedQuaternion2 = 8,
    }

    private sealed class Channel
    {
        public ChannelType Type;
        public int Track;
        public int Slot;
        public int Bits;      // bits of each frame
        public float Quantum;
        public float Offset;
        public Vector4 Static;
        public float[]? Table;
        public float[]? Values;
    }

    // A block of frames: channel parameters from the start, then frameLength bytes for each frame (bits of the
    // channels that have some, in channel order), then the channel counts of the 9 types (u16) and, for each type,
    // one u16 per channel (track << 2 | slot) padded to 4 entries.
    private readonly ref struct Sequence(ReadOnlySpan<byte> data, int frameOffset, int frameCount, int frameLength, int chunkSize)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public Vector4[]?[] Decode(int trackCount)
        {
            int listAt = frameOffset + frameLength * frameCount;
            int entryAt = listAt + 9 * 2;
            int position = 0;
            var channels = new List<Channel>();
            for (int type = 0; type < 9; type++)
            {
                int count = U16(listAt + type * 2);
                for (int i = 0; i < count; i++)
                {
                    int reference = U16(entryAt);
                    entryAt += 2;
                    var channel = new Channel { Type = (ChannelType)type, Track = reference >> 2, Slot = reference & 3 };
                    position = ReadParameters(channel, position);
                    channels.Add(channel);
                }
                entryAt += (4 - count % 4) % 4 * 2;
            }

            for (int f = 0; f < frameCount; f++)
            {
                long bit = (frameOffset + (long)frameLength * f) * 8;
                foreach (var channel in channels)
                {
                    switch (channel.Type)
                    {
                        case ChannelType.RawFloat:
                            channel.Values![f] = BitConverter.UInt32BitsToSingle(Bits(bit, 32));
                            bit += 32;
                            break;
                        case ChannelType.QuantizeFloat:
                            channel.Values![f] = Bits(bit, channel.Bits) * channel.Quantum + channel.Offset;
                            bit += channel.Bits;
                            break;
                        case ChannelType.IndirectQuantizeFloat:
                            uint index = Bits(bit, channel.Bits);
                            var table = channel.Table!;
                            channel.Values![f] = table.Length == 0 ? channel.Offset : table[Math.Min(index, (uint)table.Length - 1)];
                            bit += channel.Bits;
                            break;
                    }
                }
            }

            var result = new Vector4[]?[trackCount];
            foreach (var group in channels.GroupBy(c => c.Track))
            {
                if (group.Key < trackCount)
                    result[group.Key] = Combine(group.OrderBy(c => c.Slot).ToList());
            }
            return result;
        }

        // Values of a track: its channels fill x, y, z, w in slot order (a static vector or quaternion fills several).
        // A first kind of cached quaternion channel stands for the component found from the 3 others, placed by its
        // slot number; with the second kind, the 4 components are stored and it is left out.
        private Vector4[] Combine(List<Channel> channels)
        {
            var values = new Vector4[frameCount];
            channels.RemoveAll(c => c.Type == ChannelType.CachedQuaternion2);
            var cached = channels.Find(c => c.Type == ChannelType.CachedQuaternion1);
            Span<float> v = stackalloc float[4];
            for (int f = 0; f < frameCount; f++)
            {
                v.Clear();
                int at = 0;
                foreach (var channel in channels)
                {
                    if (channel == cached || at >= 4)
                        continue;
                    switch (channel.Type)
                    {
                        case ChannelType.StaticQuaternion:
                        case ChannelType.StaticVector3:
                            int n = channel.Type == ChannelType.StaticQuaternion ? 4 : 3;
                            for (int k = 0; k < n && at < 4; k++)
                                v[at++] = channel.Static[k];
                            break;
                        default:
                            v[at++] = channel.Values is { } frames ? frames[f] : channel.Static.X;
                            break;
                    }
                }
                if (cached is not null)
                {
                    float w = MathF.Sqrt(Math.Max(1f - (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]), 0f));
                    int place = cached.Slot;
                    for (int k = 3; k > place; k--)
                        v[k] = v[k - 1];
                    v[place] = w;
                }
                values[f] = new Vector4(v[0], v[1], v[2], v[3]);
            }
            return values;
        }

        private int ReadParameters(Channel channel, int position)
        {
            switch (channel.Type)
            {
                case ChannelType.StaticQuaternion:
                    var q = new Vector3(F(position), F(position + 4), F(position + 8));
                    channel.Static = new Vector4(q, MathF.Sqrt(Math.Max(1f - q.LengthSquared(), 0f)));
                    return position + 12;
                case ChannelType.StaticVector3:
                    channel.Static = new Vector4(F(position), F(position + 4), F(position + 8), 0f);
                    return position + 12;
                case ChannelType.StaticFloat:
                    channel.Static = new Vector4(F(position), 0f, 0f, 0f);
                    return position + 4;
                case ChannelType.RawFloat:
                    channel.Values = new float[frameCount];
                    return position;
                case ChannelType.QuantizeFloat:
                    channel.Bits = I32(position);
                    channel.Quantum = F(position + 4);
                    channel.Offset = F(position + 8);
                    channel.Values = new float[frameCount];
                    return position + 12;
                case ChannelType.IndirectQuantizeFloat:
                {
                    channel.Bits = I32(position);
                    int valueBits = I32(position + 4);
                    int ints = I32(position + 8);
                    channel.Quantum = F(position + 12);
                    channel.Offset = F(position + 16);
                    int count = valueBits <= 0 ? 0 : (int)Math.Min((long)ints * 32 / valueBits, 1 << Math.Min(channel.Bits, 24));
                    var table = new float[count];
                    long bit = (position + 20) * 8L;
                    for (int i = 0; i < count; i++, bit += valueBits)
                        table[i] = Bits(bit, valueBits) * channel.Quantum + channel.Offset;
                    channel.Table = table;
                    channel.Values = new float[frameCount];
                    return position + 20 + ints * 4;
                }
                case ChannelType.LinearFloat:
                {
                    int ints = I32(position);
                    channel.Values = Linear(position, I32(position + 4), F(position + 8), F(position + 12));
                    return position + ints * 4;
                }
                default:
                    return position;
            }
        }

        // Frames in chunks: for each chunk a bit offset to its deltas and a start value (all offsets, then all
        // values), then per frame: the change of slope, its low bits first, then its high bits as a count of zeros
        // ended by a 1, then its sign when not zero.
        private float[] Linear(int position, int counts, float quantum, float offset)
        {
            int offsetBits = counts & 0xFF, valueBits = (counts >> 8) & 0xFF, deltaBits = (counts >> 16) & 0xFF;
            var values = new float[frameCount];
            int chunk = chunkSize == 0 ? 64 : chunkSize;
            int chunks = (frameCount + chunk - 1) / chunk;
            long start = (position + 16) * 8L;
            long deltas = start + (long)chunks * (offsetBits + valueBits);
            long end = _data.Length * 8L;
            for (int c = 0; c < chunks; c++)
            {
                long bit = deltas + Bits(start + (long)c * offsetBits, offsetBits);
                int value = (int)Bits(start + (long)chunks * offsetBits + (long)c * valueBits, valueBits);
                int slope = 0;
                for (int j = 0; j < chunk; j++)
                {
                    int frame = c * chunk + j;
                    if (frame >= frameCount)
                        break;
                    values[frame] = value * quantum + offset;
                    if (j + 1 >= chunk)
                        break;
                    int delta = (int)Bits(bit, deltaBits);
                    bit += deltaBits;
                    int zeros = 0;
                    while (bit < end && Bits(bit, 1) == 0)
                    {
                        bit++;
                        zeros++;
                    }
                    bit++;
                    delta |= zeros << deltaBits;
                    if (delta != 0)
                    {
                        if (Bits(bit, 1) == 1)
                            delta = -delta;
                        bit++;
                    }
                    slope += delta;
                    value += slope;
                }
            }
            return values;
        }

        // n bits (at most 32) from a bit position, lowest bit first; zeros past the end.
        private uint Bits(long bit, int n)
        {
            if (n <= 0 || bit < 0)
                return 0;
            int at = (int)(bit >> 3);
            ulong word = 0;
            for (int k = 0; k < 5 && at + k < _data.Length; k++)
                word |= (ulong)_data[at + k] << (8 * k);
            word >>= (int)(bit & 7);
            return (uint)(word & (n >= 32 ? 0xFFFFFFFFUL : (1UL << n) - 1));
        }

        private int U16(int offset) => offset + 2 <= _data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_data[offset..])
            : throw new InvalidDataException("Séquence d'animation tronquée.");

        private int I32(int offset) => offset + 4 <= _data.Length ? BinaryPrimitives.ReadInt32LittleEndian(_data[offset..])
            : throw new InvalidDataException("Séquence d'animation tronquée.");

        private float F(int offset) => BitConverter.Int32BitsToSingle(I32(offset));
    }
}
