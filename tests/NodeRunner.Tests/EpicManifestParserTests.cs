using NodeRunner.Data.Installation.Sources;

namespace NodeRunner.Tests;

public class EpicManifestParserTests
{
    [Theory]
    [InlineData("Grand Theft Auto V")]
    [InlineData("Grand Theft Auto V Enhanced")]
    public void ReturnsInstallLocation_ForTheGame(string displayName)
    {
        var json = $$"""{ "DisplayName": "{{displayName}}", "InstallLocation": "E:\\Epic\\GTAV" }""";
        Assert.Equal(@"E:\Epic\GTAV", EpicManifestParser.TryGetGameInstallLocation(json));
    }

    [Fact]
    public void ReturnsNull_ForAnotherGame()
    {
        const string json = """{ "DisplayName": "Fortnite", "InstallLocation": "E:\\Epic\\Fortnite" }""";
        Assert.Null(EpicManifestParser.TryGetGameInstallLocation(json));
    }
}
