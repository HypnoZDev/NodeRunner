using NodeRunner.App.Ui;
using NodeRunner.App.World;
using NodeRunner.Editor;

namespace NodeRunner.App;

internal sealed unsafe partial class MainForm
{
    private void HandleUiRequests()
    {
        _state.Backup.Update(_clock.Elapsed.TotalSeconds);
        if (_state.Backup.Found is { } found)
            OfferRecovery(found);
        WindowChrome.IsMaximized = WindowState == FormWindowState.Maximized;
        if (WindowChrome.MinimizeRequested)
        {
            WindowChrome.MinimizeRequested = false;
            WindowState = FormWindowState.Minimized;
        }
        if (WindowChrome.CloseRequested)
        {
            WindowChrome.CloseRequested = false;
            _ui.ExitRequested = true;
        }

        if (_ui.ExitRequested)
        {
            _ui.ExitRequested = false;
            Close();
            return;
        }

        HandleProjectRequests();

        if (_state.DialogRequest is { } dialog)
        {
            _state.DialogRequest = null;
            dialog(this);
        }

        if (_ui.ArchiveExtractRequested is { } extract)
        {
            _ui.ArchiveExtractRequested = null;
            ExtractArchiveItem(extract);
        }

        if (_ui.ChooseExportFolderRequested)
        {
            _ui.ChooseExportFolderRequested = false;
            ChooseExportFolder();
        }

        if (_ui.SaveSoloPackRequested)
        {
            _ui.SaveSoloPackRequested = false;
            SaveSoloPack();
        }

        if (_ui.SaveRequested)
        {
            _ui.SaveRequested = false;
            SaveMaps();
        }

        if (_ui.BrowseGameFolderRequested)
        {
            _ui.BrowseGameFolderRequested = false;
            BrowseForGameFolder();
        }
    }

