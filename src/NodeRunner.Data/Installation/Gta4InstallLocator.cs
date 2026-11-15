using NodeRunner.Data.Installation.Sources;

namespace NodeRunner.Data.Installation;

// Finds a GTA IV installation (Complete Edition, or the original game with its episodes): Steam libraries, the Rockstar
// registry key, then the default folders. The game files sit in a "GTAIV" sub-folder on Steam.
public static class Gta4InstallLocator
{
    public static readonly string[] Executables = ["GTAIV.exe", "EFLC.exe"];

    public static bool IsGameFolder(string? folder)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                && Executables.Any(e => File.Exists(Path.Combine(folder, e)))
                && Directory.Exists(Path.Combine(folder, "pc"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The game folder itself when given a parent folder ("...\Grand Theft Auto IV" for "...\Grand Theft Auto IV\GTAIV").
    public static string? Resolve(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        try
        {
            folder = Path.GetFullPath(folder.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (IsGameFolder(folder))
            return folder;
        foreach (string sub in new[] { "GTAIV", "EFLC" })
            if (IsGameFolder(Path.Combine(folder, sub)))
                return Path.Combine(folder, sub);
        return null;
    }

    public static string? Find()
    {
        foreach (string candidate in Candidates())
        {
            if (Resolve(candidate) is { } folder)
                return folder;
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        List<string> libraries;
        try
        {
            libraries = SteamSource.Libraries();
        }
        catch (Exception)
        {
            libraries = []; // A broken Steam config must never prevent startup.
        }
        foreach (string library in libraries)
        {
            yield return Path.Combine(library, "steamapps", "common", "Grand Theft Auto IV");
            yield return Path.Combine(library, "steamapps", "common", "Grand Theft Auto IV The Complete Edition");
        }

        foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto IV", @"SOFTWARE\WOW6432Node\Rockstar Games\GTAIV" })
        {
            foreach (string value in new[] { "InstallFolder", "InstallFolderSteam", "InstallFolderEpic" })
            {
                string? path = null;
                try
                {
                    path = RegistryHelper.ReadMachineString(key, value);
                }
                catch (Exception)
                {
                    // Unreadable key: next one.
                }
                if (!string.IsNullOrWhiteSpace(path))
                    yield return path;
            }
        }

        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            yield return Path.Combine(root, "Rockstar Games", "Grand Theft Auto IV");
            yield return Path.Combine(root, "Rockstar Games", "Grand Theft Auto IV The Complete Edition");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "Grand Theft Auto IV");
        }
    }
}
