using System.Text.Json;
using System.Text.Json.Serialization;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Editor;

/// <summary>
/// A mapping project (a hospital, a police station...): a folder holding projet.json (name, map shown, FiveM
/// resource) and a "stream" folder with the saved map files. One project = one FiveM resource.
/// </summary>
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

    /// <summary>Version of the file's layout, for later changes.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>Name shown in the interface (free text).</summary>
    public string Name { get; set; } = "";

    /// <summary>Map the project was made on: multiplayer (FiveM) or story mode.</summary>
    public MapVersion MapVersion { get; set; } = MapVersion.Multiplayer;

    /// <summary>Last DLC pack applied (see the toolbar), null = all.</summary>
    public string? LastDlcPack { get; set; }

    /// <summary>Name of the exported FiveM resource (server.cfg "ensure ...").</summary>
    public string ResourceName { get; set; } = FiveMResource.DefaultName;

    /// <summary>Folder where the resource is exported (typically a server's "resources" folder), null until picked.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>
    /// Name (without extension) of the project's own map, holding the objects added by the user; chosen once, from
    /// the resource name, so that it never changes afterwards (null until the first object is added).
    /// </summary>
    public string? AddedMapName { get; set; }

    /// <summary>File name of the map of added objects, chosen if not yet (then the project must be saved).</summary>
    public string AddedMapFile()
    {
        AddedMapName ??= $"{ResourceName}_props";
        return AddedMapName + ".ymap";
    }

    /// <summary>The project's folder (not stored in the file: the folder can be moved).</summary>
    [JsonIgnore]
    public string Folder { get; private set; } = "";

    /// <summary>Folder of the saved map files.</summary>
    [JsonIgnore]
    public string StreamFolder => Path.Combine(Folder, FiveMResource.StreamFolderName);

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, FileName));

    /// <summary>The folder holds map files, loose or in "stream" (they become the project's when it is created).</summary>
    public static bool HasMapFiles(string folder)
    {
        string stream = Path.Combine(folder, FiveMResource.StreamFolderName);
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.ymap").Any()
               || Directory.Exists(stream) && Directory.EnumerateFiles(stream, "*.ymap").Any();
    }

    /// <summary>Reads the project of a folder. Throws if the file is missing or unreadable.</summary>
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

    /// <summary>
    /// Makes a project of a folder (created if needed). Map files lying directly in it (the save folder of
    /// earlier versions) are moved into the "stream" folder, unless one of that name is already there.
    /// </summary>
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

    /// <summary>Folder name for a project name: the characters Windows refuses become '_'.</summary>
    public static string ToFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string folder = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return folder.Length > 0 ? folder : "Projet";
    }
}
