using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Archives;

// The game's files plus the type, model and texture files of a folder (a project's "stream"), seen as one more archive
// (GameContent.LooseFilesRoot) that is activated last: like in FiveM, they win over the game's files of the same name.
// Maps (.ymap) of the folder are handled by the project itself. A collision file (.ybn) changed in the project keeps its
// first version in the "originaux" folder next to the stream folder: that one is read, the changes are made on it again.
public sealed class LooseFileOverlay : IGameFileSystem
{
    public static readonly string[] Extensions = [".ytyp", ".ydr", ".ydd", ".yft", ".ytd", ".ybn"];

    public const string OriginalsFolderName = "originaux";

    private readonly IGameFileSystem _game;
    private readonly string _originals;
    private readonly Dictionary<string, string> _diskPaths = new(StringComparer.OrdinalIgnoreCase);

    private LooseFileOverlay(IGameFileSystem game, string folder)
    {
        _game = game;
        _originals = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder)) ?? folder, OriginalsFolderName);
    }

    public int FileCount => _diskPaths.Count;

    // The game's file system alone when the folder is missing or holds none of these files. Skipped: files of the folder
    // that are not the project's own (a game's .ybn rewritten with the project's changes).
    public static IGameFileSystem Over(IGameFileSystem game, string? folder, IReadOnlySet<string>? skipped = null)
    {
        if (folder is null || !Directory.Exists(folder))
            return game;
        var overlay = new LooseFileOverlay(game, folder);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return game;
        }
        foreach (var file in files)
        {
            if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                && skipped?.Contains(Path.GetFileName(file)) != true)
                overlay._diskPaths.TryAdd($@"{GameContent.LooseFilesRoot}\{Path.GetFileName(file)}", file);
        }
        return overlay._diskPaths.Count == 0 ? game : overlay;
    }

    public GameInstallation? Installation => _game.Installation;

    public void Open(GameInstallation installation, IProgress<string>? progress = null) =>
        throw new NotSupportedException("Open the game's file system itself.");

    public bool FileExists(string path) => _diskPaths.ContainsKey(path) || _game.FileExists(path);

    // The file of the stream folder behind a path of this overlay, or null for a game's file.
    public string? StreamFileOf(string path) => _diskPaths.GetValueOrDefault(path);

    // Where the first version of a file of the stream folder is kept once the project changes it.
    public string OriginalOf(string streamFile) => Path.Combine(_originals, Path.GetFileName(streamFile));

    // The original when there is one: the stream folder then holds the changed file.
    private string Source(string disk) =>
        disk.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase) && OriginalOf(disk) is var original && File.Exists(original)
            ? original : disk;

    public byte[]? ReadFile(string path)
    {
        if (!_diskPaths.TryGetValue(path, out var disk))
            return _game.ReadFile(path);
        try
        {
            var bytes = File.ReadAllBytes(Source(disk));
            if (ProtectedFile.IsProtected(bytes))
                throw ProtectedFile.Error(Path.GetFileName(disk));
            return ResourceFile.Read(bytes).Content;
        }
        catch (IOException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    public ResourceHeader? GetResourceHeader(string path)
    {
        if (!_diskPaths.TryGetValue(path, out var disk))
            return _game.GetResourceHeader(path);
        try
        {
            Span<byte> head = stackalloc byte[16];
            using var stream = File.OpenRead(Source(disk));
            stream.ReadExactly(head);
            return ResourceFile.ReadHeader(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // The folder's files come last, so that a file of a name the game has too replaces it.
    public IEnumerable<string> EnumerateFiles(string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        string ext = extension;
        return _game.EnumerateFiles(extension)
            .Concat(_diskPaths.Keys.Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }

    public IEnumerable<string> EnumerateArchives() => _game.EnumerateArchives().Append(GameContent.LooseFilesRoot);

    // The game's file system belongs to the caller.
    public void Dispose()
    {
    }
}
