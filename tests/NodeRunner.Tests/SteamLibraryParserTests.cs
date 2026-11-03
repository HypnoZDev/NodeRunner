using NodeRunner.Data.Installation.Sources;

namespace NodeRunner.Tests;

public class SteamLibraryParserTests
{
    [Fact]
    public void ParsesEveryLibraryPath_AndUnescapesBackslashes()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                    "apps"
                    {
                        "228980"		"123"
                    }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                    "apps"
                    {
                        "271590"		"95000000000"
                    }
                }
            }
            """;

        var paths = SteamLibraryParser.ParseLibraryPaths(vdf);

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], paths);
    }

    [Fact]
    public void ReturnsEmptyList_ForGarbage()
    {
        Assert.Empty(SteamLibraryParser.ParseLibraryPaths("not a vdf file"));
    }
}
