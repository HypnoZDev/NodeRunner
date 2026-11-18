using System.IO.Compression;
using NodeRunner.Data.FileSystem;

namespace NodeRunner.Editor;

// Ignored: package commands that give no streamed file (whole .rpf archives, xml edits...). Author: from an OpenIV package.
public sealed record ImportResult(MapProject Project, int Copied, IReadOnlyList<string> Skipped, IReadOnlyList<string> Ignored, string? Author)
{
    // Copied files locked by FiveM's Asset Escrow (see ProtectedFile).
    public IReadOnlyList<string> Protected { get; init; } = [];

    // XML exports (Sollumz, CodeWalker) converted to game files, among Copied.
    public int Converted { get; init; }

    // Files of the project replaced by a file of the same name, among Copied (AddToProject).
    public int Replaced { get; init; }

    // Data files of the imported resource kept in the project's resource folder (sounds, timecycle, .meta...).
    public int ResourceFiles { get; init; }
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

    // A streamed file exported as XML by Sollumz or CodeWalker ("name.ybn.xml").
    public static bool IsXmlExport(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && IsStreamed(name[..^4]);

    public static bool IsImported(string name) => IsStreamed(name) || IsXmlExport(name);

    // Game file name of an imported file ("name.ybn.xml" -> "name.ybn").
    private static string TargetName(string name) => IsXmlExport(name) ? name[..^4] : name;

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // What can be dropped on the window or picked: a folder, a .oiv, a .zip, or a map file or its XML export.
    public static bool CanImport(string path) =>
        Directory.Exists(path) || File.Exists(path) && (IsArchive(path) || IsImported(Path.GetFileName(path)));

    // Streamed files anywhere under the folder (resources often nest them: stream/maps/..., several resources...).
    // One file per name: FiveM streams by name, the first one found wins.
    public static (List<string> Files, List<string> Duplicates) FindFiles(string folder, bool withXmlExports = false)
    {
        var files = new List<string>();
        var duplicates = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!(IsStreamed(file) || withXmlExports && IsXmlExport(file)))
                continue;
            if (names.Add(TargetName(Path.GetFileName(file))))
                files.Add(file);
            else
                duplicates.Add(Path.GetRelativePath(folder, file));
        }
        return (files, duplicates);
    }

    // What to add again from a watched folder after its files changed: the changed files that can be imported; for
    // another file (an image of a .ytd.xml...), the XML exports of its folder and of the folder above, inside the
    // watched one. The watched folder itself among the changes (events lost) = all of it.
    public static List<string> WatchedSources(IEnumerable<string> changed, string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var sources = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string file)
        {
            if (seen.Add(file))
                sources.Add(file);
        }
        bool Inside(string path) => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        foreach (string path in changed.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')))
        {
            if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase))
                return Directory.Exists(folder) ? [folder] : [];
            if (!Inside(path) || !File.Exists(path))
                continue;
            if (IsImported(Path.GetFileName(path)))
            {
                Add(path);
                continue;
            }
            string? directory = Path.GetDirectoryName(path);
            for (int level = 0; level < 2 && directory is not null && (Inside(directory) || string.Equals(directory, folder, StringComparison.OrdinalIgnoreCase)); level++)
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (IsXmlExport(Path.GetFileName(file)))
                        Add(file);
                }
                if (string.Equals(directory, folder, StringComparison.OrdinalIgnoreCase))
                    break;
                directory = Path.GetDirectoryName(directory);
            }
        }
        return sources;
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
        public readonly List<(string Name, string Source, Func<Stream> Open, Func<string, byte[]?> ReadSibling)> Files = [];
        public readonly List<string> Duplicates = [];
        public readonly List<string> Ignored = [];
        public readonly List<IDisposable> Archives = [];
        // Data files of an imported resource (ResourceExtras), relative to its folder, and their manifest lines.
        public readonly List<(string Relative, Func<Stream> Open)> Extras = [];
        public readonly List<string> ExtraLines = [];
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        // readSibling: reads a file by path relative to this one's folder (the images of a .ytd.xml...).
        public void Add(string name, string shown, Func<Stream> open, Func<string, byte[]?>? readSibling = null)
        {
            if (_names.Add(TargetName(name)))
                Files.Add((TargetName(name), name, open, readSibling ?? (_ => null)));
            else
                Duplicates.Add(shown);
        }

        public void Dispose()
        {
            foreach (var archive in Archives)
                archive.Dispose();
        }
    }

    // xmlConverters: by game extension (".ybn"), turns an XML export into the game file; XML exports of other types
    // are left out.
    public static ImportResult Import(string source, string projectsFolder, MapVersion mapVersion, string? lastDlcPack,
        IReadOnlyDictionary<string, Func<XmlExport, byte[]>>? xmlConverters = null) =>
        Import([source], projectsFolder, mapVersion, lastDlcPack, xmlConverters);

    // sources: folders, archives and single files, imported together in a new project.
    public static ImportResult Import(IReadOnlyList<string> sources, string projectsFolder, MapVersion mapVersion, string? lastDlcPack,
        IReadOnlyDictionary<string, Func<XmlExport, byte[]>>? xmlConverters = null)
    {
        using var plan = Collect(sources, out string name);
        name = plan.Name ?? name;
        string folder = FreeFolder(projectsFolder, name);
        // "name 2" when "name" is taken: the resource name differs too, so that both exports can sit on one server.
        if (!string.Equals(Path.GetFileName(folder), MapProject.ToFolderName(name), StringComparison.Ordinal))
            name = Path.GetFileName(folder);
        var project = MapProject.Create(folder, name, mapVersion, lastDlcPack);
        try
        {
            return Copy(plan, project, xmlConverters);
        }
        catch
        {
            // No half-imported project left behind.
            try
            {
                Directory.Delete(project.Folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    // Adds files to an existing project (a new export from Blender...): a file of the same name in its stream folder is
    // replaced.
    public static ImportResult AddToProject(IReadOnlyList<string> sources, MapProject project,
        IReadOnlyDictionary<string, Func<XmlExport, byte[]>>? xmlConverters = null)
    {
        using var plan = Collect(sources, out _);
        Directory.CreateDirectory(project.StreamFolder);
        return Copy(plan, project, xmlConverters);
    }

    // The files to import; name: that of the first folder or archive, or of the folder of the first single file.
    private static Plan Collect(IReadOnlyList<string> sources, out string name)
    {
        var plan = new Plan();
        name = "";
        try
        {
            foreach (string path in sources)
            {
                string source = Path.GetFullPath(path.TrimEnd('\\', '/'));
                string sourceName;
                if (File.Exists(source) && IsArchive(source))
                {
                    AddArchive(plan, source);
                    sourceName = Path.GetFileNameWithoutExtension(source);
                }
                else if (File.Exists(source))
                {
                    string fileName = Path.GetFileName(source);
                    if (!IsImported(fileName))
                        throw new InvalidDataException(L($"{fileName} n'est ni un fichier de carte ni un export XML."));
                    string directory = Path.GetDirectoryName(source)!;
                    plan.Add(fileName, fileName, () => File.OpenRead(source), Sibling(directory));
                    sourceName = Path.GetFileName(directory);
                }
                else if (Directory.Exists(source))
                {
                    // A folder named "stream" is not a good project name: take its parent's.
                    sourceName = Path.GetFileName(source);
                    if (string.Equals(sourceName, FiveMResource.StreamFolderName, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(source) is { } parent)
                        sourceName = Path.GetFileName(parent);
                    if (OivPackage.IsPackageFolder(source))
                        AddPackageFolder(plan, source);
                    else
                        AddFolder(plan, source);
                }
                else
                    throw new InvalidDataException(L("Choisis un dossier, un mod OpenIV (.oiv) ou une archive .zip."));
                if (name.Length == 0)
                    name = sourceName;
            }
            if (plan.Files.Count == 0)
                throw new InvalidDataException(L("Aucun fichier de carte (.ymap, .ybn, .ytyp, .ydr..., ou leur export XML) ni mod OpenIV (.oiv) à importer ici."));
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    // Each file is written next to its target first: a failure never leaves half a file in the project.
    private static ImportResult Copy(Plan plan, MapProject project, IReadOnlyDictionary<string, Func<XmlExport, byte[]>>? xmlConverters)
    {
        var locked = new List<string>();
        int copied = 0, converted = 0, replaced = 0;
        foreach (var (file, sourceName, open, readSibling) in plan.Files)
        {
            string target = Path.Combine(project.StreamFolder, file);
            string temporary = target + ".tmp";
            bool existed = File.Exists(target);
            try
            {
                if (IsXmlExport(sourceName))
                {
                    if (xmlConverters?.GetValueOrDefault(Path.GetExtension(file).ToLowerInvariant()) is not { } convert)
                    {
                        plan.Ignored.Add(L($"{sourceName} (XML pas encore pris en charge)"));
                        continue;
                    }
                    byte[] bytes;
                    try
                    {
                        string xml;
                        using (var reader = new StreamReader(open()))
                            xml = reader.ReadToEnd();
                        bytes = convert(new XmlExport(sourceName, xml, readSibling));
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or IOException or UnauthorizedAccessException))
                    {
                        // Any unexpected XML (a field out of range...) only leaves that file out, never the whole import.
                        plan.Ignored.Add(L($"{sourceName} (conversion impossible : {ex.Message})"));
                        continue;
                    }
                    File.WriteAllBytes(temporary, bytes);
                    converted++;
                }
                else
                {
                    using var input = open();
                    using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write);
                    input.CopyTo(output);
                }
                File.Move(temporary, target, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                throw;
            }
            copied++;
            if (existed)
                replaced++;
            if (ProtectedFile.IsProtected(target))
                locked.Add(file);
        }
        int extras = CopyExtras(plan, project);
        return new ImportResult(project, copied, plan.Duplicates, plan.Ignored, plan.Author)
            { Protected = locked, Converted = converted, Replaced = replaced, ResourceFiles = extras };
    }

    // The data files of an imported resource go in the project's resource folder, with their manifest lines.
    private static int CopyExtras(Plan plan, MapProject project)
    {
        if (plan.ExtraLines.Count == 0)
            return 0;
        string folder = Path.Combine(project.Folder, ResourceExtras.FolderName);
        foreach (var (relative, open) in plan.Extras)
        {
            string target = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".tmp";
            using (var input = open())
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write))
                input.CopyTo(output);
            File.Move(temporary, target, overwrite: true);
        }
        ResourceExtras.AddLines(project.Folder, plan.ExtraLines);
        return plan.Extras.Count;
    }

    // Reads what a resource's manifest declares besides its stream folder (ResourceExtras).
    private static void AddResource(Plan plan, string manifestText, IReadOnlyCollection<string> files, Func<string, Func<Stream>> open)
    {
        var (kept, lines, leftOut) = ResourceExtras.Select(ResourceExtras.Parse(manifestText), files);
        plan.Ignored.AddRange(leftOut);
        if (lines.Count == 0)
            return;
        foreach (string file in kept.Where(IsSafeRelative))
            plan.Extras.Add((file, open(file)));
        plan.ExtraLines.AddRange(lines);
    }

    // The folder holding a resource's manifest: the folder itself, or the parent of a "stream" folder.
    private static void AddResourceFolder(Plan plan, string folder)
    {
        string root = folder;
        if (!ManifestIn(root, out string? manifest)
            && string.Equals(Path.GetFileName(folder), FiveMResource.StreamFolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { } parent)
        {
            root = parent;
            ManifestIn(root, out manifest);
        }
        if (manifest is null)
            return;
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
        AddResource(plan, File.ReadAllText(manifest), files, relative => () => File.OpenRead(Path.Combine(root, relative)));
    }

    private static bool ManifestIn(string folder, out string? manifest)
    {
        manifest = Directory.EnumerateFiles(folder).FirstOrDefault(f => ResourceExtras.IsManifest(Path.GetFileName(f)));
        return manifest is not null;
    }

    // Reads a file by path relative to a folder (the images of a .ytd.xml...).
    private static Func<string, byte[]?> Sibling(string directory) => relative =>
    {
        if (!IsSafeRelative(relative))
            return null;
        string sibling = Path.Combine(directory, relative);
        return File.Exists(sibling) ? File.ReadAllBytes(sibling) : null;
    };

    // Loose files first; a folder without any (a downloaded mod: readme + .oiv) takes the packages inside it.
    private static void AddFolder(Plan plan, string folder)
    {
        var (files, duplicates) = FindFiles(folder, withXmlExports: true);
        foreach (var file in files)
            plan.Add(Path.GetFileName(file), Path.GetRelativePath(folder, file), () => File.OpenRead(file), Sibling(Path.GetDirectoryName(file)!));
        plan.Duplicates.AddRange(duplicates);
        if (files.Count > 0)
        {
            AddResourceFolder(plan, folder);
            return;
        }
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
        // A zipped resource: the shallowest manifest gives its folder.
        if (entries.Keys.Where(k => ResourceExtras.IsManifest(Path.GetFileName(k))).OrderBy(k => k.Count(c => c == '/')).FirstOrDefault() is { } manifestKey)
        {
            string root = manifestKey[..^Path.GetFileName(manifestKey).Length];
            string manifestText;
            using (var reader = new StreamReader(entries[manifestKey].Open()))
                manifestText = reader.ReadToEnd();
            var inside = entries.Keys.Where(k => k.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !k.EndsWith('/'))
                .Select(k => k[root.Length..]).ToList();
            AddResource(plan, manifestText, inside, relative => entries[OivPackage.NormalizePath(root + relative)].Open);
        }
        foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0 && IsImported(e.Name)).OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            string directory = OivPackage.NormalizePath(entry.FullName)[..^entry.Name.Length];
            plan.Add(entry.Name, $"{archive} : {entry.FullName}", entry.Open, relative =>
            {
                if (!IsSafeRelative(relative) || !entries.TryGetValue(OivPackage.NormalizePath(directory + relative), out var sibling))
                    return null;
                using var input = sibling.Open();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                return bytes.ToArray();
            });
        }
    }

    private static bool IsSafeRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");

    private static OivPackage ReadPackage(Plan plan, string xml)
    {
        var package = OivPackage.Parse(xml, IsStreamed);
        plan.Name ??= package.Name;
        plan.Author ??= package.Author;
        plan.Ignored.AddRange(package.Ignored);
        return package;
    }
}
