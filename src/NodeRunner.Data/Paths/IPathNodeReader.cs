namespace NodeRunner.Data.Paths;

// Decodes a path node file (.ynd).
public interface IPathNodeReader
{
    // Parses the content of a .ynd file, as returned by ReadFile (decompressed resource data, without the RSC7
    // header).
    PathNodeArea Read(ReadOnlySpan<byte> data, int areaId);
}
