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
    public void GameDataReaderReadsInteriorAndRoomItems()
    {
        using var data = new MemoryStream();
        using (var w = new BinaryWriter(data, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            // Interior item (24 bytes) at 0, room item (68 bytes) at 24, another kind of item (8 bytes) at 92.
            w.Write(0x0000002C); w.Write(0xAAAAA844); w.Write(0x11u); w.Write(0x22u); w.Write(1); w.Write(0x0000BEEFu);
            w.Write(0x0000002E); w.Write(0xAAAAAAAA); w.Write(0x33u); w.Write(0u); w.Write(2u);
            w.Write(0.2f); w.Write(0.1f); w.Write(0f); w.Write(0x44u); w.Write(0u); w.Write(1f); w.Write(0f);
            w.Write(0.8f); w.Write(5f); w.Write(15f); w.Write(0x55u); w.Write(0x11u);
            w.Write(0x00000003); w.Write(0);
        }
        var body = data.ToArray();
        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file))
        {
            w.Write(151); w.Write(body.Length); w.Write(body);
            w.Write(4); w.Write(0); // empty name table
            w.Write(3);
            w.Write(0x12345678u); w.Write(0); w.Write(24);
            w.Write(0x0000BEEFu); w.Write(24); w.Write(68);
            w.Write(0x99u); w.Write(92); w.Write(8);
        }
        var content = new GameDataReader().Read(file.ToArray(), "test.dat151.rel");

        var interior = Assert.Single(content.Interiors);
        Assert.Equal(0x12345678u, interior.NameHash);
        Assert.Equal([0x0000BEEFu], interior.Rooms);
        var room = Assert.Single(content.Rooms);
        Assert.Equal(0x0000BEEFu, room.NameHash);
        Assert.Equal(0x33u, room.RoomName);
        Assert.Equal(0.2f, room.ReverbSmall);
        Assert.Equal(15f, room.DistanceFromPortalFadeDistance);
        Assert.Equal(0x11u, room.WallaSoundSet);
    }

    [Fact]
    public void ReaderRefusesOtherFiles() =>
        Assert.Throws<InvalidDataException>(() => new AudioOcclusionReader().Read("<xml/>"u8.ToArray()));
}
