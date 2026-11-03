namespace NodeRunner.Data.Installation.Sources;

/// <summary>Reads the Epic Games Launcher install manifests (*.item JSON files).</summary>
public sealed class EpicGamesSource : IInstallCandidateSource
{
    private readonly string _manifestsFolder;

    public EpicGamesSource()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests"))
    {
    }

    public EpicGamesSource(string manifestsFolder)
    {
        _manifestsFolder = manifestsFolder;
    }

    public string Name => "Epic Games";

    public IEnumerable<string> GetCandidateFolders()
    {
        if (!Directory.Exists(_manifestsFolder))
            yield break;

        foreach (var file in Directory.EnumerateFiles(_manifestsFolder, "*.item"))
        {
            string? location = null;
            try
            {
                location = EpicManifestParser.TryGetGameInstallLocation(File.ReadAllText(file));
            }
            catch (Exception)
            {
                // Unreadable manifest: ignore it.
            }

            if (location is not null)
                yield return location;
        }
    }
}