    // Saves the modified map files into the project.
    private bool SaveMaps()
    {
        if (_state.PendingSaveCount == 0)
            return true;
        if (_state.Online.IsGuest)
        {
            _state.Notify(L("Session Online : c'est l'hôte qui enregistre le projet."), warning: true);
            return false;
        }
        if (_state.Project is null)
        {
            _ui.ProjectWindow.Open(saveAfter: true);
            return false;
        }
        _state.SaveMaps();
        if (_state.LastSave is { Success: false } failed)
        {
            MessageBox.Show(this, failed.Text, L("Enregistrement"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    private void HandleProjectRequests()
    {
        if (_ui.ChooseProjectsFolderRequested)
        {
            _ui.ChooseProjectsFolderRequested = false;
            using var dialog = new FolderBrowserDialog
            {
                Description = L("Dossier où créer les projets (chaque projet aura son sous-dossier)."),
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(_state.ProjectsFolder) ? _state.ProjectsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _state.Settings.ProjectsFolder = dialog.SelectedPath;
                _state.SaveSettings();
            }
        }

        if (_ui.ProjectWindow.Requested is { } create)
        {
            _ui.ProjectWindow.Requested = null;
            CreateProject(create.Folder, create.Name, create.SaveAfter, create.ExitAfter);
        }

        if (_ui.ProjectWindow.OpenRequested is { } existing)
        {
            _ui.ProjectWindow.OpenRequested = null;
            OpenProject(existing);
        }

        if (_ui.RecentProjectRequested is { } recent)
        {
            _ui.RecentProjectRequested = null;
            OpenProject(recent);
        }

        if (_ui.OpenProjectRequested)
        {
            _ui.OpenProjectRequested = false;
            BrowseForProject();
        }

        if (_ui.ImportMappingRequested)
        {
            _ui.ImportMappingRequested = false;
            BrowseForMapping();
        }

        if (_ui.ImportPackageRequested)
        {
            _ui.ImportPackageRequested = false;
            BrowseForPackage();
        }

        if (_ui.AddFilesRequested)
        {
            _ui.AddFilesRequested = false;
            BrowseForProjectFiles();
        }

        if (_ui.AddFolderRequested)
        {
            _ui.AddFolderRequested = false;
            BrowseForProjectFolder();
        }

        if (_ui.LinkFolderRequested)
        {
            _ui.LinkFolderRequested = false;
            BrowseForWatchedFolder();
        }

        if (_ui.UnlinkFolderRequested)
        {
            _ui.UnlinkFolderRequested = false;
            SetWatchedFolder(null);
        }

        AddWatchedChanges();

        if (_state.LibraryRequest is var library and not LibraryAction.None)
        {
            _state.LibraryRequest = LibraryAction.None;
            HandleLibraryRequest(library);
        }

        if (_droppedPaths is { } dropped)
        {
            _droppedPaths = null;
            Activate();
            ImportDropped(dropped);
        }

        if (_ui.CloseProjectRequested)
        {
            _ui.CloseProjectRequested = false;
            if (ConfirmLeaveChanges())
                _state.CloseProject();
        }

        if (_ui.AddViewProjectRequested is { } more)
        {
            _ui.AddViewProjectRequested = null;
            AddProjectToView(more);
        }

        if (_ui.RemoveViewProjectRequested is { } removed)
        {
            _ui.RemoveViewProjectRequested = null;
            if (ConfirmLeaveChanges())
                _state.RemoveProjectFromView(removed);
        }

        if (_ui.ActivateProjectRequested is { } activated)
        {
            _ui.ActivateProjectRequested = null;
            if (ConfirmLeaveChanges())
                _state.MakeProjectActive(activated);
        }
    }

    // Shows another project with the open one; "" to pick its folder.
    private void AddProjectToView(string folder)
    {
        if (folder.Length == 0)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = L("Choisis le dossier du projet NodeRunner à ouvrir en plus (celui qui contient projet.json)."),
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(_state.ProjectsFolder) ? _state.ProjectsFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            folder = dialog.SelectedPath;
            if (!MapProject.Exists(folder))
            {
                MessageBox.Show(this, L($"« {Path.GetFileName(folder)} » n'est pas un projet NodeRunner (pas de projet.json).\nOuvrez-le d'abord seul (Fichier > Ouvrir un projet) pour en faire un projet."),
                    L("Ouvrir un autre projet en plus"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (!ConfirmLeaveChanges())
            return;
        if (_state.AddProjectToView(folder) is { } error)
            MessageBox.Show(this, L($"Le projet n'a pas pu être ouvert en plus :\n{error}"), L("Ouvrir un autre projet en plus"), MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void CreateProject(string folder, string name, bool saveAfter, bool exitAfter)
    {
        // Without a project, the changes in progress go into the new one; otherwise the map is loaded again.
        bool reloads = _state.Project is not null || MapProject.HasMapFiles(folder);
        if (reloads && !ConfirmLeaveChanges())
            return;
        if (_state.CreateProject(folder, name) is { } error)
        {
            MessageBox.Show(this, L($"Le projet n'a pas pu être créé :\n{error}"), L("Nouveau projet"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (saveAfter && !reloads && !SaveMaps())
            return;
        if (exitAfter)
            Close();
    }

    private void OpenProject(string folder)
    {
        if (string.Equals(Path.GetFullPath(folder), _state.Project?.Folder, StringComparison.OrdinalIgnoreCase) || !ConfirmLeaveChanges())
            return;
        if (_state.OpenProject(folder) is { } error)
        {
            if (!MapProject.Exists(folder))
                _state.ForgetRecentProject(folder);
            MessageBox.Show(this, L($"Le projet n'a pas pu être ouvert :\n{error}"), L("Ouvrir un projet"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Before leaving the map shown (other project, closing it): unsaved changes are saved, left, or the action is
    // cancelled.
    private bool ConfirmLeaveChanges()
    {
        if (!_state.History.HasUnsavedChanges || _state.PendingSaveCount == 0)
            return true;
        string count = _state.PendingSaveText;
        if (_state.Project is not { } project)
        {
            return MessageBox.Show(this,
                L($"Des modifications ne sont dans aucun projet ({count}) : elles seront perdues.\nContinuer ?"),
                Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }
        var answer = MessageBox.Show(this,
            L($"Des modifications du projet « {project.Name} » ne sont pas enregistrées ({count}).\nLes enregistrer d'abord ?"),
            Program.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return answer == DialogResult.No || (answer == DialogResult.Yes && SaveMaps());
    }

    private void ExtractArchiveItem(ArchiveTree.Node node)
    {
        if (node.IsFile)
        {
            using var save = new SaveFileDialog
            {
                Title = L($"Extraire {node.Name}"),
                FileName = node.Name,
                Filter = L("Tous les fichiers (*.*)|*.*"),
                OverwritePrompt = true,
            };
            if (save.ShowDialog(this) == DialogResult.OK)
                _ui.ExtractArchiveItem(node, save.FileName);
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = L($"Dossier où extraire « {node.Name} » ({node.FileCount} fichiers, un dossier de ce nom y sera créé)."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ui.ExtractArchiveItem(node, dialog.SelectedPath);
    }

    private void SaveSoloPack()
    {
        using var save = new SaveFileDialog
        {
            Title = L("Enregistrer le pack solo"),
            FileName = SoloPack.ArchiveName,
            Filter = L("Archive RPF (*.rpf)|*.rpf"),
            InitialDirectory = _state.ExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            OverwritePrompt = true,
        };
        if (save.ShowDialog(this) == DialogResult.OK)
            _ui.ExportSoloPack(save.FileName);
    }

    private void ChooseExportFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = L("Dossier où créer la ressource FiveM (par exemple le dossier « resources » de ton serveur)."),
            UseDescriptionForTitle = true,
            InitialDirectory = _state.ExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _state.ExportFolder = dialog.SelectedPath;
    }
}
