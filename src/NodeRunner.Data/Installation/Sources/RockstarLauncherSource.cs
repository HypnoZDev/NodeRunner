namespace NodeRunner.Data.Installation.Sources;

// Rockstar Games writes the install folder in the registry, whichever launcher installed the game.
public sealed class RockstarLauncherSource : IInstallCandidateSource
{
    // Key names used by the Legacy and Enhanced editions. The Enhanced ones are best-effort:
    // if a key does not exist, it is simply skipped.
    private static readonly string[] Keys =
    [
        @"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V",
        @"SOFTWARE\WOW6432Node\Rockstar Games\GTA V Enhanced",
        @"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V Enhanced",
    ];

    private static readonly string[] Values = ["InstallFolder", "InstallFolderSteam", "InstallFolderEpic"];

    public string Name => "Rockstar Launcher";

    public IEnumerable<string> GetCandidateFolders()
    {
        foreach (var key in Keys)
        {
            foreach (var value in Values)
            {
                var path = RegistryHelper.ReadMachineString(key, value);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                yield return path;

                // Some setups store a sub-folder (e.g. "...\Grand Theft Auto V\GTAV"):
                // also try the parent folder.
                var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parent))
                    yield return parent;
            }
        }
    }
}
