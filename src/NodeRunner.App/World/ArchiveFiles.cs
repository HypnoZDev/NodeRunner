using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Gta4;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.App.World;

// What the archive explorer needs from a game's files, for GTA V (.rpf), GTA IV and the trilogy (.img).
internal interface IArchiveFiles
{
    IEnumerable<string> EnumerateArchives();
    IReadOnlyCollection<string> AllFiles { get; }
    byte[]? ReadFile(string path);
    ArchiveFileInfo? GetInfo(string path);

    // The file as it would sit on disk, readable by the modding tools.
    byte[]? Extract(string path);

    string ArchiveKind { get; }
}

internal sealed class RpfArchiveFiles(RpfFileSystem files) : IArchiveFiles
{
    public RpfFileSystem Files => files;
    public IEnumerable<string> EnumerateArchives() => files.EnumerateArchives();
    public IReadOnlyCollection<string> AllFiles => files.AllFiles;
    public byte[]? ReadFile(string path) => files.ReadFile(path);
    public ArchiveFileInfo? GetInfo(string path) => files.GetInfo(path);
    public string ArchiveKind => L("Archive RPF");

    // Binary files decompressed, resources in their usual "RSC7" form (usable in a FiveM "stream" folder).
    public byte[]? Extract(string path)
    {
        if (files.ReadFile(path) is not { } data)
            return null;
        return files.GetResourceHeader(path) is { } header ? ResourceFile.Write(header, data) : data;
    }
}

internal sealed class ImgArchiveFiles(Gta4FileSystem files) : IArchiveFiles
{
    public Gta4FileSystem Files => files;
    public IEnumerable<string> EnumerateArchives() => files.EnumerateArchives();
    public IReadOnlyCollection<string> AllFiles => files.AllFiles;
    public byte[]? ReadFile(string path) => files.ReadFile(path);
    public string ArchiveKind => L("Archive IMG");

    public ArchiveFileInfo? GetInfo(string path) => files.GetInfo(path) is { } info
        ? new ArchiveFileInfo(info.StoredSize, info.Size, info.IsResource, (int)info.ResourceType, false)
        : null;

    // Resources stay as the archive stores them ("RSC5" header and compression), like the GTA IV modding tools.
    public byte[]? Extract(string path) => files.ReadStoredFile(path);
}

internal sealed class ClassicArchiveFiles(NodeRunner.Data.Native.Classic.ClassicFileSystem files) : IArchiveFiles
{
    public IEnumerable<string> EnumerateArchives() => files.EnumerateArchives();
    public IReadOnlyCollection<string> AllFiles => files.AllFiles;
    public byte[]? ReadFile(string path) => files.ReadFile(path);
    public string ArchiveKind => L("Archive IMG");

    public ArchiveFileInfo? GetInfo(string path) => files.GetSize(path) is { } size ? new ArchiveFileInfo(size, size, false, 0, false) : null;

    // Stored as is (no compression in these archives).
    public byte[]? Extract(string path) => files.ReadFile(path);
}
