using System.Text;
using System.Text.RegularExpressions;

namespace NodeRunner.Editor;

// What an imported FiveM resource declares besides its stream folder: data files (interior sounds, timecycle, .meta...)
// and the "files" they need. They are kept in the project's "resource" folder with their manifest lines, and put back
// in the exported resource. Scripts are only listed: code tied to the original server, never copied.
public static partial class ResourceExtras
{
    public const string FolderName = "resource";
    public const string LinesFileName = "fxmanifest-extra.lua";

    private static readonly string[] ManifestNames = [FiveMResource.ManifestFileName, "__resource.lua"];

    // Declared by the export itself from the .ytyp files it writes.
    private const string TypeRequest = "DLC_ITYP_REQUEST";

    private static readonly string[] ScriptKeys =
        ["client_script", "client_scripts", "server_script", "server_scripts", "shared_script", "shared_scripts", "ui_page", "loadscreen"];

    private static readonly string[] CodeExtensions = [".lua", ".js", ".dll", ".net.dll", ".html", ".css"];

    public sealed record Manifest(List<(string Type, string Path)> DataFiles, List<string> Files, List<string> Scripts);

    public static bool IsManifest(string name) => ManifestNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static Manifest Parse(string lua)
    {
        var manifest = new Manifest([], [], []);
        string text = LongComment().Replace(lua, "");
        text = LineComment().Replace(text, "");
        foreach (Match statement in Statement().Matches(text))
        {
            string key = statement.Groups[1].Value;
            var values = Quoted().Matches(statement.Groups[2].Value).Select(m => m.Groups[2].Value.Replace('\\', '/').Trim()).ToList();
            if (key == "data_file" && values.Count >= 2)
            {
                foreach (string path in values.Skip(1))
                    manifest.DataFiles.Add((values[0], path));
            }
            else if (key is "file" or "files")
                manifest.Files.AddRange(values);
            else if (ScriptKeys.Contains(key))
                manifest.Scripts.AddRange(values);
        }
        return manifest;
    }

    // The resource files to keep (relative paths with '/', out of the stream folder), the manifest lines to write back,
    // and what is left out (scripts, declared files that are missing).
    public static (List<string> Files, List<string> Lines, List<string> LeftOut) Select(Manifest manifest, IReadOnlyCollection<string> resourceFiles)
    {
        var candidates = resourceFiles.Select(f => f.Replace('\\', '/'))
            .Where(f => !f.StartsWith(FiveMResource.StreamFolderName + "/", StringComparison.OrdinalIgnoreCase)
                && !IsManifest(f) && !CodeExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var kept = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var leftOut = new List<string>();
        var lines = new List<string>();

        var dataFiles = manifest.DataFiles.Where(d => !d.Type.Equals(TypeRequest, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (type, path) in dataFiles)
        {
            // The game adds the extension of some types (AUDIO_GAMEDATA "x.dat" is x.dat151.rel): a prefix match.
            var found = candidates.Where(f => Matches(path, f) || f.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
            {
                leftOut.Add(L($"{path} ({type}, fichier absent)"));
                continue;
            }
            kept.UnionWith(found);
            lines.Add($"data_file '{type}' '{path}'");
        }
        if (lines.Count > 0)
        {
            // Files the data files need (audio banks .awc...), as the resource listed them.
            foreach (string pattern in manifest.Files)
                kept.UnionWith(candidates.Where(f => Matches(pattern, f)));
        }
        foreach (string script in manifest.Scripts.Distinct(StringComparer.OrdinalIgnoreCase))
            leftOut.Add(L($"{script} (script non repris)"));

        if (kept.Count > 0)
        {
            var files = new List<string> { "files {" };
            files.AddRange(kept.Select(f => $"    '{f}',"));
            files.Add("}");
            lines.InsertRange(0, files);
        }
        return ([.. kept], lines, leftOut);
    }

    // FiveM's patterns: "*" within a folder, "**" across folders.
    public static bool Matches(string pattern, string path)
    {
        var regex = new StringBuilder("^");
        string p = pattern.Replace('\\', '/');
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                regex.Append(".*");
                i++;
                if (i + 1 < p.Length && p[i + 1] == '/')
                    i++;
            }
            else if (p[i] == '*')
                regex.Append("[^/]*");
            else if (p[i] == '?')
                regex.Append("[^/]");
            else
                regex.Append(Regex.Escape(p[i].ToString()));
        }
        return Regex.IsMatch(path, regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Adds lines to the project's kept lines (a later import), without repeating any.
    public static void AddLines(string projectFolder, IEnumerable<string> lines)
    {
        string path = Path.Combine(projectFolder, FolderName, LinesFileName);
        var all = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var dataFiles = all.Where(l => l.StartsWith("data_file", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var files = new SortedSet<string>(all.Where(l => l.StartsWith("    '", StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            if (line.StartsWith("data_file", StringComparison.Ordinal))
                dataFiles.Add(line);
            else if (line.StartsWith("    '", StringComparison.Ordinal))
                files.Add(line);
        }
        var text = new List<string>();
        if (files.Count > 0)
        {
            text.Add("files {");
            text.AddRange(files);
            text.Add("}");
        }
        text.AddRange(all.Where(l => l.StartsWith("data_file", StringComparison.Ordinal)));
        text.AddRange(dataFiles.Where(l => !all.Contains(l)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, text);
    }

    // The kept lines of a project, empty when it has none.
    public static List<string> ReadLines(string projectFolder)
    {
        string path = Path.Combine(projectFolder, FolderName, LinesFileName);
        return File.Exists(path) ? [.. File.ReadAllLines(path)] : [];
    }

    // The kept files of a project (relative paths with '/'), without the lines file.
    public static List<string> ReadFiles(string projectFolder)
    {
        string folder = Path.Combine(projectFolder, FolderName);
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => !f.Equals(LinesFileName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"--\[(=*)\[.*?\]\1\]", RegexOptions.Singleline)]
    private static partial Regex LongComment();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\b([A-Za-z_]+)\s*\(?((?:\s*,?\s*(?:'[^'\n]*'|""[^""\n]*""|\{[^}]*\}))+)")]
    private static partial Regex Statement();

    [GeneratedRegex(@"(['""])(.*?)\1")]
    private static partial Regex Quoted();
}
