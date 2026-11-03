namespace NodeRunner.Data.Installation.Sources;

// Last resort: the default install folders of each launcher.
public sealed class DefaultFoldersSource : IInstallCandidateSource
{
    public string Name => "Dossier par défaut";

    public IEnumerable<string> GetCandidateFolders()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var root in new[] { programFiles, programFilesX86 }.Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            yield return Path.Combine(root, "Rockstar Games", "Grand Theft Auto V");
            yield return Path.Combine(root, "Rockstar Games", "Grand Theft Auto V Enhanced");
            yield return Path.Combine(root, "Epic Games", "GTAV");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "Grand Theft Auto V");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "Grand Theft Auto V Enhanced");
        }
    }
}
