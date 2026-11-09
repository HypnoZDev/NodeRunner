using System.Buffers.Binary;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Tests;

public class ResourceBuilderTests
{
    // 7 pages need 3 entries in the page list, 8 pages only one: a list that grows past the end of the first page, then
    // shrinks back, must not make the layout go back and forth for ever.
    [Fact]
    public void PageListThatChangesThePageCountSettles()
    {
        var builder = new ResourceBuilder();
        var root = builder.Add(16);
        var first = builder.Add(0x2000 - 48);
        builder.Pointer(root, 0, first);
        for (int i = 0; i < 6; i++)
            builder.Add(0x2000 - 32);

        var task = Task.Run(() => builder.Build(165));
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "Le placement des blocs ne se termine pas.");

        var (header, content) = ResourceFile.Read(task.Result);
        Assert.Equal(165, header.Version);
        Assert.Equal(8 * 0x2000, content.Length);
        int pages = (int)(BinaryPrimitives.ReadUInt64LittleEndian(content.AsSpan(8)) - ResourceBuilder.SystemBase);
        Assert.Equal(1, content[pages + 8]);
    }
}
