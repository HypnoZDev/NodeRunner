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
    public void ReaderRefusesOtherFiles() =>
        Assert.Throws<InvalidDataException>(() => new AudioOcclusionReader().Read("<xml/>"u8.ToArray()));
}
