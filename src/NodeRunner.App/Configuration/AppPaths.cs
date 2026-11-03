namespace NodeRunner.App.Configuration;

/// <summary>
/// Where NodeRunner stores its files. Portable first: next to the exe.
/// If that folder is read-only (e.g. inside Program Files), falls back to %LOCALAPPDATA%\NodeRunner.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder that holds settings.json.</summary>
    public string ConfigDirectory { get; }

    /// <summary>Folder for extracted data and cache (the "data" folder).</summary>
    public string DataDirectory { get; }

    /// <summary>True when we had to fall back to %LOCALAPPDATA%.</summary>
    public bool UsesLocalAppData { get; }

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    private AppPaths(string configDirectory, string dataDirectory, bool usesLocalAppData)
    {
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
        UsesLocalAppData = usesLocalAppData;
    }

    public static AppPaths Resolve()
    {
        var exeDir = AppContext.BaseDirectory;
        var portableData = Path.Combine(exeDir, "data");

        if (IsWritable(exeDir) && TryCreateDirectory(portableData))
            return new AppPaths(exeDir, portableData, usesLocalAppData: false);

        var fallbackRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeRunner");
        var fallbackData = Path.Combine(fallbackRoot, "data");
        Directory.CreateDirectory(fallbackData);
        return new AppPaths(fallbackRoot, fallbackData, usesLocalAppData: true);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
