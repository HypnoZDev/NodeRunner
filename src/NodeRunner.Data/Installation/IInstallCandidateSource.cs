namespace NodeRunner.Data.Installation;

/// <summary>
/// Something that can suggest folders where the game might be installed
/// (a launcher, the registry, common default paths...). Candidates are validated afterwards.
/// </summary>
public interface IInstallCandidateSource
{
    /// <summary>Human-readable name shown to the user (e.g. "Steam").</summary>
    string Name { get; }

    /// <summary>Folders that might contain the game. May return paths that do not exist.</summary>
    IEnumerable<string> GetCandidateFolders();
}
