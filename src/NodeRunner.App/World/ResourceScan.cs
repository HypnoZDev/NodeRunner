using System.Text;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Maps;
using NodeRunner.Data.Native.Maps;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Editor;

namespace NodeRunner.App.World;

// The dependency check of a folder of streamed files (a project after an import, an exported resource), against the
// loaded game, and its report as text.
internal static class ResourceScan
{
    public sealed record Result(List<DependencyCheck.Issue> Issues, int Files)
    {
        public int Errors => Issues.Count(i => DependencyCheck.IsError(i.Kind));
        public int Warnings => Issues.Count - Errors;
    }

    // A project's stream folder: what it lacks may also come from the prop library (copied at export).
    public static Result? Project(WorldLoader world, string streamFolder) =>
        Directory.Exists(streamFolder) ? Check(world, Directory.GetFiles(streamFolder), withLibrary: true) : null;

    // An exported (or any) FiveM resource: its manifest and every file of its "stream" folders, at any depth.
    public static Result? Resource(WorldLoader world, string resourceFolder)
    {
        if (!Directory.Exists(resourceFolder))
            return null;
        var all = Directory.GetFiles(resourceFolder, "*", SearchOption.AllDirectories);
        string Relative(string f) => Path.GetRelativePath(resourceFolder, f).Replace('\\', '/');
        var streamed = all.Where(f => Relative(f).Split('/').SkipLast(1).Contains(FiveMResource.StreamFolderName, StringComparer.OrdinalIgnoreCase)).ToList();
        var result = Check(world, streamed, withLibrary: false);
        if (result is null)
            return null;
        string? manifest = all.FirstOrDefault(f => Path.GetDirectoryName(f) == resourceFolder.TrimEnd('\\', '/') && ResourceExtras.IsManifest(Path.GetFileName(f)));
        string? lua = null;
        try
        {
            lua = manifest is null ? null : File.ReadAllText(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported as missing.
        }
        bool maps = streamed.Any(f => f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase));
        result.Issues.InsertRange(0, DependencyCheck.CheckManifest(manifest is null ? null : Path.GetFileName(manifest), lua,
            all.Select(Relative).ToList(), maps));
        return result;
    }

    private static Result? Check(WorldLoader world, IReadOnlyList<string> files, bool withLibrary)
    {
        if (world.Maps is not { } library || world.Models is not { } models || world.Textures is not { } textures)
            return null;
        bool Game(string? path) => path is not null && !GameContent.IsLooseFile(path) && (withLibrary || !GameContent.IsLibraryFile(path));
        var game = new DependencyCheck.GameFiles(
            h => Game(library.GetArchetypeFile(h)),
            h => Game(models.DrawablePath(h)),
            h => Game(models.DictionaryPath(h)),
            h => Game(models.FragmentPath(h)),
            h => Game(textures.Locate(h)));

        var types = new List<(string, MapTypes)>();
        var maps = new List<(string, MapData)>();
        var unreadable = new List<string>();
        var typeReader = new YtypReader();
        var mapReader = new YmapReader();
        foreach (string file in files)
        {
            bool isType = file.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase);
            if (!isType && !file.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var content = ResourceFile.Read(File.ReadAllBytes(file)).Content;
                if (isType)
                    types.Add((file, typeReader.Read(content)));
                else
                    maps.Add((file, mapReader.Read(content)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                unreadable.Add(file);
            }
        }
        var issues = DependencyCheck.Check(files, types, maps, unreadable, game, Memory);
        return new Result(issues, files.Count);
    }

    private static long? Memory(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var head = new byte[16];
            if (stream.Read(head) < head.Length)
                return null;
            return ResourceFile.MemorySize(ResourceFile.ReadHeader(head));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public static string Text(DependencyCheck.Issue issue, HashNames names) => issue.Kind switch
    {
        DependencyCheck.Kind.DuplicateFile => L($"présent {issue.Value} fois dans les dossiers stream : FiveM n'en charge qu'un"),
        DependencyCheck.Kind.Unreadable => L("illisible (fichier abîmé, ou chiffré par FiveM Asset Escrow)"),
        DependencyCheck.Kind.MissingModel => L($"le type {names.Describe(issue.Hash)} n'a pas de modèle (.ydr / .ydd / .yft), ni ici ni dans le jeu : invisible en jeu"),
        DependencyCheck.Kind.UnknownArchetype => L($"utilise le modèle {names.Describe(issue.Hash)}, décrit dans aucun .ytyp ici ni dans le jeu : l'objet n'apparaîtra pas"),
        DependencyCheck.Kind.NoManifest => L("aucun fxmanifest.lua : FiveM ne démarrera pas la ressource"),
        DependencyCheck.Kind.NoFxVersion => L("pas de ligne fx_version : FiveM refuse la ressource"),
        DependencyCheck.Kind.MissingDataFile => L("cité par une ligne data_file du manifeste, mais absent de la ressource"),
        DependencyCheck.Kind.MissingTextures => L($"le type {names.Describe((uint)issue.Value)} demande les textures {names.Describe(issue.Hash)}.ytd, absentes ici et du jeu (textures blanches ou manquantes)"),
        DependencyCheck.Kind.ModelWithoutArchetype => L("modèle décrit dans aucun .ytyp : il ne peut pas être placé (sauf par un autre .ytyp du serveur)"),
        DependencyCheck.Kind.Oversized => L($"{issue.Value / (1024.0 * 1024):0.0} Mo une fois chargé : FiveM l'annonce comme trop gros (« oversized asset »), il peut manquer chez certains joueurs"),
        DependencyCheck.Kind.NotStreamed => L("type de fichier que FiveM ne charge pas depuis stream"),
        DependencyCheck.Kind.MissingListedFile => L("cité par le manifeste (files / scripts), mais absent de la ressource"),
        DependencyCheck.Kind.NotAMap => L("pas de ligne this_is_a_map 'yes' alors que la ressource contient des YMAP"),
        _ => issue.Kind.ToString(),
    };

    // The report as Markdown: what was checked, then the errors and the warnings, file by file.
    public static string Report(string title, string subject, Result result, IEnumerable<string> notConverted, HashNames names)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(title).Append("\n\n");
        text.Append(subject).Append("\n\n");
        text.Append(L($"{DateTime.Now:yyyy-MM-dd HH:mm} · {result.Files} fichier(s) vérifié(s) · {result.Errors} erreur(s) · {result.Warnings} avertissement(s)")).Append("\n\n");
        var failed = notConverted.ToList();
        if (failed.Count > 0)
        {
            text.Append("## ").Append(L("Non repris")).Append("\n\n");
            foreach (string line in failed)
                text.Append("- ").Append(line).Append('\n');
            text.Append('\n');
        }
        void Section(string heading, bool errors)
        {
            var lines = result.Issues.Where(i => DependencyCheck.IsError(i.Kind) == errors).ToList();
            if (lines.Count == 0)
                return;
            text.Append("## ").Append(heading).Append("\n\n");
            foreach (var issue in lines)
                text.Append("- `").Append(issue.File).Append("` : ").Append(Text(issue, names)).Append('\n');
            text.Append('\n');
        }
        Section(L("Erreurs"), true);
        Section(L("Avertissements"), false);
        if (result.Issues.Count == 0 && failed.Count == 0)
            text.Append(L("Aucun problème trouvé.")).Append('\n');
        return text.ToString();
    }

    // Writes the report; null if it could not be written.
    public static string? Write(string folder, string fileName, string report)
    {
        try
        {
            string path = Path.Combine(folder, fileName);
            File.WriteAllText(path, report);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
