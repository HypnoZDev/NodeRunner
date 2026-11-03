namespace NodeRunner.Data.Installation;

// Something that can suggest folders where the game might be installed (a launcher, the registry, common default
// paths...). Candidates are validated afterwards.
public interface IInstallCandidateSource
{
    string Name { get; }

    IEnumerable<string> GetCandidateFolders();
}
