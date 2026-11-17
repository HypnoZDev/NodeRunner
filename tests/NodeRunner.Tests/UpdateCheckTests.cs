using NodeRunner.App;

namespace NodeRunner.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.6.0", "1.5.1", "1.6.0")]
    [InlineData("v1.5.2", "1.5.1", "1.5.2")]
    [InlineData("1.10.0", "1.9.3", "1.10.0")]
    [InlineData("v1.5.1", "1.5.1", null)]
    [InlineData("v1.5.0", "1.5.1", null)]
    [InlineData("nightly", "1.5.1", null)]
    [InlineData("", "1.5.1", null)]
    public void Newer_OnlyAboveTheCurrentVersion(string tag, string current, string? expected) =>
        Assert.Equal(expected, UpdateCheck.Newer(tag, current));
}
