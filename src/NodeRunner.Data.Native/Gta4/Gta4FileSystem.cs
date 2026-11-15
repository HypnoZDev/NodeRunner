using Microsoft.Win32.SafeHandles;

namespace NodeRunner.Data.Native.Gta4;

// What the archive stores for a file: bytes on disk, size once decompressed, resource type and flags.
public readonly record struct Gta4FileInfo(long StoredSize, long Size, bool IsResource, uint ResourceType, uint ResourceFlags);

// Files of a GTA IV installation: the content of every ".img" archive ("pc\data\maps\manhat\manhat01.img\manhat01.wpl")
// and, for any other path, the loose file of the game folder.
public sealed class Gta4FileSystem : IDisposable
{
    public static readonly string[] Executables = ["GTAIV.exe", "EFLC.exe"];

    private readonly List<SafeFileHandle> _disks = [];
    private readonly Dictionary<string, ImgEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _archives = [];
    private readonly List<string> _errors = [];

    public string? Folder { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    public IReadOnlyCollection<string> AllFiles => _files.Keys;

    public static bool IsGameFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
        && Executables.Any(e => File.Exists(Path.Combine(folder, e)));

    public void Open(string folder, IProgress<string>? progress = null)
    {
        Dispose();
        string root = Path.GetFullPath(folder);
        var paths = Directory.EnumerateFiles(root, "*.img", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var handles = new SafeFileHandle?[paths.Count];
        bool needsKey = false;
        for (int i = 0; i < paths.Count; i++)
        {
            try
            {
                handles[i] = File.OpenHandle(Path.Combine(root, paths[i]), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    FileOptions.RandomAccess);
                needsKey |= ImgArchive.IsEncrypted(handles[i]!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _errors.Add($"{paths[i]} : {ex.Message}");
            }
        }

        Gta4Cipher? cipher = null;
        if (needsKey)
        {
            progress?.Report("Lecture de la clé du jeu...");
            cipher = FindCipher(root);
        }

        var read = new (List<ImgEntry>? Entries, string? Error)[paths.Count];
        int done = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        var disks = new int[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            disks[i] = -1;
            if (handles[i] is { } handle)
            {
                _disks.Add(handle);
                disks[i] = _disks.Count - 1;
            }
        }
        Parallel.For(0, paths.Count, options, i =>
        {
            if (disks[i] < 0)
                return;
            try
            {
                read[i] = (ImgArchive.Read(_disks[disks[i]], disks[i], cipher), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                read[i] = (null, $"{paths[i]} : {ex.Message}");
            }
            int count = Interlocked.Increment(ref done);
            progress?.Report($"Lecture des archives ({count} / {paths.Count}) : {paths[i]}");
        });

        for (int i = 0; i < paths.Count; i++)
        {
            if (read[i].Error is { } error)
                _errors.Add(error);
            if (read[i].Entries is not { } entries)
                continue;
            _archives.Add(paths[i]);
            foreach (var entry in entries)
                _files[paths[i] + Path.DirectorySeparatorChar + entry.Name] = entry;
        }
        Folder = root;
    }

    private Gta4Cipher? FindCipher(string root)
    {
        foreach (string exe in Executables)
        {
            string path = Path.Combine(root, exe);
            if (!File.Exists(path))
                continue;
            try
            {
                if (Gta4Cipher.FromExecutable(File.ReadAllBytes(path)) is { } cipher)
                    return cipher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _errors.Add($"{exe} : {ex.Message}");
            }
        }
        _errors.Add("Clé des archives introuvable dans l'exécutable du jeu.");
        return null;
    }

    public IEnumerable<string> EnumerateArchives() => _archives.ToList();

    public IEnumerable<string> EnumerateFiles(string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        return _files.Keys.Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool FileExists(string path) => _files.ContainsKey(path) || LoosePath(path) is not null;

    // Content of a file; resources are returned decompressed (system pages, then graphics pages).
    public byte[]? ReadFile(string path)
    {
        if (!_files.TryGetValue(path, out var entry))
            return LoosePath(path) is { } loose ? File.ReadAllBytes(loose) : null;
        byte[] stored = ReadStored(entry);
        return entry.IsResource ? ImgArchive.InflateResource(stored) : stored;
    }

    // Bytes of a file exactly as the archive stores them (resource header and compression included).
    public byte[]? ReadStoredFile(string path) =>
        _files.TryGetValue(path, out var entry) ? ReadStored(entry) : null;

    public Gta4FileInfo? GetInfo(string path)
    {
        if (!_files.TryGetValue(path, out var e))
            return null;
        if (!e.IsResource)
            return new Gta4FileInfo(e.Size, e.Size, false, 0, 0);
        var (system, graphics) = ImgArchive.ResourceSizes(e.ResourceFlags);
        return new Gta4FileInfo(e.Size, (long)system + graphics, true, e.ResourceType, e.ResourceFlags);
    }

    private byte[] ReadStored(ImgEntry entry)
    {
        var data = new byte[entry.Size];
        ImgArchive.ReadExactly(_disks[entry.Disk], data, entry.Offset);
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
        Folder = null;
    }
}
