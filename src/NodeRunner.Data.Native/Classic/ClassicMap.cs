using System.Text;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Classic;

// A placement file. LodFile holds the LOD instances of San Andreas: the file itself for a text ".ipl", the text ".ipl"
// of the area for a streamed one ("gta3.img\lae2_stream0.ipl" -> "data\maps\la\lae2.ipl").
public sealed class ClassicMapFile(string path, ClassicInstance[] instances)
{
    public string Path { get; } = path;

    public ClassicInstance[] Instances { get; } = instances;

    public ClassicMapFile? LodFile { get; internal set; }

    public ClassicInstance? LodOf(in ClassicInstance instance) =>
        LodFile is { } lod && instance.Lod >= 0 && instance.Lod < lod.Instances.Length ? lod.Instances[instance.Lod] : null;
}

// The map as the game loads it: "data\default.dat" then the game's list ("gta3.dat", "gta_vc.dat", "gta.dat"). Every
// "IDE" line adds definitions; every "IPL" line a text placement file and, in San Andreas, the binary files of the same
// name streamed from the archives ("lae2_stream0.ipl"...). Collision files and extra archives are listed for later.
public sealed class ClassicMap
{
    public const string DefaultList = @"data\default.dat";

    public ClassicGame Game { get; private init; }

    public ClassicDefinitions Definitions { get; } = new();

    public List<ClassicMapFile> Files { get; } = [];

    public List<string> CollisionFiles { get; } = [];

    public List<string> Archives { get; } = [];

    public List<string> Errors { get; } = [];

    public int InstanceCount => Files.Sum(f => f.Instances.Length);

    public static ClassicMap Load(ClassicFileSystem files)
    {
        var map = new ClassicMap { Game = files.Game };
        var entries = new List<(string Kind, string Path)>();
        foreach (string list in new[] { DefaultList, ClassicInstallLocator.MapList(files.Game) })
        {
            if (files.ReadFile(list) is { } data)
                entries.AddRange(ReadLoadList(Encoding.ASCII.GetString(data)));
            else
                map.Errors.Add($"{list} introuvable");
        }

        var streamed = files.EnumerateFiles(".ipl")
            .Where(p => p.Contains(".img" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToLookup(StreamOwner, StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, path) in entries)
        {
            switch (kind)
            {
                case "IDE":
                    map.ReadDefinitions(files, path);
                    break;
                case "IPL":
                    map.ReadPlacements(files, path, streamed);
                    break;
                case "COLFILE":
                    map.CollisionFiles.Add(path);
                    break;
                case "IMG" or "CDIMAGE":
                    map.Archives.Add(path);
                    break;
            }
        }
        return map;
    }

    // "IPL DATA\MAPS\LA\LAE2.IPL" -> ("IPL", @"data\maps\la\lae2.ipl"); "COLFILE 0 MODELS\COLL\GENERIC.COL" keeps the path.
    public static IEnumerable<(string Kind, string Path)> ReadLoadList(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment].Trim();
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            string kind = parts[0].ToUpperInvariant();
            if (kind is not ("IDE" or "IPL" or "COLFILE" or "IMG" or "CDIMAGE"))
                continue;
            string path = (kind == "COLFILE" ? parts[^1] : parts[1]).Replace('/', '\\').Trim('\\').ToLowerInvariant();
            if (path.Length > 0 && !path.Contains("..") && !path.Contains(':'))
                yield return (kind, path);
        }
    }

    // "lae2_stream12.ipl" -> "lae2" (null for a file that is not a streamed part).
    private static string StreamOwner(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        int stream = name.LastIndexOf("_stream", StringComparison.OrdinalIgnoreCase);
        return stream > 0 ? name[..stream] : "";
    }

    private void ReadDefinitions(ClassicFileSystem files, string path)
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

    private void ReadPlacements(ClassicFileSystem files, string path, ILookup<string, string> streamed)
    {
        var main = ReadFile(files, path);
        if (main is not null)
            main.LodFile = main;
        if (Game != ClassicGame.SanAndreas)
            return;
        foreach (string part in streamed[System.IO.Path.GetFileNameWithoutExtension(path)].Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadFile(files, part) is { } file)
                file.LodFile = main;
        }
    }

    private ClassicMapFile? ReadFile(ClassicFileSystem files, string path)
    {
        if (files.ReadFile(path) is not { } data)
        {
            Errors.Add($"{path} : introuvable");
            return null;
        }
        try
        {
            ClassicInstance[] instances;
            if (ClassicPlacements.IsBinary(data))
            {
                instances = ClassicPlacements.ReadBinary(data);
            }
            else
            {
                instances = ClassicPlacements.ReadText(Encoding.ASCII.GetString(data), Game, out int bad);
                if (bad > 0)
                    Errors.Add($"{path} : {bad} ligne(s) illisible(s)");
            }
            var file = new ClassicMapFile(path, instances);
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
