using NodeRunner.Data.Installation;

namespace NodeRunner.Data.FileSystem;

// Access to the files of the user's game installation (including files packed inside RPF archives).
public interface IGameFileSystem : IDisposable
{
    GameInstallation? Installation { get; }

    void Open(GameInstallation installation, IProgress<string>? progress = null);

    bool FileExists(string path);

    byte[]? ReadFile(string path);

    // Version and page layout of a resource file (.ymap, .ydr...), needed to write it back in the same form; null if
    // the file does not exist or is not a resource.
    ResourceHeader? GetResourceHeader(string path);

    IEnumerable<string> EnumerateFiles(string extension);

    // Path of every RPF archive, including archives nested in other archives (e.g.
    // "x64i.rpf\levels\gta5\_citye\downtown_01\downtown_01_metadata.rpf"), in no particular order.
    IEnumerable<string> EnumerateArchives();
}

// Header of a resource file ("RSC7"): format version and the flags describing its memory pages (the high 4 bits of
// each flag hold half of the version).
public readonly record struct ResourceHeader(int Version, uint SystemFlags, uint GraphicsFlags);
