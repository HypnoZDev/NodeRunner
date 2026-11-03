namespace NodeRunner.Data.Installation.Sources;

/// <summary>Looks in every Steam library folder listed in Steam's libraryfolders.vdf.</summary>
public sealed class SteamSource : IInstallCandidateSource
{
    internal static readonly string[] GameFolderNames = ["Grand Theft Auto V", "Grand Theft Auto V Enhanced"];

    public string Name => "Steam";

    public IEnumerable<string> GetCandidateFolders()
    {
        var steamRoot =
            RegistryHelper.ReadMachineString(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            ?? RegistryHelper.ReadUserString(@"Software\Valve\Steam", "SteamPath");

        if (string.IsNullOrWhiteSpace(steamRoot))
            yield break;

        var libraries = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
            libraries.AddRange(SteamLibraryParser.ParseLibraryPaths(File.ReadAllText(vdfPath)));

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in GameFolderNames)
                yield return Path.Combine(library, "steamapps", "common", name);
    }
}
