using System.Text;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Textures;
using NodeRunner.Data.Textures;

namespace NodeRunner.Tests;

public class TextureTests
{
    [Theory]
    [InlineData(TextureFormat.Bc1, 256, 256, 512, 32768)]
    [InlineData(TextureFormat.Bc3, 256, 128, 1024, 32768)]
    [InlineData(TextureFormat.Bc1, 2, 2, 8, 8)]      // Smaller than one block: still one whole block.
    [InlineData(TextureFormat.B8G8R8A8, 16, 8, 64, 512)]
    [InlineData(TextureFormat.A8, 16, 8, 16, 128)]
    public void Computes_pitch(TextureFormat format, int width, int height, int expectedRow, int expectedSlice)
    {
        Assert.Equal((expectedRow, expectedSlice), TextureLayout.Pitch(format, width, height));
    }

    [Fact]
    public void Mip_levels_follow_each_other_and_stop_when_data_is_too_short()
    {
        // 16x16 DXT5: 256 + 64 + 16 (+16 for 2x2, +16 for 1x1). The game files often cut the smallest levels.
        var texture = Texture(TextureFormat.Bc3, 16, 16, levels: 5, dataLength: 256 + 64 + 16);
        var levels = TextureLayout.GetMipLevels(texture);

        Assert.Equal(3, levels.Count);
        Assert.Equal(new MipLevel(16, 16, 0, 64, 256), levels[0]);
        Assert.Equal(new MipLevel(8, 8, 256, 32, 64), levels[1]);
        Assert.Equal(new MipLevel(4, 4, 320, 16, 16), levels[2]);
    }

    [Fact]
    public void Reads_parents_from_xml()
    {
        var xml = "﻿<?xml version=\"1.0\"?><CMapParentTxds><txdRelationships>"
            + "<Item><parent>DowntownRD</parent><child>hei_DT1_03_Build</child></Item>"
            + "<Item><parent> GlobalRoads </parent><child>ap1_01</child></Item>"
            + "<Item><parent></parent><child>ignored</child></Item>"
            + "</txdRelationships></CMapParentTxds>";

        var links = new GtxdReader().ReadParents(Encoding.UTF8.GetBytes(xml));

        Assert.Equal([new("hei_DT1_03_Build", "DowntownRD"), new("ap1_01", "GlobalRoads")], links);
    }

    [Fact]
    public void Reads_parents_from_rbf()
    {
        var rbf = new RbfBuilder();
        rbf.Open(0, "CMapParentTxds");
        rbf.UInt(1, "version", 3);          // A value element, to check it is skipped correctly.
        rbf.Open(2, "txdRelationships");
        foreach (var (parent, child) in new[] { ("DowntownRD", "dt1_03"), ("GlobalRoads", "ap1_01") })
        {
            rbf.Open(3, "item");
            rbf.Open(4, "parent").Text(parent).Close();
            rbf.Open(5, "child").Text(child).Close();
            rbf.Close();
        }
        rbf.Close().Close();

        var links = new GtxdReader().ReadParents(rbf.ToArray());

        Assert.Equal([new("dt1_03", "DowntownRD"), new("ap1_01", "GlobalRoads")], links);
    }

    [Fact]
    public void Damaged_rbf_is_reported()
    {
        var rbf = new RbfBuilder();
        rbf.Open(0, "CMapParentTxds");      // Never closed.

        Assert.Throws<InvalidDataException>(() => new GtxdReader().ReadParents(rbf.ToArray()));
    }

    [Fact]
    public void Search_chain_follows_parents_and_skips_missing_files()
    {
        uint child = Joaat.Hash("dt1_03"), middle = Joaat.Hash("downtownrd"), root = Joaat.Hash("globalroads");
        var catalog = new TextureCatalog(
            new Dictionary<uint, string> { [child] = "a/dt1_03.ytd", [root] = "b/globalroads.ytd" },
            new Dictionary<uint, uint> { [child] = middle, [middle] = root, [root] = child }); // Loop: must stop.

        Assert.Equal(["a/dt1_03.ytd", "b/globalroads.ytd"], catalog.GetSearchChain(child));
        Assert.Empty(catalog.GetSearchChain(0));
    }

    private static TextureData Texture(TextureFormat format, int width, int height, int levels, int dataLength) => new()
    {
        NameHash = 1,
        Name = "test",
        Width = width,
        Height = height,
        MipLevels = levels,
        Format = format,
        Data = new byte[dataLength],
    };

    // Writes a small RBF file. Descriptor indices must be given in order of first use.
    private sealed class RbfBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private readonly HashSet<byte> _declared = new();

        public RbfBuilder()
        {
            _writer = new BinaryWriter(_stream);
            _writer.Write(0x30464252u);
        }

        public RbfBuilder Open(byte index, string name)
        {
            Tag(index, name, 0);
            _writer.Write(new byte[6]);
            return this;
        }

        public RbfBuilder UInt(byte index, string name, uint value)
        {
            Tag(index, name, 0x10);
            _writer.Write(value);
            return this;
        }

        public RbfBuilder Text(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            _writer.Write([0xFD, 0xFF]);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
            return this;
        }

        public RbfBuilder Close()
        {
            _writer.Write([0xFF, 0xFF]);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();

        private void Tag(byte index, string name, byte type)
        {
            _writer.Write(index);
            _writer.Write(type);
            if (_declared.Add(index))
            {
                _writer.Write((short)name.Length);
                _writer.Write(Encoding.ASCII.GetBytes(name));
            }
        }
    }
}
