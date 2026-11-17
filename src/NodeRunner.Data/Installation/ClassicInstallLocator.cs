using NodeRunner.Data.Installation.Sources;

namespace NodeRunner.Data.Installation;

// The RenderWare games of the first trilogy.
public enum ClassicGame
{
    Gta3,
    ViceCity,
    SanAndreas,
}

// Finds GTA III, Vice City or San Andreas (original PC versions; the Definitive Editions use other files): Steam
// libraries, the default folders, and the sub-folders of a folder holding several games.
public static class ClassicInstallLocator
{
    public static readonly ClassicGame[] Games = [ClassicGame.Gta3, ClassicGame.ViceCity, ClassicGame.SanAndreas];

    // The file listing the map of each game, only present in the original PC versions.
    public static string MapList(ClassicGame game) => game switch
    {
        ClassicGame.Gta3 => Path.Combine("data", "gta3.dat"),
        ClassicGame.ViceCity => Path.Combine("data", "gta_vc.dat"),
        _ => Path.Combine("data", "gta.dat"),
    };

    public static string DisplayName(ClassicGame game) => game switch
    {
        ClassicGame.Gta3 => "GTA III",
        ClassicGame.ViceCity => "Vice City",
        _ => "San Andreas",
    };

    public static bool IsGameFolder(string? folder, ClassicGame game)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, MapList(game)))
                || !File.Exists(Path.Combine(folder, "models", "gta3.img")))
                return false;
            // Only San Andreas has its interiors in a separate archive; it tells it apart from a folder holding
            // the data files of two games.
            return File.Exists(Path.Combine(folder, "models", "gta_int.img")) == (game == ClassicGame.SanAndreas);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The game folder itself, or the one sub-folder holding the game ("...\Original Trilogy" for "...\GTASA").
    public static string? Resolve(string? folder, ClassicGame game)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        try
        {
            folder = Path.GetFullPath(folder.Trim().Trim('"'));
            if (IsGameFolder(folder, game))
                return folder;
            if (!Directory.Exists(folder))
                return null;
            return Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(sub => IsGameFolder(sub, game));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? Find(ClassicGame game)
    {
        foreach (string candidate in Candidates(game))
        {
            if (Resolve(candidate, game) is { } folder)
                return folder;
        }
        return null;
    }

    private static string[] FolderNames(ClassicGame game) => game switch
    {
        ClassicGame.Gta3 => ["Grand Theft Auto 3", "Grand Theft Auto III", "GTA III", "GTA3"],
        ClassicGame.ViceCity => ["Grand Theft Auto Vice City", "GTA Vice City", "GTAVC"],
        _ => ["Grand Theft Auto San Andreas", "GTA San Andreas", "GTASA"],
    };

    private static IEnumerable<string> Candidates(ClassicGame game)
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
            foreach (string name in FolderNames(game))
                yield return Path.Combine(library, "steamapps", "common", name);

        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            foreach (string name in FolderNames(game))
            {
                yield return Path.Combine(root, "Rockstar Games", name);
                yield return Path.Combine(root, name);
            }
        }

        // Copies installed by hand: the folders of each fixed drive and of its "Games" / "Jeux" folder (each one is
        // also looked into, for a folder holding the three games).
        foreach (string folder in HandInstalledFolders())
            yield return folder;
    }

    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "ProgramData", "System Volume Information", "Recovery", "PerfLogs", "Users", "Program Files",
        "Program Files (x86)", "Intel", "AMD", "NVIDIA", "OneDriveTemp", "Config.Msi",
    };

    private static IEnumerable<string> HandInstalledFolders()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }
            if (!ready)
                continue;
            foreach (string root in new[] { drive.RootDirectory.FullName, Path.Combine(drive.RootDirectory.FullName, "Games"),
                Path.Combine(drive.RootDirectory.FullName, "Jeux") })
            {
                string[] folders;
                try
                {
                    folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    folders = [];
                }
                foreach (string folder in folders)
                {
                    string name = Path.GetFileName(folder);
                    if (!name.StartsWith('$') && !SystemFolders.Contains(name))
                        yield return folder;
                }
            }
        }
    }
}
