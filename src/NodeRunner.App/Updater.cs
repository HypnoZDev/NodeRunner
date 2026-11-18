using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace NodeRunner.App;

// Replaces this copy of NodeRunner by the published ZIP of a newer version: downloaded, checked (address, SHA-256 given
// by GitHub, complete archive, version), then the running files are renamed to .old (Windows allows it), the new ones
// put in their place, and NodeRunner restarted. Any failure puts the previous files back. The user's settings.json,
// imgui.ini and data files are never replaced; the .old files go at the next start-up.
internal static class Updater
{
    public const string DownloadPrefix = "https://github.com/HypnoZDev/NodeRunner/releases/download/";
    private const string OldSuffix = ".old";
    private const string CleanupFile = "update-cleanup.txt";
    private const long MaxDownload = 1L << 30;
    private const int MaxFiles = 5000;

    private static readonly string[] KeptFiles = ["settings.json", "imgui.ini"];
    private const string DataFolder = "data/";

    public static string AppFolder => AppContext.BaseDirectory;

    // Whether NodeRunner can write its own folder (not under Program Files...).
    public static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $"update-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsTrustedUrl(string url) =>
        url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && !url.Contains("..", StringComparison.Ordinal);

    // Downloads the archive to a temporary file and checks its SHA-256 (hex). Returns its path.
    public static async Task<string> DownloadAsync(string url, string sha256, IProgress<double> progress, CancellationToken cancel)
    {
        if (!IsTrustedUrl(url))
            throw new InvalidDataException(L("adresse de téléchargement inattendue"));
        string folder = Path.Combine(Path.GetTempPath(), "NodeRunner");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"update-{Guid.NewGuid():N}.zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NodeRunner-Studio");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if (length > MaxDownload)
            throw new InvalidDataException(L("archive trop grande"));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxDownload)
                    throw new InvalidDataException(L("archive trop grande"));
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                if (length > 0)
                    progress.Report((double)total / length.Value);
            }
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidDataException(L("l'archive téléchargée ne correspond pas à celle publiée (empreinte SHA-256 différente)"));
        }
        return path;
    }

    // Puts the archive's files in place of this copy's. version: the one announced, checked against the archive.
    public static void Install(string zipPath, string appFolder, string version)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string root = Path.GetFullPath(appFolder);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        var entries = new List<(ZipArchiveEntry Entry, string Target)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0)
                continue;
            string name = entry.FullName.Replace('\\', '/').TrimStart('/');
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || name.Split('/').Contains(".."))
                throw new InvalidDataException(L($"chemin refusé dans l'archive : {entry.FullName}"));
            if (KeptFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                || (name.StartsWith(DataFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(target)))
                continue;
            entries.Add((entry, target));
        }
        if (entries.Count > MaxFiles || !entries.Any(e => e.Entry.FullName.Equals("NodeRunner.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L("cette archive n'est pas une version de NodeRunner"));
        CheckVersion(entries.FirstOrDefault(e => e.Entry.FullName.Equals("NodeRunner.dll", StringComparison.OrdinalIgnoreCase)).Entry, version);

        // Every file is read once before anything is touched: a damaged archive stops here.
        foreach (var (entry, _) in entries)
        {
            using var stream = entry.Open();
            stream.CopyTo(Stream.Null);
        }

        var renamed = new List<string>();
        var placed = new List<string>();
        try
        {
            foreach (var (entry, target) in entries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    string old = target + OldSuffix;
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(target, old);
                    renamed.Add(target);
                }
                entry.ExtractToFile(target);
                placed.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            foreach (string file in placed)
                TryDelete(file);
            foreach (string file in renamed)
            {
                try
                {
                    File.Move(file + OldSuffix, file, overwrite: true);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                }
            }
            throw new IOException(L($"la mise à jour n'a pas pu remplacer les fichiers, la version actuelle est gardée : {ex.Message}"), ex);
        }
        try
        {
            File.WriteAllLines(Path.Combine(root, CleanupFile), renamed.Select(f => f + OldSuffix));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The .old files stay: harmless.
        }
    }

    private static void CheckVersion(ZipArchiveEntry? dll, string version)
    {
        if (dll is null)
            throw new InvalidDataException(L("cette archive n'est pas une version de NodeRunner"));
        string temp = Path.Combine(Path.GetTempPath(), "NodeRunner", $"check-{Guid.NewGuid():N}.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            dll.ExtractToFile(temp);
            string? found = FileVersionInfo.GetVersionInfo(temp).ProductVersion?.Split('+')[0];
            if (!string.Equals(found, version, StringComparison.Ordinal))
                throw new InvalidDataException(L($"l'archive contient la version {found}, pas la {version}"));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // Starts the new copy; the caller then closes this one.
    public static void Restart(string appFolder) =>
        Process.Start(new ProcessStartInfo(Path.Combine(appFolder, "NodeRunner.exe")) { UseShellExecute = false, WorkingDirectory = appFolder });

    // At start-up: the files replaced by the last update go.
    public static void CleanUp(string appFolder)
    {
        string list = Path.Combine(appFolder, CleanupFile);
        if (!File.Exists(list))
            return;
        bool all = true;
        try
        {
            foreach (string file in File.ReadAllLines(list))
            {
                if (file.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFullPath(file).StartsWith(Path.GetFullPath(appFolder), StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                    all &= TryDelete(file);
            }
            if (all)
                File.Delete(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
