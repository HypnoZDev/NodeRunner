namespace NodeRunner.Data.Paths;

// Writes a path node file (.ynd).
public interface IPathNodeWriter
{
    // Rewrites source (the game's file, decompressed resource data) with the positions of the area's nodes and the
    // link lengths given by linkLength; same size and layout as the source.
    byte[] Write(ReadOnlySpan<byte> source, PathNodeArea area, Func<PathNode, PathLink, int> linkLength);
}
