using System.IO.Compression;

namespace NodeRunner.Editor;

// Extracts a .zip that comes from someone else, within limits: total size actually written (the sizes announced by the
// archive are not trusted), number of files, and no path leaving the folder.
public static class SafeZip
{
    public static void Extract(Stream source, string folder, long maxBytes, int maxFiles)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > maxFiles)
            throw new InvalidDataException(L($"trop de fichiers dans l'archive ({zip.Entries.Count})"));
        string root = Path.GetFullPath(folder);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        long left = maxBytes;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L($"chemin invalide dans l'archive : {entry.FullName}"));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                left -= read;
                if (left < 0)
                    throw new InvalidDataException(L("archive trop grande une fois décompressée"));
                output.Write(buffer, 0, read);
            }
        }
    }
}
