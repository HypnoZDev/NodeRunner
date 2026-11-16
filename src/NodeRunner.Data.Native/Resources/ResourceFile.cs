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

    public static ResourceHeader ReadHeader(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
            throw new InvalidDataException("Ce fichier n'est pas une ressource RSC7 (fichier chiffré par FiveM ?).");
        return new ResourceHeader(
            BinaryPrimitives.ReadInt32LittleEndian(file[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(file[12..]));
    }

    public static (ResourceHeader Header, byte[] Content) Read(ReadOnlySpan<byte> file)
    {
        var header = ReadHeader(file);
        var content = Inflate(file[HeaderSize..].ToArray(),
            Archives.ResourcePages.Size(header.SystemFlags) + Archives.ResourcePages.Size(header.GraphicsFlags));
        return (header, content);
    }

    // The most a file may take once decompressed: far above the largest of the game and of mods, low enough that a damaged
    // or hostile file (a few megabytes can inflate to gigabytes) fails cleanly instead of exhausting the memory.
    public const int MaxInflatedSize = 1 << 30;

    internal static byte[] Inflate(byte[] data, int expectedLength)
    {
        using var input = new MemoryStream(data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        // The expected length only sizes the buffer: capped, in case the flags are wrong (modded files), so that a few
        // reads at once cannot exhaust the memory of a small PC; the buffer grows if really needed.
        using var output = new MemoryStream(Math.Clamp(expectedLength, 0, 32 * 1024 * 1024));
        var buffer = new byte[81920];
        int read;
        while ((read = deflate.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxInflatedSize)
                throw new InvalidDataException("Fichier trop grand une fois décompressé (fichier abîmé ?).");
            output.Write(buffer, 0, read);
        }
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }
}
