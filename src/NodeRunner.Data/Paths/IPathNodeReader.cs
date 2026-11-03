namespace NodeRunner.Data.Paths;

/// <summary>Decodes a path node file (.ynd).</summary>
public interface IPathNodeReader
{
    /// <summary>
    /// Parses the content of a .ynd file, as returned by <see cref="FileSystem.IGameFileSystem.ReadFile"/>
    /// (decompressed resource data, without the RSC7 header).
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a valid path node file.</exception>
    PathNodeArea Read(ReadOnlySpan<byte> data, int areaId);
}
