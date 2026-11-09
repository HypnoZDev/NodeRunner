using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Archives;

// The game's files plus the type, model and texture files of two folders, each seen as one more archive activated after
// the game: the prop library shared by every project (GameContent.LibraryRoot), then the project's "stream" folder
// (GameContent.LooseFilesRoot). Like in FiveM, a file wins over the game's files of the same name, and the project's
// over the library's. Maps (.ymap) of the project are handled by the project itself; those of the library are ignored.
// A collision file (.ybn) changed in the project keeps its first version in the "originaux" folder next to the stream
// folder: that one is read, the changes are made on it again.
public sealed class LooseFileOverlay : IGameFileSystem
{
    public static readonly string[] Extensions = [".ytyp", ".ydr", ".ydd", ".yft", ".ytd", ".ybn"];

    public const string OriginalsFolderName = "originaux";

    private static readonly string[] ResourceManifests = ["fxmanifest.lua", "__resource.lua"];

    private readonly IGameFileSystem _game;
    private readonly string? _originals;
    private readonly Dictionary<string, string> _projectPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Disk, string Pack)> _libraryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _libraryDuplicates = [];

    private LooseFileOverlay(IGameFileSystem game, string? folder)
    {
        _game = game;
        if (folder is not null)
            _originals = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder)) ?? folder, OriginalsFolderName);
    }

    // Files of the project's folder.
    public int FileCount => _projectPaths.Count;

    public int LibraryFileCount => _libraryPaths.Count;

    // Library files found in several packs: the first pack (by folder name) wins.
    public IReadOnlyList<string> LibraryDuplicates => _libraryDuplicates;

    // The game's file system alone when neither folder holds any of these files. Skipped: files of the project's folder
    // that are not the project's own (a game's .ybn rewritten with the project's changes).
    public static IGameFileSystem Over(IGameFileSystem game, string? folder, IReadOnlySet<string>? skipped = null, string? library = null)
    {
        var overlay = new LooseFileOverlay(game, folder);
        foreach (var file in FilesOf(folder))
        {
            if (skipped?.Contains(Path.GetFileName(file)) != true)
                overlay._projectPaths.TryAdd($@"{GameContent.LooseFilesRoot}\{Path.GetFileName(file)}", file);
        }
        overlay.AddLibrary(library);
        return overlay._projectPaths.Count == 0 && overlay._libraryPaths.Count == 0 ? game : overlay;
    }

    private static List<string> FilesOf(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
            return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A pack is a FiveM resource (folder with a manifest) or, without one, a folder right under the library.
    private void AddLibrary(string? library)
    {
        var files = FilesOf(library);
        if (files.Count == 0)
            return;
        string root = Path.GetFullPath(library!).TrimEnd('\\', '/');
        var packOfFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string PackOf(string file)
        {
            string folder = Path.GetDirectoryName(file)!;
            if (packOfFolder.TryGetValue(folder, out var known))
                return known;
            string pack = "";
            for (string? f = folder; f is not null && f.Length > root.Length; f = Path.GetDirectoryName(f))
            {
                if (ResourceManifests.Any(m => File.Exists(Path.Combine(f, m))))
                {
                    pack = Path.GetFileName(f);
                    break;
                }
                if (string.Equals(Path.GetDirectoryName(f), root, StringComparison.OrdinalIgnoreCase))
                    pack = Path.GetFileName(f);
            }
            packOfFolder[folder] = pack;
            return pack;
        }

        foreach (var (file, pack) in files.Select(f => (f, PackOf(f)))
                     .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.f, StringComparer.OrdinalIgnoreCase))
        {
            if (!_libraryPaths.TryAdd($@"{GameContent.LibraryRoot}\{Path.GetFileName(file)}", (file, pack)))
                _libraryDuplicates.Add(file);
        }
    }

    public GameInstallation? Installation => _game.Installation;

    public void Open(GameInstallation installation, IProgress<string>? progress = null) =>
        throw new NotSupportedException("Open the game's file system itself.");

    public bool FileExists(string path) => Disk(path) is not null || _game.FileExists(path);

    // The file of the project's stream folder behind a path of this overlay, or null for a game's or library's file.
    public string? StreamFileOf(string path) => _projectPaths.GetValueOrDefault(path);

    // The file of the library behind a path of this overlay, and its pack.
    public (string Disk, string Pack)? LibraryFileOf(string path) =>
        _libraryPaths.TryGetValue(path, out var file) ? file : null;

    // The library's file of this name (with its extension), or null.
    public string? LibraryFileNamed(string fileName) =>
        _libraryPaths.TryGetValue($@"{GameContent.LibraryRoot}\{fileName}", out var file) ? file.Disk : null;

    // Where the first version of a file of the stream folder is kept once the project changes it.
    public string OriginalOf(string streamFile) =>
        Path.Combine(_originals ?? Path.GetDirectoryName(streamFile)!, Path.GetFileName(streamFile));

    private string? Disk(string path) =>
        _projectPaths.TryGetValue(path, out var own) ? own
        : _libraryPaths.TryGetValue(path, out var shared) ? shared.Disk
        : null;

    // The original when there is one: the stream folder then holds the changed file.
    private string Source(string path, string disk) =>
        _projectPaths.ContainsKey(path) && disk.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase)
        && OriginalOf(disk) is var original && File.Exists(original)
            ? original : disk;

    public byte[]? ReadFile(string path)
    {
        if (Disk(path) is not { } disk)
            return _game.ReadFile(path);
        try
        {
            var bytes = File.ReadAllBytes(Source(path, disk));
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
        if (Disk(path) is not { } disk)
            return _game.GetResourceHeader(path);
        try
        {
            Span<byte> head = stackalloc byte[16];
            using var stream = File.OpenRead(Source(path, disk));
            stream.ReadExactly(head);
            return ResourceFile.ReadHeader(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // The library's files, then the project's, come after the game's.
    public IEnumerable<string> EnumerateFiles(string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        string ext = extension;
        return _game.EnumerateFiles(extension)
            .Concat(_libraryPaths.Keys.Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Concat(_projectPaths.Keys.Where(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }

    public IEnumerable<string> EnumerateArchives()
    {
        var archives = _game.EnumerateArchives();
        if (_libraryPaths.Count > 0)
            archives = archives.Append(GameContent.LibraryRoot);
        if (_projectPaths.Count > 0)
            archives = archives.Append(GameContent.LooseFilesRoot);
        return archives;
    }

    // The game's file system belongs to the caller.
    public void Dispose()
    {
    }
}
