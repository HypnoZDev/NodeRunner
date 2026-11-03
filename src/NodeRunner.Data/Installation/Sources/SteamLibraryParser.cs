using System.Text.RegularExpressions;

namespace NodeRunner.Data.Installation.Sources;

/// <summary>
/// Minimal reader for Steam's libraryfolders.vdf (Valve KeyValues text format).
/// We only need the "path" entries, so a full parser is not necessary.
/// </summary>
public static partial class SteamLibraryParser
{
    // Matches:  "path"		"D:\\SteamLibrary"
    [GeneratedRegex("\"path\"\\s+\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();

    public static IReadOnlyList<string> ParseLibraryPaths(string vdfText)
    {
        var paths = new List<string>();
        foreach (Match m in PathEntry().Matches(vdfText))
        {
            // VDF escapes backslashes ("D:\\SteamLibrary").
            var path = m.Groups["p"].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }
        return paths;
    }
}
