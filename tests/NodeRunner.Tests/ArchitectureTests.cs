namespace NodeRunner.Tests;

/// <summary>Guards the dependencies: NodeRunner reads the game's files with its own code only.</summary>
public class ArchitectureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NodeRunner.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("NodeRunner.sln not found.");
    }

    [Fact]
    public void NoProject_ReferencesCodeWalkerOrSharpDx()
    {
        var root = RepoRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Where(p =>
            {
                var text = File.ReadAllText(p);
                return text.Contains("CodeWalker", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("SharpDX", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Solution_HasNoExternalProjects()
    {
        var solution = File.ReadAllText(Path.Combine(RepoRoot(), "NodeRunner.sln"));
        Assert.DoesNotContain("external", solution, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), ".gitmodules")));
    }
}
