using NodeRunner.Data.Installation;

namespace NodeRunner.Tests;

public sealed class GameInstallLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NodeRunnerTests_" + Guid.NewGuid().ToString("N"));

    public GameInstallLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Creates a fake game folder containing an empty executable (no real game file involved).</summary>
    private string FakeGameFolder(string name, string exe)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, exe), []);
        return folder;
    }

    private sealed class FakeSource(string name, params string[] folders) : IInstallCandidateSource
    {
        public string Name => name;
        public IEnumerable<string> GetCandidateFolders() => folders;
    }

    private sealed class BrokenSource : IInstallCandidateSource
    {
        public string Name => "Broken";
        public IEnumerable<string> GetCandidateFolders() => throw new IOException("boom");
    }

    [Fact]
    public void DetectsLegacyAndEnhanced_FromTheExecutable()
    {
        Assert.Equal(GameEdition.Legacy, GameFolderValidator.DetectEdition(FakeGameFolder("legacy", "GTA5.exe")));
        Assert.Equal(GameEdition.Enhanced, GameFolderValidator.DetectEdition(FakeGameFolder("enh", "GTA5_Enhanced.exe")));
        Assert.Null(GameFolderValidator.DetectEdition(Path.Combine(_root, "missing")));
        Assert.Null(GameFolderValidator.DetectEdition(null));
    }

    [Fact]
    public void IgnoresInvalidFolders_Duplicates_AndBrokenSources()
    {
        var legacy = FakeGameFolder("legacy", "GTA5.exe");
        var enhanced = FakeGameFolder("enhanced", "GTA5_Enhanced.exe");

        var locator = new GameInstallLocator(
        [
            new BrokenSource(),
            new FakeSource("Steam", Path.Combine(_root, "nope"), enhanced),
            new FakeSource("Rockstar Launcher", legacy, enhanced + Path.DirectorySeparatorChar),
        ]);

        var all = locator.FindAll();

        Assert.Equal(2, all.Count);
        Assert.Equal("Steam", all[0].Source);
        Assert.Equal(GameEdition.Enhanced, all[0].Edition);
        Assert.Equal(GameEdition.Legacy, all[1].Edition);
    }

    [Fact]
    public void PrefersLegacy()
    {
        var legacy = FakeGameFolder("legacy", "GTA5.exe");
        var enhanced = FakeGameFolder("enhanced", "GTA5_Enhanced.exe");

        var preferred = new GameInstallLocator([new FakeSource("Test", enhanced, legacy)]).FindPreferred();

        Assert.Equal(GameEdition.Legacy, preferred?.Edition);
    }
}
