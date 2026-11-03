using System.Buffers.Binary;
using System.IO.Compression;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Data.Native.Resources;

// Resource files as stored on disk ("RSC7"): a 16-byte header (magic, version, system and graphics page flags)
// followed by the content compressed with raw Deflate.
public static class ResourceFile
{
    private const uint Magic = 0x37435352; // "RSC7"
    private const int HeaderSize = 16;

    public static byte[] Write(ResourceHeader header, ReadOnlySpan<byte> content)
    {
        using var output = new MemoryStream();
        Span<byte> head = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], header.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(head[8..], header.SystemFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(head[12..], header.GraphicsFlags);
        output.Write(head);
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(content);
        return output.ToArray();
    }

    public static (ResourceHeader Header, byte[] Content) Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
            throw new InvalidDataException("Ce fichier n'est pas une ressource RSC7.");
        var header = new ResourceHeader(
            BinaryPrimitives.ReadInt32LittleEndian(file[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[12..]));
        using var input = new MemoryStream(file[HeaderSize..].ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var content = new MemoryStream();
        deflate.CopyTo(content);
        return (header, content.ToArray());
    }
}
