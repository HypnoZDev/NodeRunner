namespace NodeRunner.Data.Paths;

// Writes a path node file (.ynd).
public interface IPathNodeWriter
{
    // The whole file (resource with its header) for an area, with the link lengths given by linkLength.
    byte[] Write(PathNodeArea area, Func<PathNode, PathLink, int> linkLength, int version);
}
