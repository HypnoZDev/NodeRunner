using System.Numerics;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Native.Audio;

namespace NodeRunner.Tests;

public class AudioOcclusionTests
{
    [Fact]
    public void ProxyHashXorsTheArchetypeWithTheTruncatedCentimetres()
    {
        uint hash = AudioOcclusionHash.Of(0x12345678, new Vector3(-84.13119f, 821.3452f, 36.71499f));
        Assert.Equal(0x12345678u ^ unchecked((uint)-8413) ^ 82134u ^ 3671u, hash);
    }

    [Fact]
    public void MovingTheInteriorChangesTheFileName()
    {
        var position = new Vector3(100, 200, 30);
        Assert.NotEqual(AudioOcclusionHash.Of(0xABCDEF01, position), AudioOcclusionHash.Of(0xABCDEF01, position + new Vector3(0.05f, 0, 0)));
    }

    [Fact]
    public void ReaderRefusesOtherFiles() =>
        Assert.Throws<InvalidDataException>(() => new AudioOcclusionReader().Read("<xml/>"u8.ToArray()));
}
