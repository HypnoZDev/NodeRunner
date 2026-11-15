using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Gta4;

namespace NodeRunner.Tests;

// GTA IV ".img" archives built in memory (no game file): table, names, resources, encryption.
public class Gta4ArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noderunner-img-" + Guid.NewGuid().ToString("N"));

    public Gta4ArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly byte[] PlainFile = Encoding.ASCII.GetBytes("placement file, stored as is");
    private static readonly byte[] ResourceContent = Enumerable.Range(0, 0x1000).Select(i => (byte)(i * 7)).ToArray();
    private const uint ResourceFlags = 0xC0000010; // 0x10 << 8 = 0x1000 system bytes, no graphics
    private const uint DrawableType = 0x6E;

    [Fact]
    public void Read_PlainArchive_ListsFilesAndReadsThem()
    {
        string path = Path.Combine(_folder, "plain.img");
        File.WriteAllBytes(path, BuildArchive(null));
        using var handle = File.OpenHandle(path);

        Assert.False(ImgArchive.IsEncrypted(handle));
        var entries = ImgArchive.Read(handle, 0, null);

        Assert.Equal(["test.wpl", "model.wdr"], entries.Select(e => e.Name));
        Assert.False(entries[0].IsResource);
        Assert.Equal(PlainFile.Length, entries[0].Size);
        Assert.True(entries[1].IsResource);
        Assert.Equal(DrawableType, entries[1].ResourceType);
        Assert.Equal(ResourceFlags, entries[1].ResourceFlags);
    }

    [Fact]
    public void Read_EncryptedArchive_NeedsTheKey()
    {
        var cipher = new Gta4Cipher(RandomNumberGenerator.GetBytes(32));
        string path = Path.Combine(_folder, "encrypted.img");
        File.WriteAllBytes(path, BuildArchive(cipher));
        using var handle = File.OpenHandle(path);

        Assert.True(ImgArchive.IsEncrypted(handle));
        Assert.Throws<InvalidDataException>(() => ImgArchive.Read(handle, 0, null));
        Assert.Throws<InvalidDataException>(() => ImgArchive.Read(handle, 0, new Gta4Cipher(RandomNumberGenerator.GetBytes(32))));
        Assert.Equal(["test.wpl", "model.wdr"], ImgArchive.Read(handle, 0, cipher).Select(e => e.Name));
    }

    [Fact]
    public void FileSystem_ReadsArchivesAndLooseFiles()
    {
        string maps = Path.Combine(_folder, "pc", "data", "maps");
        Directory.CreateDirectory(maps);
        File.WriteAllBytes(Path.Combine(maps, "area.img"), BuildArchive(null));
        File.WriteAllText(Path.Combine(maps, "area.ide"), "objs\nend\n");
        File.WriteAllBytes(Path.Combine(_folder, "GTAIV.exe"), []);

        Assert.True(Gta4InstallLocator.IsGameFolder(_folder));
        Assert.Equal(_folder, Gta4InstallLocator.Resolve(_folder));
        using var files = new Gta4FileSystem();
        files.Open(_folder);

        Assert.Empty(files.Errors);
        Assert.Equal([@"pc\data\maps\area.img"], files.EnumerateArchives());
        Assert.Equal(PlainFile, files.ReadFile(@"PC\data\maps\area.img\TEST.WPL"));
        Assert.Equal(ResourceContent, files.ReadFile(@"pc\data\maps\area.img\model.wdr"));
        Assert.Equal([@"pc\data\maps\area.img\model.wdr"], files.EnumerateFiles("wdr"));
        Assert.Equal("objs\nend\n", Encoding.ASCII.GetString(files.ReadFile(@"pc\data\maps\area.ide")!));
        Assert.Null(files.ReadFile(@"..\outside.txt"));

        var info = files.GetInfo(@"pc\data\maps\area.img\model.wdr")!.Value;
        Assert.True(info.IsResource);
        Assert.Equal(ResourceContent.Length, info.Size);
        Assert.Equal(ImgArchive.ResourceMagic, BinaryPrimitives.ReadUInt32LittleEndian(files.ReadStoredFile(@"pc\data\maps\area.img\model.wdr")));
    }

    [Fact]
    public void FileSystem_EncryptedArchiveWithoutKey_IsReported()
    {
        File.WriteAllBytes(Path.Combine(_folder, "locked.img"), BuildArchive(new Gta4Cipher(RandomNumberGenerator.GetBytes(32))));
        File.WriteAllBytes(Path.Combine(_folder, "GTAIV.exe"), new byte[4096]);

        using var files = new Gta4FileSystem();
        files.Open(_folder);

        Assert.Empty(files.EnumerateArchives());
        Assert.Equal(2, files.Errors.Count);
    }

    private static byte[] BuildArchive(Gta4Cipher? cipher)
    {
        byte[] resource = BuildResource();
        string[] names = ["test.wpl", "model.wdr"];
        byte[] nameBytes = [.. names.SelectMany(n => Encoding.ASCII.GetBytes(n + "\0"))];
        int tableSize = names.Length * 16 + nameBytes.Length;

        var output = new byte[3 * ImgArchive.BlockSize + resource.Length + ImgArchive.BlockSize];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, ImgArchive.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], names.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], tableSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], 16);

        var table = span.Slice(ImgArchive.HeaderSize, tableSize);
        WriteEntry(table, (uint)PlainFile.Length, 0, 1, PlainFile.Length, resource: false);
        WriteEntry(table[16..], ResourceFlags, DrawableType, 2, resource.Length, resource: true);
        nameBytes.CopyTo(table[32..]);

        PlainFile.CopyTo(span[ImgArchive.BlockSize..]);
        resource.CopyTo(span[(2 * ImgArchive.BlockSize)..]);

        if (cipher is not null)
        {
            cipher.Encrypt(table);
            cipher.Encrypt(span[..16]);
        }
        return output;
    }

    private static void WriteEntry(Span<byte> e, uint sizeOrFlags, uint type, uint block, int size, bool resource)
    {
        int blocks = (size + ImgArchive.BlockSize - 1) / ImgArchive.BlockSize;
        BinaryPrimitives.WriteUInt32LittleEndian(e, sizeOrFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(e[4..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], block);
        BinaryPrimitives.WriteUInt16LittleEndian(e[12..], (ushort)blocks);
        BinaryPrimitives.WriteUInt16LittleEndian(e[14..], (ushort)((resource ? 0x2000 : 0) | (blocks * ImgArchive.BlockSize - size)));
    }

    private static byte[] BuildResource()
    {
        using var output = new MemoryStream();
        var header = new byte[ImgArchive.ResourceHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ImgArchive.ResourceMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), DrawableType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), ResourceFlags);
        output.Write(header);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(ResourceContent);
        return output.ToArray();
    }
}

