namespace NodeRunner.App.Audio;

// Writes 16-bit PCM samples (interleaved if several channels) as a .wav file.
internal static class WaveFile
{
    public static void Write(string path, short[] samples, int channels, int sampleRate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataLength = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (short sample in samples)
            writer.Write(sample);
    }
}
