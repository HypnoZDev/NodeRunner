namespace NodeRunner.Data.FileSystem;

// A file locked by FiveM's Asset Escrow ("FXAP" header): only a server holding the resource's licence can read it.
// Never decrypted here; such models are shown as boxes.
public static class ProtectedFile
{
    public static bool IsProtected(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("FXAP"u8);

    public static bool IsProtected(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[4];
            using var stream = File.OpenRead(path);
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && IsProtected(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const string ErrorKey = "NodeRunner.ProtectedFile";

    // An InvalidDataException like any unreadable file (callers already handle those), marked as a protected one.
    public static InvalidDataException Error(string fileName)
    {
        var error = new InvalidDataException($"{fileName} : fichier protégé par FiveM (Asset Escrow), illisible hors du serveur qui en a la licence.");
        error.Data[ErrorKey] = fileName;
        return error;
    }

    public static bool IsError(Exception error) => error.Data.Contains(ErrorKey);
}