// GTA IV path nodes built in memory: positions, links between areas, vehicle / pedestrian / boat nodes, lanes.
public class Gta4PathNodesTests
{
    [Fact]
    public void Read_DecodesNodesAndLinks()
    {
        // Two vehicle nodes (the second a boat node) and one pedestrian node; 3 links.
        var data = new byte[16 + 3 * 32 + 3 * 8];
        var span = data.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, 3);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 3);
        void Node(int i, short x, short y, ushort firstLink, bool boat, short z = 0)
        {
            var n = data.AsSpan(16 + i * 32, 32);
            BinaryPrimitives.WriteUInt16LittleEndian(n[8..], 29);
            BinaryPrimitives.WriteUInt16LittleEndian(n[10..], (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(n[18..], firstLink);
            BinaryPrimitives.WriteInt16LittleEndian(n[20..], x);
            BinaryPrimitives.WriteInt16LittleEndian(n[22..], y);
            BinaryPrimitives.WriteInt16LittleEndian(n[24..], z);
            n[31] = (byte)(boat ? 2 : 0);
        }
        Node(0, 8918, -5999, 0, false, 1962);
        Node(1, 80, 16, 2, true);
        Node(2, 0, 0, 3, false);
        void Link(int i, ushort area, ushort node, byte lanes)
        {
            var l = data.AsSpan(16 + 3 * 32 + i * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(l, area);
            BinaryPrimitives.WriteUInt16LittleEndian(l[2..], node);
            l[5] = lanes;
        }
        Link(0, 29, 1, 0x09);
        Link(1, 30, 7, 0x02);
        Link(2, 29, 0, 0x09);

        var area = Gta4PathNodes.Read(data, 29);

        Assert.Equal(3, area.Nodes.Length);
        Assert.Equal(1114.75f, area.Nodes[0].Position.X);
        Assert.Equal(-749.875f, area.Nodes[0].Position.Y);
        Assert.Equal(30.65625f, area.Nodes[0].Position.Z);
        Assert.True(area.Nodes[0].IsVehicle);
        Assert.False(area.Nodes[0].IsBoat);
        Assert.True(area.Nodes[1].IsBoat);
        Assert.False(area.Nodes[2].IsVehicle);
        Assert.Equal([new Gta4PathLink(29, 1, 1, 1), new Gta4PathLink(30, 7, 2, 0)], area.LinksOf(area.Nodes[0]).ToArray());
        Assert.Equal(1, area.Nodes[1].LinkCount);
        Assert.Equal(0, area.Nodes[2].LinkCount);
    }
}

// GTA IV map content built in memory: ".ide" definitions, ".wpl" placements, the "gta.dat" load list.
public class Gta4MapTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noderunner-iv-map-" + Guid.NewGuid().ToString("N"));

    public Gta4MapTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private const string Ide = """
        objs
        Tower_MH1, 10_MH1, 150, 12582912, 0, -29.5, -35.25, -90.1, 29.5, 35.25, 90.1, 0, 0, 0, 101.15, LOD_Tower_MH1
        broken line, 1, 2
        end
        tobj
        Sign_NL_MH1, Nightlight_MH1, 55, 12591104, 0, -11, -9.5, -1.75, 10.75, 9.25, 1.25, 0, 0, 0, 14.5, null, 33423487
        end
        anim
        Flag_MH1, 2_MH1, manhat01, 60, 1024, 0, -18.75, 20.5, -2.5, -0.125, 34.75, 2.75, -9.5, 27.5, 0.125, 12, null
        end
        txdp
        road_mh1,mh_roads
        end
        """;

    [Fact]
    public void Definitions_ReadModelSections()
    {
        var defs = new Gta4Definitions();

        Assert.Equal(1, defs.Read(Ide));

        Assert.Equal(3, defs.Models.Count);
        var tower = defs.Find(Joaat.Hash("tower_mh1"))!;
        Assert.Equal("Tower_MH1", tower.Name);
        Assert.Equal("10_MH1", tower.TextureDictionary);
        Assert.Equal(150, tower.DrawDistance);
        Assert.Equal(12582912u, tower.Flags);
        Assert.Equal(new Vector3(-29.5f, -35.25f, -90.1f), tower.BoxMin);
        Assert.Equal(new Vector3(29.5f, 35.25f, 90.1f), tower.BoxMax);
        Assert.Equal(101.15f, tower.SphereRadius);
        Assert.Equal("LOD_Tower_MH1", tower.DrawableDictionary);
        Assert.False(tower.IsTimed);
        Assert.True(defs.Find(Joaat.Hash("Sign_NL_MH1"))!.IsTimed);
        Assert.Null(defs.Find(Joaat.Hash("Sign_NL_MH1"))!.DrawableDictionary);
        Assert.Equal(33423487u, defs.Find(Joaat.Hash("Sign_NL_MH1"))!.TimeFlags);
        var flag = defs.Find(Joaat.Hash("Flag_MH1"))!;
        Assert.Equal("manhat01", flag.AnimationDictionary);
        Assert.Equal(60, flag.DrawDistance);
        Assert.Equal(new Vector3(-9.5f, 27.5f, 0.125f), flag.SphereCenter);
        Assert.Equal("mh_roads", defs.TextureParents["ROAD_MH1"]);
    }

    // Same layout as the game's "mlo" sections (values made up).
    private const string InteriorIde = """
        mlo
        shop_int, 0, 2, 1, 3, 150, -1, -1
        	shop_walls, 1.5, -2, 0.25, -0, -0, -0.707107, -0.707107, 0, 384,
        	shop_door, 0, 4, 0, -0, -0, -0, -1, 0, 384,
        	shop_lod, 0, 0, 0, -0, -0, -0, -1, 1, 384,
        mloroomstart
        	limbo, 1, 1, 10, 10, 5, -10, -10, -1, 1, 0, 0
        	2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        	roomend
        	shoproom, 2, 1, 4, 4, 3, -4, -4, 0, 1, 12345, 96
        	0, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        	roomend
        mloportalstart
        	1, 0, -1, 4, 0, 1, 4, 0, 1, 4, 2, -1, 4, 2, 1, -1, -1, -1, 64, 16777215, 1
        mloend
        end
        objs
        shop_walls, shop, 100, 0, 0, -4, -4, 0, 4, 4, 3, 0, 0, 1.5, 6, null
        end
        """;

    [Fact]
    public void Definitions_ReadInteriors()
    {
        var defs = new Gta4Definitions();

        Assert.Equal(0, defs.Read(InteriorIde));

        Assert.NotNull(defs.Find(Joaat.Hash("shop_walls")));
        var shop = defs.Interiors[Joaat.Hash("shop_int")];
        Assert.Equal(3, shop.Objects.Length);
        Assert.Equal(new Vector3(1.5f, -2, 0.25f), shop.Objects[0].Position);
        Assert.Equal(new Quaternion(-0f, -0f, -0.707107f, -0.707107f), shop.Objects[0].Rotation);
        Assert.Equal(1, shop.Objects[2].Lod);
        Assert.Equal(["limbo", "shoproom"], shop.Rooms.Select(r => r.Name));
        Assert.Equal([0, 1], shop.Rooms[1].Objects);
        Assert.Equal(new Vector3(-4, -4, 0), shop.Rooms[1].BoxMin);
        Assert.Equal(new Vector3(4, 4, 3), shop.Rooms[1].BoxMax);
        Assert.Equal(12345u, shop.Rooms[1].TimecycleHash);
        var portal = Assert.Single(shop.Portals);
        Assert.Equal((1, 0), (portal.RoomFrom, portal.RoomTo));
        Assert.Equal(new Vector3(1, 4, 2), portal.Corners[2]);
        Assert.Equal([1], portal.Objects);
    }

    private static byte[] Placements(params (Vector3 Position, Quaternion Stored, string Model, int LodParent)[] instances)
    {
        var data = new byte[68 + instances.Length * 48 + 56];
        BinaryPrimitives.WriteInt32LittleEndian(data, 3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), instances.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1); // one car generator after the instances
        for (int i = 0; i < instances.Length; i++)
        {
            var s = data.AsSpan(68 + i * 48);
            var (p, q, model, lod) = instances[i];
            float[] values = [p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W];
            for (int v = 0; v < values.Length; v++)
                BinaryPrimitives.WriteSingleLittleEndian(s[(v * 4)..], values[v]);
            BinaryPrimitives.WriteUInt32LittleEndian(s[28..], Joaat.Hash(model));
            BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 0x180);
            BinaryPrimitives.WriteInt32LittleEndian(s[36..], lod);
            BinaryPrimitives.WriteSingleLittleEndian(s[44..], -1);
        }
        return data;
    }

    [Fact]
    public void Placements_ReadInstancesWithTheRotationInverted()
    {
        var stored = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
        var data = Placements((new Vector3(91.75f, -958.25f, 13.5f), stored, "Tower_MH1", 7));

        var instance = Assert.Single(Gta4Placements.Read(data));

        Assert.Equal(Joaat.Hash("tower_mh1"), instance.ModelHash);
        Assert.Equal(new Vector3(91.75f, -958.25f, 13.5f), instance.Position);
        Assert.True(Quaternion.Dot(Quaternion.Conjugate(stored), instance.Orientation) > 0.9999f);
        Assert.Equal(0x180u, instance.Flags);
        Assert.Equal(7, instance.LodParent);
        Assert.Equal(-1, instance.LodDistance);
        Assert.Throws<InvalidDataException>(() => Gta4Placements.Read(data.AsSpan(0, 100)));
        data[0] = 2;
        Assert.Throws<InvalidDataException>(() => Gta4Placements.Read(data));
    }

    [Theory]
    [InlineData("platform:/DATA/MAPS/MANHAT/MANHAT01.IPL", @"pc\data\maps\manhat\manhat01.ipl")]
    [InlineData("common:/DATA/gtxd.IDE", @"common\data\gtxd.ide")]
    [InlineData("platformimg:/data/maps/props/x", null)]
    [InlineData("platform:/../outside.ide", null)]
    public void GamePath_MapsDevicesToFolders(string device, string? expected) => Assert.Equal(expected, Gta4Map.GamePath(device));

    [Fact]
    public void ApplyEpisode_ReplacesPlacementsAndAddsDefinitions()
    {
        List<(string Kind, string Path)> base_ =
        [
            ("IDE", @"pc\data\maps\manhat\manhat01.ide"),
            ("IPL", @"pc\data\maps\manhat\manhat01.ipl"),
            ("IPL", @"common\data\maps\ambient.ipl"),
            ("IPL", @"pc\data\maps\manhat\manhat02.ipl"),
        ];
        const string content = """
            # data files
            RADIO common/data/radiohud.dat
            IDE pc/data/maps/manhat/manhat01.ide
            IPL pc/data/maps/manhat/manhat01.ipl
            DELAYED_IDE pc/data/maps/interiors/Int_1.ide
            DISABLE_FILE common:/DATA/MAPS/AMBIENT.IPL
            IPL ../outside.ipl
            """;

        var entries = Gta4Map.ApplyEpisode(base_, "TLAD", content);

        Assert.Equal(
        [
            ("IDE", @"pc\data\maps\manhat\manhat01.ide"),
            ("IPL", @"tlad\pc\data\maps\manhat\manhat01.ipl"),
            ("IPL", @"pc\data\maps\manhat\manhat02.ipl"),
            ("IDE", @"tlad\pc\data\maps\manhat\manhat01.ide"),
            ("IDE", @"tlad\pc\data\maps\interiors\int_1.ide"),
        ], entries);
    }

    // An unencrypted ".img" holding one file stored as is.
    private static byte[] PlainArchive(string name, byte[] content)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name + "\0");
        int blocks = (content.Length + ImgArchive.BlockSize - 1) / ImgArchive.BlockSize;
        var output = new byte[(1 + blocks) * ImgArchive.BlockSize];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, ImgArchive.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 16 + nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], 16);
        var e = span[ImgArchive.HeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)content.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(e[12..], (ushort)blocks);
        BinaryPrimitives.WriteUInt16LittleEndian(e[14..], (ushort)(blocks * ImgArchive.BlockSize - content.Length));
        nameBytes.CopyTo(e[16..]);
        content.CopyTo(span[ImgArchive.BlockSize..]);
        return output;
    }

    [Fact]
    public void Load_ReadsTheMapOfTheLoadList()
    {
        string data = Path.Combine(_folder, "common", "data");
        string manhat = Path.Combine(_folder, "pc", "data", "maps", "manhat");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(manhat);
        File.WriteAllText(Path.Combine(data, "gta.dat"), """
            # load list
            IDE platform:/DATA/MAPS/MANHAT/MANHAT01.IDE
            IPL common:/DATA/MAPS/PATHS.IPL
            IPL platform:/DATA/MAPS/MANHAT/MANHAT01.IPL
            """);
        File.WriteAllText(Path.Combine(manhat, "manhat01.ide"), Ide);
        var lod = (new Vector3(100, -950, 20), Quaternion.Identity, "LOD_Tower_MH1", -1);
        File.WriteAllBytes(Path.Combine(manhat, "manhat01.wpl"), Placements(lod, lod));
        File.WriteAllBytes(Path.Combine(manhat, "manhat01.img"),
            PlainArchive("manhat01_stream0.wpl", Placements((new Vector3(91, -958, 13), Quaternion.Identity, "Tower_MH1", 1))));
        File.WriteAllBytes(Path.Combine(_folder, "GTAIV.exe"), []);
        using var files = new Gta4FileSystem();
        files.Open(_folder);

        var map = Gta4Map.Load(files);

        Assert.Equal(["ligne(s) illisible(s)"], map.Errors.Select(e => e[(e.IndexOf(": ", StringComparison.Ordinal) + 4)..]));
        Assert.Equal(3, map.Definitions.Models.Count);
        Assert.Equal([@"pc\data\maps\manhat\manhat01.wpl", @"pc\data\maps\manhat\manhat01.img\manhat01_stream0.wpl"],
            map.Files.Select(f => f.Path));
        Assert.Equal(3, map.InstanceCount);
        var streamed = map.Files[1];
        Assert.Same(map.Files[0], streamed.LodFile);
        Assert.Same(map.Files[0], map.Files[0].LodFile);
        Assert.Equal(Joaat.Hash("LOD_Tower_MH1"), streamed.LodParentOf(streamed.Instances[0])!.Value.ModelHash);
        Assert.Null(map.Files[0].LodParentOf(map.Files[0].Instances[0]));
    }
}
