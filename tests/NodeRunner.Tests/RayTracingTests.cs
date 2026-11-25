using System.Numerics;
using NodeRunner.Data.Textures;
using NodeRunner.Render;
using TextureLayout = NodeRunner.Data.Textures.TextureLayout;

namespace NodeRunner.Tests;

public sealed class RayTracingTests
{
    [Fact]
    public void RayTracingShader_Compiles()
    {
        var shader = RayTracedReflections.CompileShader(out var errors);
        Assert.True(shader is { Length: > 0 }, errors);
    }

    [Fact]
    public void AverageColor_OfCompressedTexture_IsItsColour()
    {
        // 8x8 BC1, every block pure red (565: 0xF800), second mip level 4x4 the same.
        byte[] block = [0x00, 0xF8, 0x00, 0xF8, 0, 0, 0, 0];
        var data = Enumerable.Range(0, 5).SelectMany(_ => block).ToArray();
        var texture = new TextureData { NameHash = 0, Name = "red", Width = 8, Height = 8, MipLevels = 2, Format = TextureFormat.Bc1, Data = data };

        var color = TextureAverage.Of(texture, TextureLayout.GetMipLevels(texture));

        Assert.NotNull(color);
        Assert.Equal(1f, color.Value.X, 3);
        Assert.Equal(0f, color.Value.Y, 3);
        Assert.Equal(0f, color.Value.Z, 3);
    }

    [Fact]
    public void AverageColor_IgnoresTransparentTexels()
    {
        // Half the texels opaque white, half transparent black.
        var data = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i += 2)
            data.AsSpan(i * 4, 4).Fill(255);
        var texture = new TextureData { NameHash = 0, Name = "half", Width = 4, Height = 4, MipLevels = 1, Format = TextureFormat.R8G8B8A8, Data = data };

        var color = TextureAverage.Of(texture, TextureLayout.GetMipLevels(texture));

        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), color);
    }

    [Fact]
    public void RangeAllocator_GivesBackFreedRoom()
    {
        var space = new RangeAllocator(1000);
        var a = space.Allocate(300, 256);
        var b = space.Allocate(300, 256);
        var c = space.Allocate(300, 256);

        Assert.Equal(0, a);
        Assert.Equal(512, b);
        Assert.Null(c); // 768 + 300 > 1000.

        space.Free(a!.Value, 300);
        space.Free(b!.Value, 300);
        Assert.Equal(0, space.Used);
        Assert.Equal(0, space.Allocate(1000));
    }
}
