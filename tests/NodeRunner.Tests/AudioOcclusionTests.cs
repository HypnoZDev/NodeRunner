using System.Numerics;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
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
    public void GameDataReaderReadsAmbientZonesAndStaticEmitters()
    {
        var zone = new byte[232 + 2 * 4 + 4 + 8];
        var z = zone.AsSpan();
        z[0] = 37;
        BitConverter.TryWriteBytes(z[4..], 0x5u);
        z[8] = 2; // line
        void V(Span<byte> s, int at, float x, float y, float h)
        {
            BitConverter.TryWriteBytes(s[at..], x); BitConverter.TryWriteBytes(s[(at + 4)..], y); BitConverter.TryWriteBytes(s[(at + 8)..], h);
        }
        V(z, 16, 1, 2, 3); V(z, 32, 40, 50, 60); BitConverter.TryWriteBytes(z[80..], (ushort)45);
        V(z, 96, 10, 20, 30); V(z, 112, 11, 21, 31);
        BitConverter.TryWriteBytes(z[176..], 0.5f);
        BitConverter.TryWriteBytes(z[220..], 0x77u);
        z[228] = 3; z[230] = 2;
        BitConverter.TryWriteBytes(z[232..], 0xA1u); BitConverter.TryWriteBytes(z[236..], 0xA2u);
        z[240] = 1;
        BitConverter.TryWriteBytes(z[244..], 0xD1u); BitConverter.TryWriteBytes(z[248..], 0.25f);

        var emitter = new byte[92];
        var e = emitter.AsSpan();
        e[0] = 6;
        BitConverter.TryWriteBytes(e[8..], 0xC1u); BitConverter.TryWriteBytes(e[12..], 0xC2u);
        V(e, 16, -100, 200, 30);
        BitConverter.TryWriteBytes(e[28..], 2f); BitConverter.TryWriteBytes(e[32..], 25f); BitConverter.TryWriteBytes(e[36..], -300);
        BitConverter.TryWriteBytes(e[48..], 0xE1u);
        BitConverter.TryWriteBytes(e[80..], (ushort)1200); BitConverter.TryWriteBytes(e[82..], (ushort)360);

        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(151); w.Write(zone.Length + emitter.Length); w.Write(zone); w.Write(emitter);
            w.Write(0);
            w.Write(2);
            w.Write(0x1111u); w.Write(0); w.Write(zone.Length);
            w.Write(0x2222u); w.Write(zone.Length); w.Write(emitter.Length);
        }
        var content = new GameDataReader().Read(file.ToArray(), "test.dat151.rel");

        var read = Assert.Single(content.AmbientZones);
        Assert.Equal(0x1111u, read.NameHash);
        Assert.Equal(AudioZoneShape.Line, read.Shape);
        Assert.Equal(new AudioZoneVolume(new Vector3(1, 2, 3), new Vector3(40, 50, 60), 45), read.Activation);
        Assert.Equal(new Vector3(11, 21, 31), read.Positioning.Size);
        Assert.Equal(0.5f, read.BuiltUpFactor);
        Assert.Equal(0x77u, read.PedWalla);
        Assert.Equal(3, read.RulesToPlay);
        Assert.Equal([0xA1u, 0xA2u], read.Rules);
        Assert.Equal([new DirectionalAmbience(0xD1u, 0.25f)], read.DirectionalAmbiences);

        var source = Assert.Single(content.StaticEmitters);
        Assert.Equal(0x2222u, source.NameHash);
        Assert.Equal((0xC1u, 0xC2u), (source.ChildSound, source.RadioStation));
        Assert.Equal(new Vector3(-100, 200, 30), source.Position);
        Assert.Equal((2f, 25f, -300), (source.MinDistance, source.MaxDistance, source.Volume));
        Assert.Equal(0xE1u, source.Interior);
        Assert.Equal((1200, 360), (source.StartMinutes, source.EndMinutes));
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
    public void BuilderLinksPortalsAndChainsPathsWithoutCrossingTheOutside()
    {
        // Outside, hall and office; a door between the outside and the hall, a mirror in the office.
        var door = new MapEntity { ArchetypeHash = 70, Position = Vector3.Zero, InteriorPortal = 0 };
        var interior = new Archetype
        {
            NameHash = 50,
            Kind = ArchetypeKind.Interior,
            InteriorEntities = [door],
            InteriorRooms = [new(Vector3.Zero, Vector3.One) { Name = "limbo" }, new(Vector3.Zero, Vector3.One) { Name = "hall" },
                new(Vector3.Zero, Vector3.One) { Name = "office" }],
            InteriorPortals = [new(1, 0, 0), new(2, 2, 0x4), new(1, 2, 0)],
        };
        var doorType = new Archetype { NameHash = 70, Flags = 0x4000000 | 0x20000 };
        const uint proxy = 0x0D55AD60;

        var built = AudioOcclusionBuilder.Build(interior, proxy, h => h == 70 ? doorType : null);

        // One passage per portal and direction; the mirror is counted in the office's ranks but not linked.
        Assert.Equal([(0, 0, 1), (1, 0, 0), (1, 1, 2), (2, 1, 1)],
            built.Links.Select(l => (l.RoomIndex, l.PortalIndex, l.DestRoomIndex)));
        var muffled = Assert.Single(built.Links[0].Entities);
        Assert.True(muffled.IsDoor);
        Assert.Equal(AudioOcclusionBuilder.DoorOcclusion, muffled.MaxOcclusion);

        uint outside = AudioOcclusionBuilder.OutsideRoomKey, hall = proxy ^ Joaat.Hash("hall"), office = proxy ^ Joaat.Hash("office");
        var nodes = built.Paths.ToDictionary(n => n.Key);
        // Outside -> office in 2 portals: through the hall, using the node hall -> office of depth 1.
        var far = nodes[AudioOcclusionBuilder.PathKey(outside, office, 2)];
        Assert.Equal([new AudioPathChild(AudioOcclusionBuilder.PathKey(hall, office, 1), 0)], far.Children);
        // Office -> hall never goes through the outside; hall -> office in 3 portals still exists (direct one).
        Assert.DoesNotContain(AudioOcclusionBuilder.PathKey(office, outside, 1), nodes.Keys);
        Assert.Contains(AudioOcclusionBuilder.PathKey(hall, office, 3), nodes.Keys);
        // Nodes made of direct portals only come first.
        Assert.All(built.Paths.TakeWhile(n => n.Children.All(c => c.ParentKey == 0)), n => Assert.True(n.Children.Count > 0));
        Assert.Contains(built.Paths.SkipWhile(n => n.Children.All(c => c.ParentKey == 0)), n => n == far);
    }

    [Fact]
    public void NewInteriorGetsItsAudioSettingsWrittenAndReadBack()
    {
        var interior = new Archetype
        {
            NameHash = 0x1234ABCD,
            Kind = ArchetypeKind.Interior,
            InteriorRooms = [new(Vector3.Zero, Vector3.One) { Name = "limbo" }, new(Vector3.Zero, Vector3.One) { Name = "hall" },
                new(Vector3.Zero, Vector3.One) { Name = "office" }, new(Vector3.Zero, Vector3.One) { Name = "hall" }],
        };

        var (settings, rooms) = InteriorAudioBuilder.Build(interior, null);
        Assert.Equal(3, rooms.Count); // One item per room name.
        Assert.Equal(1f, rooms[0].ExteriorAudibility);
        Assert.Equal(Joaat.Hash("office"), rooms[2].RoomName);
        Assert.Equal(rooms.Select(r => r.NameHash), settings.Rooms);

        var bytes = GameDataWriter.Write([settings], rooms);
        var read = new GameDataReader().Read(bytes, "test");
        Assert.Equal(settings with { File = "test" }, read.Interiors.Single() with { Rooms = settings.Rooms });
        Assert.Equal(settings.Rooms, read.Interiors.Single().Rooms);
        Assert.Equal(rooms.Select(r => r with { File = "test" }), read.Rooms.OrderBy(r => rooms.FindIndex(x => x.NameHash == r.NameHash)));

        // Index sorted like the game's files, then the offsets of the item references: 3 ambient zones, 3 rooms.
        int dataLength = BitConverter.ToInt32(bytes, 4);
        int at = 8 + dataLength + 8;
        int count = BitConverter.ToInt32(bytes, at);
        var names = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(bytes, at + 4 + i * 12)).ToList();
        Assert.Equal(names.OrderBy(n => System.Numerics.BitOperations.RotateRight(n, 8)), names);
        Assert.Equal(6, BitConverter.ToInt32(bytes, at + 4 + count * 12));
    }

    [Fact]
    public void ReaderRefusesOtherFiles() =>
        Assert.Throws<InvalidDataException>(() => new AudioOcclusionReader().Read("<xml/>"u8.ToArray()));
}
