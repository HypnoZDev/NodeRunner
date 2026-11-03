using NodeRunner.Data.Installation.Sources;

namespace NodeRunner.Data.Installation;

/// <summary>Finds game installations on this computer by asking several candidate sources.</summary>
public sealed class GameInstallLocator
{
    private readonly IReadOnlyList<IInstallCandidateSource> _sources;

    public GameInstallLocator(IEnumerable<IInstallCandidateSource> sources)
    {
        _sources = sources.ToList();
    }

    /// <summary>Locator with every built-in source (Rockstar Launcher, Steam, Epic Games, default folders).</summary>
    public static GameInstallLocator CreateDefault() => new(
    [
        new RockstarLauncherSource(),
        new SteamSource(),
        new EpicGamesSource(),
        new DefaultFoldersSource(),
    ]);

    /// <summary>Every valid installation found, without duplicates, in source priority order.</summary>
    public IReadOnlyList<GameInstallation> FindAll()
    {
        var results = new List<GameInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            IEnumerable<string> candidates;
            try
            {
                candidates = source.GetCandidateFolders().ToList();
            }
            catch (Exception)
            {
                continue; // A broken launcher config must never prevent startup.
            }

            foreach (var candidate in candidates)
            {
                var install = GameFolderValidator.TryCreate(candidate, source.Name);
                if (install is not null && seen.Add(install.FolderPath.TrimEnd('\\', '/')))
                    results.Add(install);
            }
        }

        return results;
    }

    /// <summary>
    /// The installation to use by default: Legacy first (best supported by the data readers for now),
    /// then Enhanced. Null if nothing was found.
    /// </summary>
    public GameInstallation? FindPreferred()
    {
        var all = FindAll();
        return all.FirstOrDefault(i => i.Edition == GameEdition.Legacy) ?? all.FirstOrDefault();
    }
}
