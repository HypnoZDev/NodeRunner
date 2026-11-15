namespace NodeRunner.Data.Installation.Sources;

// Looks in every Steam library folder listed in Steam's libraryfolders.vdf.
public sealed class SteamSource : IInstallCandidateSource
{
    internal static readonly string[] GameFolderNames = ["Grand Theft Auto V", "Grand Theft Auto V Enhanced"];

    public string Name => "Steam";

    public IEnumerable<string> GetCandidateFolders()
    {
        foreach (var library in Libraries())
            foreach (var name in GameFolderNames)
                yield return Path.Combine(library, "steamapps", "common", name);
    }

    // Every Steam library folder of this computer.
    internal static List<string> Libraries()
    {
        var steamRoot =
            RegistryHelper.ReadMachineString(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            ?? RegistryHelper.ReadUserString(@"Software\Valve\Steam", "SteamPath");

        if (string.IsNullOrWhiteSpace(steamRoot))
            return [];

        var libraries = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
            libraries.AddRange(SteamLibraryParser.ParseLibraryPaths(File.ReadAllText(vdfPath)));
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
