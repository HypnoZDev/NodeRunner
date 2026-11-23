namespace NodeRunner.Data.Audio;

// One sound of a sound bank (.awc): mono 16-bit samples, decoded on demand (a long stream is several megabytes).
// Channels of a multi-channel stream (music, radio, stereo ambience) are separate tracks sharing the same Group.
public sealed class SoundTrack
{
    public required uint Id { get; init; }           // Low 29 bits of the hash of the sound's name.
    public required int SampleRate { get; init; }
    public required int SampleCount { get; init; }
    public short Headroom { get; init; }             // Hundredths of a decibel the sound was lowered by.
    public int LoopStart { get; init; } = -1;        // Sample where a loop starts again; -1 if it does not loop.
    public int Group { get; init; } = -1;            // Index of the multi-channel stream it belongs to, or -1.
    public bool IsCompressed { get; init; }          // ADPCM (else plain PCM).
    public required Func<short[]> Decode { get; init; }

    public double Seconds => SampleRate > 0 ? (double)SampleCount / SampleRate : 0;
}

public sealed record SoundBank(IReadOnlyList<SoundTrack> Tracks);

public interface ISoundBankReader
{
    // Throws InvalidDataException if the file is not a readable sound bank.
    SoundBank Read(byte[] data);
}
