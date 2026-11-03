namespace NodeRunner.Data.Installation;

// Checks whether a folder is a usable game installation.
public static class GameFolderValidator
{
    public const string LegacyExecutable = "GTA5.exe";
    public const string EnhancedExecutable = "GTA5_Enhanced.exe";

    // Returns the edition found in folderPath, or null if the folder does not look like a game installation.
    public static GameEdition? DetectEdition(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return null;

        // Enhanced is checked first: an Enhanced folder never contains GTA5.exe,
        // but being explicit keeps the intent clear.
        if (File.Exists(Path.Combine(folderPath, EnhancedExecutable)))
            return GameEdition.Enhanced;
        if (File.Exists(Path.Combine(folderPath, LegacyExecutable)))
            return GameEdition.Legacy;
        return null;
    }

    public static GameInstallation? TryCreate(string? folderPath, string source)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(folderPath.Trim().Trim('"'));
        }
        catch (Exception)
        {
            return null; // Malformed path coming from the registry or a manifest.
        }

        var edition = DetectEdition(normalized);
        return edition is null ? null : new GameInstallation(normalized, edition.Value, source);
    }
}
