using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
        void Node(int i, short x, short y, ushort firstLink, bool boat)
        {
            var n = data.AsSpan(16 + i * 32, 32);
            BinaryPrimitives.WriteUInt16LittleEndian(n[8..], 29);
            BinaryPrimitives.WriteUInt16LittleEndian(n[10..], (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(n[18..], firstLink);
            BinaryPrimitives.WriteInt16LittleEndian(n[20..], x);
            BinaryPrimitives.WriteInt16LittleEndian(n[22..], y);
            n[31] = (byte)(boat ? 2 : 0);
        }
        Node(0, 8918, -5999, 0, false);
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
        Assert.True(area.Nodes[0].IsVehicle);
        Assert.False(area.Nodes[0].IsBoat);
        Assert.True(area.Nodes[1].IsBoat);
        Assert.False(area.Nodes[2].IsVehicle);
        Assert.Equal([new Gta4PathLink(29, 1, 1, 1), new Gta4PathLink(30, 7, 2, 0)], area.LinksOf(area.Nodes[0]).ToArray());
        Assert.Equal(1, area.Nodes[1].LinkCount);
        Assert.Equal(0, area.Nodes[2].LinkCount);
    }
}
