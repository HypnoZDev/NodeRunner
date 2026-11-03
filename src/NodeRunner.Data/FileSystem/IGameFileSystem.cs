using NodeRunner.Data.Installation;

namespace NodeRunner.Data.FileSystem;

/// <summary>
/// Access to the files of the user's game installation (including files packed inside RPF archives).
/// Implemented by RpfFileSystem (NodeRunner.Data.Native), over the RPF archives of the installation.
/// Paths use the game's internal form, e.g. "update\update.rpf\common\data\levels\gta5\...".
/// </summary>
public interface IGameFileSystem : IDisposable
{
    /// <summary>The installation currently opened, or null.</summary>
    GameInstallation? Installation { get; }

    /// <summary>Indexes the archives of the installation. Can take several seconds.</summary>
    void Open(GameInstallation installation, IProgress<string>? progress = null);

    bool FileExists(string path);

    /// <summary>Returns the uncompressed/decrypted bytes of a file, or null if it does not exist.</summary>
    byte[]? ReadFile(string path);

    /// <summary>
    /// Version and page layout of a resource file (.ymap, .ydr...), needed to write it back in the same form;
    /// null if the file does not exist or is not a resource.
    /// </summary>
    ResourceHeader? GetResourceHeader(string path);

    /// <summary>Every file path whose extension matches (e.g. ".ymap"), in no particular order.</summary>
    IEnumerable<string> EnumerateFiles(string extension);

    /// <summary>
    /// Path of every RPF archive, including archives nested in other archives
    /// (e.g. "x64i.rpf\levels\gta5\_citye\downtown_01\downtown_01_metadata.rpf"), in no particular order.
    /// </summary>
    IEnumerable<string> EnumerateArchives();
}

/// <summary>
/// Header of a resource file ("RSC7"): format version and the flags describing its memory pages
/// (the high 4 bits of each flag hold half of the version).
/// </summary>
public readonly record struct ResourceHeader(int Version, uint SystemFlags, uint GraphicsFlags);
