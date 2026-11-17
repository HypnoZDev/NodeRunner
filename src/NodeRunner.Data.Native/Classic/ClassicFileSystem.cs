using Microsoft.Win32.SafeHandles;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Classic;

// Files of a GTA III / Vice City / San Andreas installation: the content of its ".img" archives
// ("models\gta3.img\lae2_roads.dff") and, for any other path, the loose file of the game folder.
// Only the folders the game itself reads are opened: mod managers ("modloader") and repack extras are left out, so
// the map shown is the game's own.
public sealed class ClassicFileSystem : IDisposable
{
    private static readonly string[] GameFolders = ["models", "anim", "data"];

    private readonly List<SafeFileHandle> _disks = [];
    private readonly Dictionary<string, ClassicImgEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _archives = [];
    private readonly List<string> _errors = [];

    public string? Folder { get; private set; }

    public ClassicGame Game { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    public IReadOnlyCollection<string> AllFiles => _files.Keys;

    // Files stored twice in the same archive (the last one is kept).
    public int Duplicates { get; private set; }

    public void Open(string folder, ClassicGame game, IProgress<string>? progress = null)
    {
        Dispose();
        string root = Path.GetFullPath(folder);
        var paths = GameFolders
            .Select(sub => Path.Combine(root, sub))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.img", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(root, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < paths.Count; i++)
        {
            progress?.Report($"Lecture des archives ({i + 1} / {paths.Count}) : {paths[i]}");
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(Path.Combine(root, paths[i]), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    FileOptions.RandomAccess);
                int disk = _disks.Count;
                List<ClassicImgEntry> entries;
                if (ClassicImgArchive.IsVersion2(handle))
                {
                    entries = ClassicImgArchive.Read(handle, disk);
                }
                else
                {
                    string dir = Path.ChangeExtension(Path.Combine(root, paths[i]), ".dir");
                    if (!File.Exists(dir))
                        throw new InvalidDataException("fichier .dir introuvable à côté de l'archive");
                    entries = ClassicImgArchive.Read(handle, File.ReadAllBytes(dir), disk);
                }
                _disks.Add(handle);
                handle = null;
                _archives.Add(paths[i]);
                foreach (var entry in entries)
                {
                    string key = paths[i] + Path.DirectorySeparatorChar + entry.Name;
                    if (_files.ContainsKey(key))
                        Duplicates++;
                    _files[key] = entry;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _errors.Add($"{paths[i]} : {ex.Message}");
            }
            finally
            {
                handle?.Dispose();
            }
        }
        Folder = root;
        Game = game;
    }

    public IEnumerable<string> EnumerateArchives() => _archives.ToList();

    public IEnumerable<string> EnumerateFiles(string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        return _files.Keys.Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool FileExists(string path) => _files.ContainsKey(path) || LoosePath(path) is not null;

    public long? GetSize(string path) => _files.TryGetValue(path, out var entry) ? entry.Size : null;

    public byte[]? ReadFile(string path)
    {
        if (!_files.TryGetValue(path, out var entry))
            return LoosePath(path) is { } loose ? File.ReadAllBytes(loose) : null;
        var data = new byte[entry.Size];
        ClassicImgArchive.ReadExactly(_disks[entry.Disk], data, entry.Offset);
        return data;
    }

    // A loose file inside the game folder (never outside it).
    private string? LoosePath(string path)
    {
        if (Folder is null || string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            string full = Path.GetFullPath(Path.Combine(Folder, path));
            return full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                ? full
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var handle in _disks)
            handle.Dispose();
        _disks.Clear();
        _files.Clear();
        _archives.Clear();
        _errors.Clear();
        Duplicates = 0;
        Folder = null;
    }
}
