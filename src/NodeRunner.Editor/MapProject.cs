using System.Text.Json;
using System.Text.Json.Serialization;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Editor;

// A mapping project (a hospital, a police station...): a folder holding projet.json (name, map shown, FiveM resource)
// and a "stream" folder with the saved map files.
public sealed class MapProject
{
    public const string FileName = "projet.json";
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Format { get; set; } = CurrentFormat;

    public string Name { get; set; } = "";

    public MapVersion MapVersion { get; set; } = MapVersion.Multiplayer;

    public string? LastDlcPack { get; set; }

    public string ResourceName { get; set; } = FiveMResource.DefaultName;

    public string? ExportFolder { get; set; }

    // Name (without extension) of the project's own map, holding the objects added by the user; chosen once, from the
    // resource name, so that it never changes afterwards (null until the first object is added).
    public string? AddedMapName { get; set; }

    public string AddedMapFile()
    {
        AddedMapName ??= $"{ResourceName}_props";
        return AddedMapName + ".ymap";
    }

    [JsonIgnore]
    public string Folder { get; private set; } = "";

    [JsonIgnore]
    public string StreamFolder => Path.Combine(Folder, FiveMResource.StreamFolderName);

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, FileName));

    public static bool HasMapFiles(string folder)
    {
        string stream = Path.Combine(folder, FiveMResource.StreamFolderName);
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.ymap").Any()
               || Directory.Exists(stream) && Directory.EnumerateFiles(stream, "*.ymap").Any();
    }

    public static MapProject Load(string folder)
    {
        var project = JsonSerializer.Deserialize<MapProject>(File.ReadAllText(Path.Combine(folder, FileName)), JsonOptions)
                      ?? throw new InvalidDataException($"{FileName} est vide.");
        project.Folder = Path.GetFullPath(folder);
        if (string.IsNullOrWhiteSpace(project.Name))
            project.Name = Path.GetFileName(project.Folder);
        if (!FiveMResource.IsValidName(project.ResourceName))
            project.ResourceName = FiveMResource.ToValidName(project.Name);
        return project;
    }

    // Makes a project of a folder (created if needed).
    public static MapProject Create(string folder, string name, MapVersion mapVersion, string? lastDlcPack)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var project = new MapProject
        {
            Folder = folder,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name.Trim(),
            MapVersion = mapVersion,
            LastDlcPack = lastDlcPack,
        };
        project.ResourceName = FiveMResource.ToValidName(project.Name);
        Directory.CreateDirectory(project.StreamFolder);
        foreach (var file in Directory.EnumerateFiles(folder, "*.ymap").ToList())
        {
            string target = Path.Combine(project.StreamFolder, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Move(file, target);
        }
        project.Save();
        return project;
    }

    public void Save()
    {
        string path = Path.Combine(Folder, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static string ToFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string folder = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return folder.Length > 0 ? folder : "Projet";
    }
}
