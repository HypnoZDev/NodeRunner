using System.IO.Compression;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Editor;

// Ignored: package commands that give no streamed file (whole .rpf archives, xml edits...). Author: from an OpenIV package.
public sealed record ImportResult(MapProject Project, int Copied, IReadOnlyList<string> Skipped, IReadOnlyList<string> Ignored, string? Author)
{
    // Copied files locked by FiveM's Asset Escrow (see ProtectedFile).
    public IReadOnlyList<string> Protected { get; init; } = [];
}

// Imports a mapping made elsewhere as a new project: a FiveM resource or a plain "stream" folder, a .zip of one, or an
// OpenIV package (.oiv, or its unzipped folder). Its streamed files are copied into the project's stream folder, where
// a file with a game name replaces the game's file (like in FiveM); the original is never changed.
public static class MappingImport
{
    public static readonly string[] StreamedExtensions =
        [".ymap", ".ytyp", ".ybn", ".ydr", ".ydd", ".yft", ".ytd", ".ynd", ".ynv", ".ymf", ".ycd", ".ymt"];

    public static readonly string[] ArchiveExtensions = [".oiv", ".zip"];

    public static bool IsStreamed(string name) => StreamedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // What can be dropped on the window or picked: a folder, a .oiv or a .zip.
    public static bool CanImport(string path) => Directory.Exists(path) || File.Exists(path) && IsArchive(path);

    // Streamed files anywhere under the folder (resources often nest them: stream/maps/..., several resources...).
    // One file per name: FiveM streams by name, the first one found wins.
    public static (List<string> Files, List<string> Duplicates) FindFiles(string folder)
    {
        var files = new List<string>();
        var duplicates = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsStreamed(file))
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

    private sealed class Plan : IDisposable
    {
        public string? Name;
        public string? Author;
        public readonly List<(string Name, Func<Stream> Open)> Files = [];
        public readonly List<string> Duplicates = [];
        public readonly List<string> Ignored = [];
        public readonly List<IDisposable> Archives = [];
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, string shown, Func<Stream> open)
        {
            if (_names.Add(name))
                Files.Add((name, open));
            else
                Duplicates.Add(shown);
        }

        public void Dispose()
        {
            foreach (var archive in Archives)
                archive.Dispose();
        }
    }

    public static ImportResult Import(string source, string projectsFolder, MapVersion mapVersion, string? lastDlcPack)
    {
        source = Path.GetFullPath(source.TrimEnd('\\', '/'));
        using var plan = new Plan();
        string name;
        if (File.Exists(source))
        {
            if (!IsArchive(source))
                throw new InvalidDataException(L("Choisis un dossier, un mod OpenIV (.oiv) ou une archive .zip."));
            AddArchive(plan, source);
            name = Path.GetFileNameWithoutExtension(source);
        }
        else
        {
            // A folder named "stream" is not a good project name: take its parent's.
            name = Path.GetFileName(source);
            if (string.Equals(name, FiveMResource.StreamFolderName, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(source) is { } parent)
                name = Path.GetFileName(parent);
            if (OivPackage.IsPackageFolder(source))
                AddPackageFolder(plan, source);
            else
                AddFolder(plan, source);
        }
        if (plan.Files.Count == 0)
            throw new InvalidDataException(L("Aucun fichier de carte (.ymap, .ybn, .ytyp, .ydr...) ni mod OpenIV (.oiv) à importer ici."));

        name = plan.Name ?? name;
        var project = MapProject.Create(FreeFolder(projectsFolder, name), name, mapVersion, lastDlcPack);
        var locked = new List<string>();
        foreach (var (file, open) in plan.Files)
        {
            string target = Path.Combine(project.StreamFolder, file);
            using (var input = open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                input.CopyTo(output);
            if (ProtectedFile.IsProtected(target))
                locked.Add(file);
        }
        return new ImportResult(project, plan.Files.Count, plan.Duplicates, plan.Ignored, plan.Author) { Protected = locked };
    }

    // Loose files first; a folder without any (a downloaded mod: readme + .oiv) takes the packages inside it.
    private static void AddFolder(Plan plan, string folder)
    {
        var (files, duplicates) = FindFiles(folder);
        foreach (var file in files)
            plan.Add(Path.GetFileName(file), Path.GetRelativePath(folder, file), () => File.OpenRead(file));
        plan.Duplicates.AddRange(duplicates);
        if (files.Count > 0)
            return;
        foreach (var archive in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsArchive).Order(StringComparer.OrdinalIgnoreCase))
            AddArchive(plan, archive);
    }

    private static void AddPackageFolder(Plan plan, string folder)
    {
        var package = ReadPackage(plan, File.ReadAllText(Path.Combine(folder, OivPackage.AssemblyFileName)));
        foreach (var entry in package.Entries)
        {
            string file = Path.Combine(folder, OivPackage.ContentFolderName, entry.Source);
            if (File.Exists(file))
                plan.Add(entry.Name, entry.Source, () => File.OpenRead(file));
            else
                plan.Ignored.Add(L($"{entry.Name} (absent du paquet)"));
        }
    }

    private static void AddArchive(Plan plan, string path)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(L($"{Path.GetFileName(path)} n'est pas une archive lisible : {ex.Message}"), ex);
        }
        plan.Archives.Add(zip);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
            entries.TryAdd(OivPackage.NormalizePath(entry.FullName), entry);
        string archive = Path.GetFileName(path);

        if (entries.TryGetValue(OivPackage.AssemblyFileName, out var assembly))
        {
            string xml;
            using (var reader = new StreamReader(assembly.Open()))
                xml = reader.ReadToEnd();
            var package = ReadPackage(plan, xml);
            foreach (var item in package.Entries)
            {
                if (entries.TryGetValue(OivPackage.NormalizePath($"{OivPackage.ContentFolderName}/{item.Source}"), out var file))
                    plan.Add(item.Name, $"{archive} : {item.Source}", file.Open);
                else
                    plan.Ignored.Add(L($"{item.Name} (absent du paquet)"));
            }
            return;
        }
        foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0 && IsStreamed(e.Name)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            plan.Add(entry.Name, $"{archive} : {entry.FullName}", entry.Open);
    }

    private static OivPackage ReadPackage(Plan plan, string xml)
    {
        var package = OivPackage.Parse(xml, IsStreamed);
        plan.Name ??= package.Name;
        plan.Author ??= package.Author;
        plan.Ignored.AddRange(package.Ignored);
        return package;
    }
}
