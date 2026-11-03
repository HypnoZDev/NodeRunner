namespace NodeRunner.Data.Installation;

/// <summary>Checks whether a folder is a usable game installation.</summary>
public static class GameFolderValidator
{
    public const string LegacyExecutable = "GTA5.exe";
    public const string EnhancedExecutable = "GTA5_Enhanced.exe";

    /// <summary>
    /// Returns the edition found in <paramref name="folderPath"/>, or null if the folder
    /// does not look like a game installation.
    /// </summary>
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

    /// <summary>Builds a <see cref="GameInstallation"/> if the folder is valid, otherwise null.</summary>
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
