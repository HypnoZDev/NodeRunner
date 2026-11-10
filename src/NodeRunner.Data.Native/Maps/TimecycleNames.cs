using System.Text.RegularExpressions;
using NodeRunner.Data.Native.Archives;

namespace NodeRunner.Data.Native.Maps;

// Names of the timecycle modifiers, from the game's timecycle_mods_*.xml (the map files only store their hash).
public static partial class TimecycleNames
{
    [GeneratedRegex("<modifier name=\"([^\"]+)\"")]
    private static partial Regex ModifierName();

    public static HashSet<string> Read(RpfFileSystem files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.EnumerateFiles(".xml"))
        {
            if (!Path.GetFileName(path).StartsWith("timecycle_mods", StringComparison.OrdinalIgnoreCase) || files.ReadFile(path) is not { } bytes)
                continue;
            foreach (Match match in ModifierName().Matches(System.Text.Encoding.UTF8.GetString(bytes)))
                names.Add(match.Groups[1].Value);
        }
        return names;
    }
}
