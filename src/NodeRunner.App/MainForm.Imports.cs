using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
    // Picks a project folder; a folder without projet.json can become a project (its .ymap files go to "stream").
    private void BrowseForProject()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = L("Choisis le dossier d'un projet NodeRunner (celui qui contient projet.json)."),
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_state.ProjectsFolder) ? _state.ProjectsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string folder = dialog.SelectedPath;
        if (MapProject.Exists(folder))
        {
            OpenProject(folder);
            return;
        }
        var answer = MessageBox.Show(this,
            L($"« {Path.GetFileName(folder)} » n'est pas un projet NodeRunner.\nEn faire un projet ? Les .ymap qu'il contient seront rangés dans son dossier « stream »."),
            L("Ouvrir un projet"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            CreateProject(folder, Path.GetFileName(folder), saveAfter: false, exitAfter: false);
    }

    private void BrowseForMapping()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = L("Choisis le dossier du mapping : une ressource FiveM (avec fxmanifest.lua) ou un dossier stream."),
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmLeaveChanges())
            return;
        ImportMapping(dialog.SelectedPath);
    }

    private void BrowseForPackage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = L("Importer un mod OpenIV (.oiv) ou une archive .zip"),
            Filter = L("Mods et archives (*.oiv;*.zip)|*.oiv;*.zip|Tous les fichiers (*.*)|*.*"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmLeaveChanges())
            return;
        ImportMapping(dialog.FileName);
    }

    // Dropped with a project open: added to it, or imported as a new project. A pack of addon props can go to the
    // library instead.
    private void ImportDropped(string[] paths)
    {
        if (paths.Length == 1 && NodeRunner.Editor.PropLibrary.LooksLikePack(paths[0]))
        {
            var library = MessageBox.Show(this,
                L($"« {Path.GetFileName(paths[0].TrimEnd('\\', '/'))} » ressemble à un pack de props addons.\n\nOui : l'ajouter à la bibliothèque (disponible dans tous les projets).\nNon : l'importer comme un mapping (projet)."),
                L("Bibliothèque de props"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (library == DialogResult.Yes)
            {
                AddLibraryPack(paths[0]);
                return;
            }
            if (library != DialogResult.No)
                return;
        }
        if (_state.Project is { } project)
        {
            string what = paths.Length == 1 ? $"« {Path.GetFileName(paths[0].TrimEnd('\\', '/'))} »" : L($"{paths.Length} éléments");
            var answer = MessageBox.Show(this,
                L($"Ajouter {what} au projet « {project.Name} » ?\n\nOui : ajouter au projet ouvert (les fichiers du même nom sont remplacés).\nNon : créer un nouveau projet."),
                L("Importer"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                AddToProject(paths);
                return;
            }
            if (answer != DialogResult.No)
                return;
        }
        if (ConfirmLeaveChanges())
            ImportMapping(paths);
    }

    private const string ProjectFilesFilter = "*.ymap;*.ytyp;*.ybn;*.ydr;*.ydd;*.yft;*.ytd;*.ynd;*.ynv;*.ymf;*.ycd;*.ymt;*.xml;*.zip;*.oiv";

    private void BrowseForProjectFiles()
    {
        if (_state.Project is null)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = L("Ajouter des fichiers au projet"),
            Filter = L($"Fichiers de carte, exports XML (Sollumz, CodeWalker), archives|{ProjectFilesFilter}|Tous les fichiers (*.*)|*.*"),
            Multiselect = true,
            InitialDirectory = _state.Project.LastAddFolder is { } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        AddToProject(dialog.FileNames);
    }

    private void BrowseForProjectFolder()
    {
        if (_state.Project is null)
            return;
        using var dialog = new FolderBrowserDialog
        {
            Description = L("Choisis le dossier à ajouter au projet (export de Sollumz, dossier stream...) : ses sous-dossiers sont compris."),
            UseDescriptionForTitle = true,
            InitialDirectory = _state.Project.LastAddFolder is { } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        AddToProject([dialog.SelectedPath]);
    }

    // Links a folder (export folder of Sollumz...) to the project: its files are added now, then again whenever they change.
    private void BrowseForWatchedFolder()
    {
        if (_state.Project is not { } project)
            return;
        using var dialog = new FolderBrowserDialog
        {
            Description = L("Choisis le dossier d'export de Sollumz (ou de Blender) à lier au projet : ses fichiers seront ajoutés au projet à chaque nouvel export."),
            UseDescriptionForTitle = true,
            InitialDirectory = (project.WatchedFolder ?? project.LastAddFolder) is { } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string folder = Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\', '/');
        string projectFolder = Path.GetFullPath(project.Folder).TrimEnd('\\', '/');
        // NodeRunner writes into the project: watching it would add its own files again and again.
        if (string.Equals(folder, projectFolder, StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith(projectFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || projectFolder.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, L("Ce dossier contient le projet ou en fait partie : choisis le dossier où Sollumz exporte, en dehors du projet."),
                L("Lier un dossier Sollumz"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!SetWatchedFolder(folder))
            return;
        AddToProject([folder], watched: true);
    }

    private bool SetWatchedFolder(string? folder)
    {
        if (_state.Project is not { } project)
            return false;
        project.WatchedFolder = folder;
        try
        {
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L($"Le projet n'a pas pu être enregistré :\n{ex.Message}"), L("Lier un dossier Sollumz"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (folder is null)
            _state.Notify(L("Dossier Sollumz délié : ses nouveaux exports ne sont plus ajoutés au projet."));
        return true;
    }

    // New exports in the folder linked to the project: added to it, then the map is loaded again.
    private void AddWatchedChanges()
    {
        string? folder = _state.Online.IsGuest ? null : _state.Project?.WatchedFolder;
        _folderWatch.Watch(folder);
        if (folder is null || _folderWatch.TakeChanges() is not { } changed)
            return;
        var sources = NodeRunner.Editor.MappingImport.WatchedSources(changed, folder);
        if (sources.Count > 0)
            AddToProject(sources, watched: true);
    }

    private void HandleLibraryRequest(LibraryAction action)
    {
        string library = _state.LibraryFolder;
        string start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        switch (action)
        {
            case LibraryAction.AddFolder:
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = L("Choisis le pack de props à ajouter à la bibliothèque (dossier d'une ressource FiveM ou dossier stream)."),
                    UseDescriptionForTitle = true,
                    InitialDirectory = start,
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    AddLibraryPack(dialog.SelectedPath);
                break;
            }
            case LibraryAction.AddZip:
            {
                using var dialog = new OpenFileDialog
                {
                    Title = L("Ajouter un pack de props (.zip) à la bibliothèque"),
                    Filter = L("Archives .zip (*.zip)|*.zip"),
                    InitialDirectory = start,
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    AddLibraryPack(dialog.FileName);
                break;
            }
            case LibraryAction.ChooseFolder:
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = L("Dossier de la bibliothèque de props (un sous-dossier par pack)."),
                    UseDescriptionForTitle = true,
                    InitialDirectory = Directory.Exists(library) ? library : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    ShowNewFolderButton = true,
                };
                if (dialog.ShowDialog(this) == DialogResult.OK && ConfirmLeaveChanges())
                    ReloadLibrary(dialog.SelectedPath);
                break;
            }
            case LibraryAction.Default:
                if (ConfirmLeaveChanges())
                    ReloadLibrary(null);
                break;
            case LibraryAction.Reload:
                if (ConfirmLeaveChanges())
                    ReloadLibrary(_state.Settings.LibraryFolder);
                break;
        }
    }

    // Copies a pack into the prop library, then loads the map again so that its props can be placed.
    private void AddLibraryPack(string source)
    {
        string library = _state.LibraryFolder;
        string name = NodeRunner.Editor.PropLibrary.PackName(source);
        if (NodeRunner.Editor.PropLibrary.HasPack(source, library)
            && MessageBox.Show(this, L($"La bibliothèque a déjà un pack « {name} ». Le remplacer ?"), L("Bibliothèque de props"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        if (!ConfirmLeaveChanges())
            return;
        try
        {
            NodeRunner.Editor.PropLibrary.AddPack(source, library);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, L($"Le pack n'a pas pu être ajouté :\n{ex.Message}"), L("Bibliothèque de props"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (ReloadLibrary(_state.Settings.LibraryFolder))
        {
            _ui.ShowLibrary();
            _state.Notify(L($"Pack « {name} » ajouté à la bibliothèque, carte rechargée (Visionneuse > Bibliothèque)."));
        }
    }

    private bool ReloadLibrary(string? folder)
    {
        if (_state.ReloadLibrary(folder) is not { } error)
            return true;
        MessageBox.Show(this, error, L("Bibliothèque de props"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // Converts and copies the files into the open project's stream folder, then loads its map again.
    // watched: from the folder linked to the project (the folder offered next time is kept).
    private void AddToProject(IReadOnlyList<string> sources, bool watched = false)
    {
        if (_state.Project is not { } project || !ConfirmLeaveChanges())
            return;
        NodeRunner.Editor.ImportResult result;
        try
        {
            result = NodeRunner.Editor.MappingImport.AddToProject(sources, project, XmlConverters);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, L($"Les fichiers n'ont pas pu être ajoutés :\n{ex.Message}"), L("Ajouter au projet"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (!watched)
        {
            string first = Path.GetFullPath(sources[0].TrimEnd('\\', '/'));
            project.LastAddFolder = Directory.Exists(first) ? first : Path.GetDirectoryName(first);
            try
            {
                project.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only the folder offered next time is lost.
            }
        }
        if (result.Copied > 0 && _state.OpenProject(project.Folder, keepOthers: true) is { } error)
        {
            MessageBox.Show(this, L($"Le projet n'a pas pu être rechargé :\n{error}"), L("Ajouter au projet"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (result.Protected.Count > 0)
        {
            _ui.ShowProtectedMapping(result);
            return;
        }
        string replaced = result.Replaced == 0 ? "" : L($" (dont {result.Replaced} remplacé(s))");
        string converted = result.Converted == 0 ? "" : L($"\n\nDont {result.Converted} export(s) XML (Sollumz, CodeWalker) converti(s) en fichiers du jeu.");
        string skipped = result.Skipped.Count == 0 ? "" : L($"\n\n{result.Skipped.Count} fichier(s) en double ignoré(s) (même nom) :\n") + string.Join("\n", result.Skipped.Take(10));
        string ignored = result.Ignored.Count == 0 ? "" : L($"\n\n{result.Ignored.Count} élément(s) non repris :\n") + string.Join("\n", result.Ignored.Take(10));
        if (result.Copied > 0 && result.Ignored.Count == 0 && result.Skipped.Count == 0)
        {
            _state.Notify(watched
                ? L($"Dossier Sollumz : {result.Copied} fichier(s) ajouté(s) au projet{replaced}, carte rechargée.")
                : L($"{result.Copied} fichier(s) ajouté(s) au projet{replaced}, carte rechargée."));
            return;
        }
        MessageBox.Show(this, L($"{result.Copied} fichier(s) ajouté(s) au projet « {project.Name} »{replaced}.{converted}{skipped}{ignored}"),
            L("Ajouter au projet"), MessageBoxButtons.OK, result.Copied > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // XML exports the import turns into game files, by game extension.
    private Dictionary<string, Func<NodeRunner.Data.FileSystem.XmlExport, byte[]>> XmlConverters => new()
    {
        [".ybn"] = export => NodeRunner.Data.Native.Collisions.YbnXml.Convert(export.Text),
        [".ytd"] = NodeRunner.Data.Native.Textures.YtdXml.Convert,
        [".ydr"] = NodeRunner.Data.Native.Models.YdrXml.Convert,
        [".ytyp"] = export => NodeRunner.Data.Native.Maps.YtypXml.Convert(export, MetaSchema()),
        [".ymap"] = export => NodeRunner.Data.Native.Maps.YmapXml.Convert(export, MetaSchema()),
    };

    private (NodeRunner.Data.Native.Archives.RpfFileSystem Files, NodeRunner.Data.Native.Resources.MetaSchema Schema)? _metaSchema;

    // Descriptions of the game's Meta structures, for the .ytyp and .ymap exports: from its .ytyp files, then its .ymap files if
    // one is missing.
    private NodeRunner.Data.Native.Resources.MetaSchema MetaSchema()
    {
        if (_state.World.Files is not { } files)
            throw new InvalidDataException(L("les fichiers de GTA V doivent être chargés (ils décrivent le format des .ytyp et .ymap)"));
        if (_metaSchema is { } cached && cached.Files == files)
            return cached.Schema;
        IEnumerable<byte[]> Read(string extension) => files.EnumerateFiles(extension).Select(files.ReadFile).OfType<byte[]>();
        var schema = new NodeRunner.Data.Native.Resources.MetaSchema(Read(".ytyp"), Read(".ymap"));
        _metaSchema = (files, schema);
        return schema;
    }

    private void ImportMapping(string source) => ImportMapping([source]);

    private void ImportMapping(IReadOnlyList<string> sources)
    {
        NodeRunner.Editor.ImportResult result;
        try
        {
            result = NodeRunner.Editor.MappingImport.Import(sources, _state.ProjectsFolder, _state.Settings.MapVersion, _state.Settings.LastDlcPack,
                XmlConverters);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, L($"Le mapping n'a pas pu être importé :\n{ex.Message}"), L("Importer un mapping"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_state.OpenProject(result.Project.Folder) is { } error)
        {
            MessageBox.Show(this, L($"Le projet importé n'a pas pu être ouvert :\n{error}"), L("Importer un mapping"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _ui.ShowProjectPanel();
        if (result.Protected.Count > 0)
        {
            _ui.ShowProtectedMapping(result);
            return;
        }
        string author = result.Author is { } by ? L($" (par {by})") : "";
        string skipped = result.Skipped.Count == 0 ? "" : L($"\n\n{result.Skipped.Count} fichier(s) en double ignoré(s) (même nom) :\n") + string.Join("\n", result.Skipped.Take(10));
        string ignored = result.Ignored.Count == 0 ? "" : L($"\n\n{result.Ignored.Count} élément(s) non repris :\n") + string.Join("\n", result.Ignored.Take(10));
        string converted = result.Converted == 0 ? "" : L($"\n\nDont {result.Converted} export(s) XML (Sollumz, CodeWalker) converti(s) en fichiers du jeu.");
        MessageBox.Show(this,
            L($"{result.Copied} fichier(s) copié(s) dans le projet « {result.Project.Name} »{author} :\n{result.Project.Folder}\n\nLes fichiers qui portent le nom d'un fichier du jeu le remplacent (comme dans FiveM).\nLe panneau Projet liste les objets ajoutés, déplacés et supprimés.{converted}{skipped}{ignored}"),
            L("Importer un mapping"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
