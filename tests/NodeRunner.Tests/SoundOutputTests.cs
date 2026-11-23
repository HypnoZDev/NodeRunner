using NodeRunner.App.Audio;

namespace NodeRunner.Tests;

public class SoundOutputTests
{
    [Fact]
    public void PlaysPausesAndSeeks()
    {
        using var output = new SoundOutput();
        var samples = new short[22050 * 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(i * 0.05) * 2000);
        output.Volume = 0f;
        output.Play(samples, 1, 22050);
        if (output.Error is not null)
            return; // No sound device on this machine.

        Thread.Sleep(150);
        Assert.True(output.IsPlaying);
        Assert.InRange(output.Position, 0.01, 1.0);

        output.Pause();
        double paused = output.Position;
        Assert.False(output.IsPlaying);
        Thread.Sleep(50);
        Assert.Equal(paused, output.Position);

        output.Seek(1.5);
        Assert.Equal(1.5, output.Position, 2);
        output.SetLoop(true, 0);
        output.Resume();
        Thread.Sleep(700);
        Assert.True(output.IsPlaying);
        Assert.InRange(output.Position, 0.0, 2.0);
    }
}
