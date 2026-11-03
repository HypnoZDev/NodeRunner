using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class MetaDocumentTests
{
    private static MetaDocument Sample(params int[] blockLengths)
    {
        var document = new MetaDocument { Name = "sample", RootBlock = 0 };
        document.Structures.Add(new MetaDocument.StructureInfo(new byte[0x20], new byte[0x30]));
        document.Enums.Add(new MetaDocument.EnumInfo(new byte[0x18], new byte[0x10]));
        for (int i = 0; i < blockLengths.Length; i++)
        {
            var data = new byte[blockLengths[i]];
            new Random(i).NextBytes(data);
            document.Blocks.Add(new MetaDocument.Block((uint)(1000 + i), data));
        }
        return document;
    }

    [Fact]
    public void Reads_back_what_it_writes()
    {
        var document = Sample(512, 48, 0x4000, 16);
        var (content, _) = document.Write();
        var read = MetaDocument.Read(content);

        Assert.Equal("sample", read.Name);
        Assert.Equal(0, read.RootBlock);
        Assert.Single(read.Structures);
        Assert.Equal(0x30, read.Structures[0].Entries.Length);
        Assert.Single(read.Enums);
        Assert.Equal(document.Blocks.Count, read.Blocks.Count);
        for (int i = 0; i < document.Blocks.Count; i++)
        {
            Assert.Equal(document.Blocks[i].Structure, read.Blocks[i].Structure);
            Assert.Equal(document.Blocks[i].Data, read.Blocks[i].Data);
        }

        // Writing again gives the same bytes: the layout only depends on the content.
        Assert.Equal(content, read.Write().Content);
    }

    [Fact]
    public void Spreads_large_content_over_pages()
    {
        var (content, flags) = Sample(0x3000, 0x3000, 0x3000, 0x3000).Write();
        Assert.Equal(0, content.Length % 0x4000); // Pages of 16 KB: the smallest that fit a 12 KB block.
        Assert.True(content.Length >= 4 * 0x3000);
        Assert.Equal(5u, flags & 0xF); // 0x200 << 5 = 16 KB.
    }

    [Theory]
    [InlineData(1, 0x2000, 0x08000004u)]
    [InlineData(3, 0x2000, 0x0C000004u)]
    [InlineData(17, 0x4000, 0x08020005u)]
    public void Encodes_page_flags(int count, int size, uint expected)
    {
        Assert.Equal(expected, MetaDocument.PageFlags(count, size));
    }

    [Fact]
    public void Encodes_pointers_between_blocks()
    {
        ulong pointer = MetaDocument.Pointer(2, 0x180);
        Assert.Equal((2, 0x180), MetaDocument.DecodePointer(pointer));
        Assert.Equal((-1, 0), MetaDocument.DecodePointer(0));
    }
}
