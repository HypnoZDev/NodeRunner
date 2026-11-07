using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Archives;

// The game's files plus the type, model and texture files of a folder (a project's "stream"), seen as one more archive
// (GameContent.LooseFilesRoot) that is activated last: like in FiveM, they win over the game's files of the same name.
// Maps (.ymap) and collisions (.ybn) of the folder are handled by the project itself.
public sealed class LooseFileOverlay : IGameFileSystem
{
    public static readonly string[] Extensions = [".ytyp", ".ydr", ".ydd", ".yft", ".ytd"];

    private readonly IGameFileSystem _game;
    private readonly Dictionary<string, string> _diskPaths = new(StringComparer.OrdinalIgnoreCase);

    private LooseFileOverlay(IGameFileSystem game) => _game = game;

    public int FileCount => _diskPaths.Count;

    // The game's file system alone when the folder is missing or holds none of these files.
    public static IGameFileSystem Over(IGameFileSystem game, string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
            return game;
        var overlay = new LooseFileOverlay(game);
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
            if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                overlay._diskPaths.TryAdd($@"{GameContent.LooseFilesRoot}\{Path.GetFileName(file)}", file);
        }
        return overlay._diskPaths.Count == 0 ? game : overlay;
    }

    public GameInstallation? Installation => _game.Installation;

    public void Open(GameInstallation installation, IProgress<string>? progress = null) =>
        throw new NotSupportedException("Open the game's file system itself.");

    public bool FileExists(string path) => _diskPaths.ContainsKey(path) || _game.FileExists(path);

    public byte[]? ReadFile(string path)
    {
        if (!_diskPaths.TryGetValue(path, out var disk))
            return _game.ReadFile(path);
        try
        {
            return ResourceFile.Read(File.ReadAllBytes(disk)).Content;
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
            using var stream = File.OpenRead(disk);
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
