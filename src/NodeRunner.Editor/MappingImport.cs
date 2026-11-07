using NodeRunner.Data.FileSystem;

namespace NodeRunner.Editor;

public sealed record ImportResult(MapProject Project, int Copied, IReadOnlyList<string> Skipped);

// Imports a mapping made elsewhere (a FiveM resource, or a plain "stream" folder) as a new project: its streamed files
// are copied into the project's stream folder, the original folder is never changed.
public static class MappingImport
{
    public static readonly string[] StreamedExtensions =
        [".ymap", ".ytyp", ".ybn", ".ydr", ".ydd", ".yft", ".ytd", ".ynd", ".ynv", ".ymf", ".ycd", ".ymt"];

    // Streamed files anywhere under the folder (resources often nest them: stream/maps/..., several resources...).
    // One file per name: FiveM streams by name, the first one found wins.
    public static (List<string> Files, List<string> Duplicates) FindFiles(string folder)
    {
        var files = new List<string>();
        var duplicates = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!StreamedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            if (names.Add(Path.GetFileName(file)))
                files.Add(file);
            else
                duplicates.Add(Path.GetRelativePath(folder, file));
        }
        return (files, duplicates);
    }

    // A free folder for the project in projectsFolder ("name", then "name 2"...).
    public static string FreeFolder(string projectsFolder, string name)
    {
        string baseName = MapProject.ToFolderName(name);
        string folder = Path.Combine(projectsFolder, baseName);
        for (int i = 2; Directory.Exists(folder) || File.Exists(folder); i++)
            folder = Path.Combine(projectsFolder, $"{baseName} {i}");
        return folder;
    }

    public static ImportResult Import(string source, string projectsFolder, MapVersion mapVersion, string? lastDlcPack)
    {
        source = Path.GetFullPath(source);
        var (files, duplicates) = FindFiles(source);
        if (files.Count == 0)
            throw new InvalidDataException(L("Aucun fichier de carte (.ymap, .ybn, .ytyp...) dans ce dossier."));

        // A folder named "stream" is not a good project name: take its parent's.
        string name = Path.GetFileName(source);
        if (string.Equals(name, FiveMResource.StreamFolderName, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(source) is { } parent)
            name = Path.GetFileName(parent);

        var project = MapProject.Create(FreeFolder(projectsFolder, name), name, mapVersion, lastDlcPack);
        foreach (var file in files)
            File.Copy(file, Path.Combine(project.StreamFolder, Path.GetFileName(file)), overwrite: false);
        return new ImportResult(project, files.Count, duplicates);
    }
}
