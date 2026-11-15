using System.Text;

namespace NodeRunner.Data.FileSystem;

// Writes a file next to its target first, then swaps it in: an interrupted write never leaves a broken file in place.
public static class SafeFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true); // On the disk before the swap, else a power cut could leave it empty.
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }
}
