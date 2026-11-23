using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Collisions;
using NodeRunner.Data.Native.Models;
using NodeRunner.Data.Native.Textures;

namespace NodeRunner.App;

// Export of a game file as the XML read by Sollumz (Blender) and CodeWalker, from the archive explorer or the viewer.
internal sealed partial class AppState
{
    private static readonly string[] XmlExtensions = [".ydr", ".ydd", ".yft", ".ytd", ".ybn"];

    private Task<string>? _xmlExport;
    private string? _xmlExportFolder;
    private (IGameFileSystem Files, ShaderNameCatalog Names)? _shaderNames;

    public bool IsExportingXml => _xmlExport is not null;

    public bool CanExportXml(string path) =>
        !IsOtherGame && World.Files is not null && XmlExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // Asks for a folder, then writes "name.ydr.xml" and its images there in the background.
    public void RequestXmlExport(string path)
    {
        if (!CanExportXml(path) || _xmlExport is not null)
            return;
        DialogRequest = owner =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = L($"Dossier où écrire {Path.GetFileName(path)}.xml (et ses textures en .dds), à ouvrir dans Blender avec Sollumz."),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = _xmlExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return;
            _xmlExportFolder = dialog.SelectedPath;
            var files = World.ModelFiles is { } models && models.FileExists(path) ? models : World.Files!;
            var game = World.Files!;
            var names = World.Names;
            string folder = dialog.SelectedPath;
            _xmlExport = Task.Run(() => ExportXml(files, game, names, path, folder));
        };
    }

    // Called every frame: tells how the export ended.
    public void PollXmlExport()
    {
        if (_xmlExport is not { IsCompleted: true } task)
            return;
        _xmlExport = null;
        if (task.IsCompletedSuccessfully)
            Notify(task.Result);
        else
            Notify(L($"Export XML impossible : {task.Exception?.GetBaseException().Message}"), warning: true);
    }

    private string ExportXml(IGameFileSystem files, IGameFileSystem game, Data.Hashing.HashNames? models, string path, string folder)
    {
        byte[] data = files.ReadFile(path) ?? throw new InvalidDataException(L("Fichier illisible."));
        var header = files.GetResourceHeader(path) ?? throw new InvalidDataException(L("Ce fichier n'est pas une ressource du jeu."));
        string name = Path.GetFileName(path);
        XmlConversion conversion = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ytd" => YtdXml.Export(name, data, header),
            ".ybn" => YbnXml.Export(name, data, header),
            ".ydr" => YdrXml.Export(name, data, header, ShaderNames(game)),
            ".ydd" => YddXml.Export(name, data, header, ShaderNames(game), models is null ? null : models.TryGet),
            ".yft" => YftXml.Export(name, data, header, ShaderNames(game)),
            _ => throw new InvalidDataException(L("Type de fichier non pris en charge.")),
        };
        File.WriteAllText(Path.Combine(folder, conversion.FileName), conversion.Text);
        foreach (var (relative, bytes) in conversion.Files)
        {
            string target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        return conversion.Files.Count == 0
            ? L($"{conversion.FileName} écrit dans {folder}")
            : L($"{conversion.FileName} et {conversion.Files.Count} texture(s) .dds écrits dans {folder}");
    }

    // Read once per game folder (a few seconds): the names are in the game's shader files.
    private ShaderNameCatalog ShaderNames(IGameFileSystem game)
    {
        lock (XmlExtensions)
        {
            if (_shaderNames is not { } cached || cached.Files != game)
                _shaderNames = cached = (game, ShaderNameCatalog.Read(game));
            return cached.Names;
        }
    }
}
