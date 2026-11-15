using System.Text;

namespace NodeRunner.Data.Native.Gta4;

// A placement file. LodFile holds the LOD parents of its instances: the file itself for a main ".wpl", the main
// ".wpl" of the area for a streamed one ("manhat01.img\manhat01_stream3.wpl" -> "manhat01.wpl").
public sealed class Gta4MapFile(string path, Gta4Instance[] instances)
{
    public string Path { get; } = path;

    public Gta4Instance[] Instances { get; } = instances;

    public Gta4MapFile? LodFile { get; internal set; }

    public Gta4Instance? LodParentOf(in Gta4Instance instance) =>
        LodFile is { } lod && instance.LodParent >= 0 && instance.LodParent < lod.Instances.Length ? lod.Instances[instance.LodParent] : null;
}

// The map of the base game, as "common\data\gta.dat" lists it: definitions of every "IDE" line, then for every "IPL"
// line the main ".wpl" next to it and the streamed ".wpl" files of the archive of the same name.
public sealed class Gta4Map
{
    public const string LoadList = @"common\data\gta.dat";

    public Gta4Definitions Definitions { get; } = new();

    public List<Gta4MapFile> Files { get; } = [];

    public List<string> Errors { get; } = [];

    public int InstanceCount => Files.Sum(f => f.Instances.Length);

    public static Gta4Map Load(Gta4FileSystem files)
    {
        var map = new Gta4Map();
        if (files.ReadFile(LoadList) is not { } list)
        {
            map.Errors.Add($"{LoadList} introuvable");
            return map;
        }

        var streamed = files.EnumerateFiles(".wpl")
            .GroupBy(p => System.IO.Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, path) in ReadLoadList(Encoding.ASCII.GetString(list)))
        {
            if (kind == "IDE")
                map.ReadDefinitions(files, path);
            else if (kind == "IPL")
                map.ReadPlacements(files, path, streamed);
        }
        return map;
    }

    // "IDE platform:/DATA/MAPS/MANHAT/MANHAT01.IDE" -> ("IDE", @"pc\data\maps\manhat\manhat01.ide").
    public static IEnumerable<(string Kind, string Path)> ReadLoadList(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int space = line.IndexOfAny([' ', '\t']);
            if (space < 0)
                continue;
            string kind = line[..space].ToUpperInvariant();
            if (kind is "IDE" or "IPL" && GamePath(line[space..].Trim()) is { } path)
                yield return (kind, path);
        }
    }

    // Game device path to a path of the game folder: "platform:/" is the "pc" folder, "common:/" the "common" folder.
    public static string? GamePath(string device)
    {
        int colon = device.IndexOf(":/", StringComparison.Ordinal);
        if (colon < 0)
            return null;
        string root = device[..colon].ToLowerInvariant() switch
        {
            "platform" => "pc",
            "common" => "common",
            _ => "",
        };
        if (root.Length == 0)
            return null;
        string rest = device[(colon + 2)..].Replace('/', '\\').Trim('\\');
        return rest.Length == 0 || rest.Contains("..") ? null : (root + "\\" + rest).ToLowerInvariant();
    }

    private void ReadDefinitions(Gta4FileSystem files, string path)
    {
        if (files.ReadFile(path) is not { } data)
        {
            Errors.Add($"{path} : introuvable");
            return;
        }
        int bad = Definitions.Read(Encoding.ASCII.GetString(data));
        if (bad > 0)
            Errors.Add($"{path} : {bad} ligne(s) illisible(s)");
    }

    private void ReadPlacements(Gta4FileSystem files, string path, Dictionary<string, List<string>> streamed)
    {
        // Text placement files (paths, culling, zones) are not map content.
        string main = System.IO.Path.ChangeExtension(path, ".wpl");
        var mainFile = ReadFile(files, main);
        if (streamed.TryGetValue(System.IO.Path.ChangeExtension(path, ".img"), out var parts))
        {
            foreach (string part in parts)
            {
                if (ReadFile(files, part) is { } file)
                    file.LodFile = mainFile;
            }
        }
        if (mainFile is not null)
            mainFile.LodFile = mainFile;
    }

    private Gta4MapFile? ReadFile(Gta4FileSystem files, string path)
    {
        if (files.ReadFile(path) is not { } data)
            return null;
        try
        {
            var file = new Gta4MapFile(path, Gta4Placements.Read(data));
            Files.Add(file);
            return file;
        }
        catch (InvalidDataException ex)
        {
            Errors.Add($"{path} : {ex.Message}");
            return null;
        }
    }
}
