using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "Export as a FiveM resource" window: resource name, destination folder (a server's "resources" folder), list of the
// map files, the check before export (conflicts with other resources and projects, deleted scenery), then the line to
// add to server.cfg. The same window exports a single-player pack (dlc.rpf).
internal sealed class ExportWindow(AppState state, Action chooseFolder, Action chooseSoloPackFile)
{
    public const string Title = $"{Icons.Package}  Exporter en ressource FiveM###Export";
    public const string SoloTitle = $"{Icons.Package}  Exporter en pack solo###Export";

    private bool _open;
    private bool _solo;
    private string _name = FiveMResource.DefaultName;
    private (string Text, bool Success, string? Folder)? _result;

    // Single-player pack: installation running in the background, its progress (written by that thread), packs installed.
    private Task? _installing;
    private volatile string _installProgress = "";
    private bool _resultInstalled;
    private List<string>? _installed;
    private bool _modsOutdated;
    private SoloPackInstaller.Support _support = SoloPackInstaller.Support.NoGame;
    private double _supportCheckedAt;

    // Check before export, run in the background when the window opens and when the folder or the name changes.
    private Task<ExportChecker.Result>? _checking;
    private ExportChecker.Result? _check;
    private (string? Folder, string Name, int Maps, bool RemoveGround) _checkedFor;

    public void Open() => Open(solo: false);

    public void OpenSolo() => Open(solo: true);

    private void Open(bool solo)
    {
        if (state.Online.IsGuest)
        {
            state.Notify(L("Session Online : seul l'hôte exporte le projet."), warning: true);
            return;
        }
        _open = true;
        _solo = solo;
        _name = solo ? SoloPack.ToPackName(state.ExportResourceName) : state.ExportResourceName;
        if (solo)
            RefreshInstalled();
        _result = null;
        StartCheck();
    }

    private void StartCheck()
    {
        if (_checking is { IsCompleted: false })
            return;
        var input = ExportChecker.Prepare(state) with
        {
            ResourceName = FiveMResource.IsValidName(_name) ? _name : state.ExportResourceName,
            RemoveGround = _removeGround,
        };
        _checkedFor = (state.ExportFolder, _name, state.MapsToExport.Count, _removeGround);
        var world = state.World;
        _checking = Task.Run(() => ExportChecker.Run(input, world));
    }

    public void Draw()
    {
        if (!_open)
            return;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(520f, 0f), new Vector2(520f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(_solo ? L(SoloTitle) : L(Title), ref _open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
        if (!_open && (state.CollisionPreview is not null || state.OcclusionPreview is not null))
            UpdatePreview(); // Closed by its cross or the Close button.
    }

    private void DrawContent()
    {
        if (_solo)
            Widgets.Hint(L("Crée un pack pour GTA V solo (dlc.rpf) avec les mêmes fichiers que l'export FiveM : YMAP, ")
                + L("collisions, modèles importés... Les fichiers du jeu ne sont pas touchés."), wrap: true);
        else
            Widgets.Hint(L("Crée un dossier prêt pour le dossier « resources » d'un serveur FiveM : fxmanifest.lua ")
                + L("et les fichiers modifiés (YMAP, pathnodes) dans « stream ». Les fichiers du jeu ne sont pas touchés."), wrap: true);
        ImGui.Dummy(new Vector2(0, 4));

        var maps = state.MapsToExport;
        var pathAreas = state.PathAreasToSave;
        var scenarioRegions = state.ScenarioRegionsToSave;
        string? folder = state.ExportFolder;
        bool nameValid = _solo ? SoloPack.ToPackName(_name) == _name : FiveMResource.IsValidName(_name);
        string? target = folder is not null && nameValid ? Path.Combine(folder, _name) : null;
        var targetState = target is not null && !_solo ? ResourceExporter.Inspect(target) : ResourceExporter.TargetState.New;

        if (Widgets.BeginProperties("##export", 0.30f))
        {
            if (_solo)
                Widgets.Property(L("Nom du pack"), L("Nom du dossier du pack dans dlcpacks : minuscules, chiffres et _"));
            else
                Widgets.Property(L("Nom de la ressource"), L("Nom utilisé dans server.cfg (ensure ...) : minuscules, chiffres, _ et -"));
            if (ImGui.InputText("##resource_name", ref _name, 64))
                _result = null;
            // Fixed when the field is left: "Ma carte" -> "ma_carte".
            if (ImGui.IsItemDeactivatedAfterEdit() && !nameValid)
                _name = _solo ? SoloPack.ToPackName(_name) : FiveMResource.ToValidName(_name);

            if (_solo)
                DrawSoloTarget(nameValid);
            else
                DrawFolder(folder, target, targetState, nameValid);

            Widgets.Property("YMAP", L("Les YMAP modifiés pendant cette session et ceux déjà enregistrés"));
            ImGui.AlignTextToFramePadding();
            int editedCollisions = state.CollisionEdits.Files.Count;
            int editedOccluders = state.OccluderEdits.StreamFileNames.Count();
            int editedInteriors = state.InteriorFilesToExport.Count;
            int projectFiles = state.ProjectFileCount;
            ImGui.TextUnformatted(maps.Count == 0 ? pathAreas.Count == 0 && scenarioRegions.Count == 0 && editedCollisions == 0 && editedOccluders == 0 && editedInteriors == 0 && projectFiles == 0 ? L("aucune modification à exporter") : "aucun" : $"{maps.Count} fichier(s)");
            if (projectFiles > 0)
            {
                Widgets.Property(L("Fichiers du projet"), L("Fichiers du dossier stream du projet (modèles, textures, collisions d'un mapping ou d'un mod importé), recopiés tels quels. Un fichier modifié dans NodeRunner est écrit à leur place."));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(L($"{projectFiles} fichier(s)"));
            }
            if (editedInteriors > 0)
            {
                Widgets.Property(L("Intérieurs"), L("Fichiers .ytyp des intérieurs (MLO) dont des objets ont été déplacés"));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"{editedInteriors} YTYP");
            }
            if (editedOccluders > 0)
            {
                Widgets.Property(L("Occlusion"), L("YMAP d'occlusion modifiés à la main (boîtes déplacées, créées ou supprimées)"));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(L($"{editedOccluders} YMAP d'occlusion"));
            }
            if (editedCollisions > 0)
            {
                Widgets.Property(L("Collisions"), L("Fichiers .ybn modifiés à la main (outil Collisions)"));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(L($"{editedCollisions} fichier(s) .ybn modifié(s)"));
            }
            if (pathAreas.Count > 0)
            {
                Widgets.Property(L("Pathnodes"), L("Fichiers du réseau routier (nodesN.ynd) dont des nœuds ont été déplacés"));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Join(", ", pathAreas.Select(Data.Paths.PathNetwork.FileName)));
            }
            if (scenarioRegions.Count > 0)
            {
                Widgets.Property(L("Scénarios"), L("Fichiers de régions de scénarios (.ymt) dont des points ont été modifiés"));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Join(", ", scenarioRegions.Select(r => r.Name + ".ymt")));
            }
            Widgets.EndProperties();
        }

        if (maps.Count > 0)
            DrawMapList(maps);
        if (maps.Count > 0)
            DrawCheck(maps.Count);

        if (_solo && _support is SoloPackInstaller.Support.NoModsLoader or SoloPackInstaller.Support.NoAsiLoader)
            DrawModsAssistant();

        ImGui.Dummy(new Vector2(0, 4));
        bool hasChanges = maps.Count > 0 || pathAreas.Count > 0 || scenarioRegions.Count > 0 || state.InteriorFilesToExport.Count > 0 || state.CollisionEdits.Files.Count > 0 || !state.OccluderEdits.IsEmpty || state.ProjectFileCount > 0;
        if (_solo)
        {
            FinishInstall();
            bool busy = _installing is not null;
            ImGui.BeginDisabled(!hasChanges || !nameValid || busy || _support != SoloPackInstaller.Support.Ready);
            if (Widgets.PrimaryButton(L($"{Icons.ArrowDownToLine}  Installer dans le jeu"), 190f))
                Install(maps, pathAreas);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!hasChanges || !nameValid || busy);
            if (Widgets.Button(L($"{Icons.Save}  Enregistrer le dlc.rpf..."), L("Enregistrer le pack ailleurs, pour l'installer soi-même ou le partager")))
            {
                _result = null;
                chooseSoloPackFile();
            }
            ImGui.EndDisabled();
        }
        else
        {
            bool canExport = hasChanges && target is not null && targetState != ResourceExporter.TargetState.Foreign;
            ImGui.BeginDisabled(!canExport);
            if (Widgets.PrimaryButton(L($"{Icons.Package}  Exporter"), 120f) && target is not null)
                Export(maps, pathAreas, target);
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        if (Widgets.Button(L("Fermer"), width: 90f))
            _open = false;

        if (_solo && _installing is not null)
        {
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.TextColored(UiTheme.TextMuted, $"{Icons.LoaderCircle}  {_installProgress}");
        }
        else if (_result is { } result)
            DrawResult(result);
        if (_solo)
            DrawInstalled();
    }

    private void RefreshInstalled()
    {
        _installed = null;
        _modsOutdated = false;
        _support = SoloPackInstaller.Check(state.Game);
        _supportCheckedAt = ImGui.GetTime();
        if (state.Game is not { } game || _support is SoloPackInstaller.Support.Enhanced or SoloPackInstaller.Support.NoGame)
            return;
        try
        {
            _installed = SoloPackInstaller.Installed(game);
            _modsOutdated = SoloPackInstaller.IsModsCopyOutdated(game, state.World.Content?.ReadFile(Data.FileSystem.GameContent.DlcListPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _installed = [];
        }
    }

    // The pack is written to a temporary folder, then installed in the background (the first time, update.rpf is copied:
    // 2 GB).
    private void Install(IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas)
    {
        if (state.Game is not { } game)
            return;
        string temp = Path.Combine(Path.GetTempPath(), "NodeRunner", "install-" + Guid.NewGuid().ToString("N"));
        string dlcRpf = Path.Combine(temp, SoloPack.ArchiveName);
        if (!Export(maps, pathAreas, dlcRpf, out string summary))
        {
            DeleteFolder(temp);
            return;
        }
        string name = _name;
        _result = null;
        _installProgress = L("Installation...");
        var progress = new InstallProgress(text => _installProgress = text);
        _installing = Task.Run(() =>
        {
            try
            {
                SoloPackInstaller.Install(game, dlcRpf, name, progress);
            }
            finally
            {
                DeleteFolder(temp);
            }
        });
        _installSummary = (name, summary, SoloPackInstaller.PackFolder(game, name));
    }

    private (string Name, string Summary, string Folder) _installSummary;

    private void FinishInstall()
    {
        if (_installing is not { IsCompleted: true } done)
            return;
        _installing = null;
        var (name, summary, folder) = _installSummary;
        _resultInstalled = done.IsCompletedSuccessfully;
        _result = done.IsCompletedSuccessfully
            ? (L($"Pack « {name} » installé dans le jeu ({summary})."), true, folder)
            : (InstallError(done.Exception?.GetBaseException()), false, null);
        RefreshInstalled();
    }

    private static string InstallError(Exception? error) => error switch
    {
        UnauthorizedAccessException => L("Windows refuse d'écrire dans le dossier du jeu. Relance NodeRunner en administrateur ")
            + L("(clic droit > Exécuter en tant qu'administrateur), ou enregistre le dlc.rpf pour l'installer toi-même."),
        null => L("Échec de l'installation."),
        _ => L($"Échec de l'installation : {error.Message}"),
    };

    private void DrawInstalled()
    {
        if (_modsOutdated)
        {
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.Warning);
            ImGui.TextWrapped(L($"{Icons.TriangleAlert}  La copie de update.rpf du dossier mods date d'avant une mise à jour du jeu : ")
                + L("des packs récents du jeu n'y sont pas. Si le jeu plante au démarrage, remplace-la par celle du jeu (OpenIV) puis réinstalle tes packs."));
            ImGui.PopStyleColor();
        }
        if (_installed is not { Count: > 0 } installed || state.Game is not { } game)
            return;
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L("Packs installés par NodeRunner"));
        ImGui.PopFont();
        foreach (var name in installed)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{Icons.Package}  {name}");
            ImGui.SameLine();
            ImGui.BeginDisabled(_installing is not null);
            if (Widgets.Button(L($"{Icons.Trash}  Désinstaller##{name}"), L("Retire le pack du jeu (dossier mods). Le projet n'est pas touché.")))
            {
                try
                {
                    SoloPackInstaller.Uninstall(game, name);
                    _resultInstalled = false;
                    _result = (L($"Pack « {name} » désinstallé."), true, null);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _result = (InstallError(ex), false, null);
                }
                RefreshInstalled();
            }
            ImGui.EndDisabled();
        }
        Widgets.Hint(L("Le dossier mods sert seulement en solo : ne lance pas GTA Online avec des mods installés."), wrap: true);
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the temporary folder, which Windows empties.
        }
    }

    // Called on the installing thread.
    private sealed class InstallProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private void DrawFolder(string? folder, string? target, ResourceExporter.TargetState targetState, bool nameValid)
    {
        Widgets.Property(L("Dossier"), L("Le dossier « resources » de ton serveur FiveM, par exemple"));
        // Button first: the path takes what is left of the cell.
        if (Widgets.Button(L($"{Icons.FolderOpen}  Choisir..."), L("Choisir le dossier où créer la ressource")))
        {
            _result = null;
            chooseFolder();
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (folder is null)
            ImGui.TextColored(UiTheme.TextFaint, L("aucun dossier choisi"));
        else
        {
            ImGui.TextUnformatted(Widgets.Fit(folder, ImGui.GetContentRegionAvail().X));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(folder);
        }

        if (target is not null)
        {
            var (text, color) = targetState switch
            {
                ResourceExporter.TargetState.Ours => (L("existe déjà (export précédent) : sera remplacée"), UiTheme.Warning),
                ResourceExporter.TargetState.Foreign => (L("ce dossier existe et n'a pas été créé par NodeRunner : choisis un autre nom"), UiTheme.Error),
                _ => (L("nouvelle ressource"), UiTheme.TextMuted),
            };
            Widgets.Property(L("Ressource"));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, Widgets.Fit(text, ImGui.GetContentRegionAvail().X));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip($"{target}\n{text}");
        }
        else if (!nameValid)
        {
            Widgets.Property(L("Ressource"));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(UiTheme.Error, L("nom invalide (minuscules, chiffres, _ et -)"));
        }
    }

    private void DrawSoloTarget(bool nameValid)
    {
        Widgets.Property(L("Pack"), L("Emplacement du pack dans le dossier mods du jeu"));
        ImGui.AlignTextToFramePadding();
        if (!nameValid)
            ImGui.TextColored(UiTheme.Error, L("nom invalide (minuscules, chiffres et _)"));
        else if (_installed?.Contains(_name) == true)
            ImGui.TextColored(UiTheme.Warning, L("déjà installé : sera remplacé"));
        else
            ImGui.TextColored(UiTheme.TextMuted, Widgets.Fit($@"mods\update\x64\dlcpacks\{_name}\{SoloPack.ArchiveName}", ImGui.GetContentRegionAvail().X));

        Widgets.Property(L("Jeu"), L("GTA V solo lit les packs du dossier mods grâce à OpenIV.asi"));
        ImGui.AlignTextToFramePadding();
        var (text, color) = _support switch
        {
            SoloPackInstaller.Support.Ready => (L("dossier mods prêt (OpenIV.asi et chargeur ASI trouvés)"), UiTheme.TextMuted),
            SoloPackInstaller.Support.NoModsLoader or SoloPackInstaller.Support.NoAsiLoader =>
                (L("le jeu ne lit pas encore le dossier mods : suis les étapes ci-dessous"), UiTheme.Warning),
            SoloPackInstaller.Support.Enhanced => (L("GTA V Enhanced : installation pas encore possible, enregistre le dlc.rpf"), UiTheme.Warning),
            _ => (L("dossier du jeu inconnu"), UiTheme.Warning),
        };
        ImGui.TextColored(color, Widgets.Fit(text, ImGui.GetContentRegionAvail().X));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip(text);
    }

    // Guides the user through OpenIV's ASI Manager: NodeRunner does not install these files itself (they hook into the
    // game, OpenIV keeps them working after each game update).
    private void DrawModsAssistant()
    {
        if (state.Game is not { } game)
            return;
        // Checked again every 2 seconds: what is installed in OpenIV shows up here by itself.
        if (ImGui.GetTime() - _supportCheckedAt > 2.0)
        {
            var before = _support;
            _support = SoloPackInstaller.Check(game);
            _supportCheckedAt = ImGui.GetTime();
            if (_support != before)
                RefreshInstalled();
            if (_support == SoloPackInstaller.Support.Ready)
            {
                _result = (L("Le jeu est prêt pour les mods : tu peux installer le pack."), true, null);
                _resultInstalled = false;
                return;
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L($"{Icons.Settings}  Préparer le jeu pour les mods"));
        ImGui.PopFont();
        Widgets.Hint(L("GTA V solo ne lit le dossier mods qu'avec deux petits fichiers, installés une fois pour toutes avec OpenIV ")
            + L("(gratuit). NodeRunner ne les installe pas lui-même : OpenIV les tient à jour après chaque mise à jour du jeu."), wrap: true);

        string? loader = SoloPackInstaller.AsiLoader(game);
        DrawRequirement(loader is not null, loader is not null ? L($"Chargeur ASI ({loader})") : L("Chargeur ASI (dinput8.dll)"));
        DrawRequirement(SoloPackInstaller.HasModsLoader(game), SoloPackInstaller.ModsLoader);

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L("1. Télécharge et installe OpenIV."));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Globe}  Ouvrir openiv.com"), L("Ouvre le site officiel d'OpenIV dans le navigateur")))
            OpenInBrowser(SoloPackInstaller.OpenIvUrl);
        ImGui.TextWrapped(L("2. Lance OpenIV, choisis « GTA V » pour « Windows », puis indique le dossier du jeu :"));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.AccentText, Widgets.Fit(game.FolderPath, ImGui.GetContentRegionAvail().X - 100 * UiTheme.Scale));
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.Copy}  Copier##gamefolder"), L("Copier le chemin du dossier du jeu")))
            ImGui.SetClipboardText(game.FolderPath);
        ImGui.TextWrapped(L("3. Dans OpenIV : menu « Outils » > « ASI Manager ». Installe « ASI Loader », puis « OpenIV.asi »."));
        ImGui.TextWrapped(L("4. Reviens ici : les deux lignes ci-dessus passent au vert toutes seules."));
        if (Widgets.Button(L($"{Icons.RefreshCw}  Vérifier à nouveau")))
            RefreshInstalled();
    }

    private static void DrawRequirement(bool present, string label)
    {
        ImGui.TextColored(present ? UiTheme.Success : UiTheme.Warning,
            present ? L($"{Icons.Check}  {label} : trouvé") : L($"{Icons.X}  {label} : absent"));
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser: the address goes to the clipboard.
            ImGui.SetClipboardText(url);
        }
    }

    private static void DrawMapList(IReadOnlyList<MapData> maps)
    {
        float height = Math.Min(maps.Count, 6) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiTheme.Field);
        if (ImGui.BeginChild("##export_maps", new Vector2(0, height), ImGuiChildFlags.None))
        {
            foreach (var map in maps.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                string name = map.FilePath is { } path ? Path.GetFileName(path) : $"0x{map.NameHash:X8}.ymap";
                ImGui.TextUnformatted($"{(map.IsSaved ? Icons.Save : Icons.File)}  {name}");
                if (map.FilePath is { } p && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(L($"Fichier du jeu : {p}"));
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawCheck(int mapCount)
    {
        if (_checking is { IsCompleted: true } finished)
        {
            _checking = null;
            _check = finished.IsCompletedSuccessfully
                ? finished.Result
                : new ExportChecker.Result([new ExportCheckItem(true, L("La vérification a échoué"), [finished.Exception?.GetBaseException().Message ?? "?"])], "", [], []);
            _excludedCollisions.Clear();
            _excludedOccluders.Clear();
            UpdatePreview();
        }
        if (_checking is null && _checkedFor != (state.ExportFolder, _name, mapCount, _removeGround) && FiveMResource.IsValidName(_name))
            StartCheck();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L("Vérification avant export"));
        ImGui.PopFont();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.RefreshCw, "recheck", L("Vérifier à nouveau"), enabled: _checking is null))
            StartCheck();

        if (_checking is not null)
        {
            ImGui.TextColored(UiTheme.TextMuted, L($"{Icons.LoaderCircle}  Vérification en cours..."));
            return;
        }
        if (_check is not { } check)
            return;

        if (check.Items.Count == 0)
            ImGui.TextColored(UiTheme.Success, L($"{Icons.Check}  Aucun problème trouvé"));
        foreach (var (item, index) in check.Items.Select((item, i) => (item, i)))
        {
            var color = item.IsProblem ? UiTheme.Warning : UiTheme.TextMuted;
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            bool open = ImGui.TreeNodeEx($"{(item.IsProblem ? Icons.TriangleAlert : Icons.Info)}  {item.Title}##check{index}", ImGuiTreeNodeFlags.SpanAvailWidth);
            ImGui.PopStyleColor();
            if (!open)
                continue;
            if (item.Advice is { } advice)
                Widgets.Hint(advice, wrap: true);
            foreach (var (detail, d) in item.Details.Select((detail, d) => (detail, d)))
            {
                if (item.Occluders is { } occluders && d < occluders.Count)
                {
                    if (ImGui.Selectable(L($"{Icons.Crosshair}  {Widgets.Fit(detail, ImGui.GetContentRegionAvail().X - 30 * UiTheme.Scale)}##occl{index}_{d}")))
                        state.ShowOccluder(occluders[d]);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                        ImGui.SetTooltip(L("Sélectionner la boîte et y aller"));
                    continue;
                }
                if (item.PathNodes is { } nodes && d < nodes.Count)
                {
                    if (ImGui.Selectable(L($"{Icons.Crosshair}  {Widgets.Fit(detail, ImGui.GetContentRegionAvail().X - 30 * UiTheme.Scale)}##node{index}_{d}")))
                    {
                        state.SelectedPathNode = nodes[d];
                        state.FocusOn(nodes[d].Position, 15f);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                        ImGui.SetTooltip(L("Sélectionner le nœud et y aller"));
                    continue;
                }
                if (item.ScenarioPoints is { } points && d < points.Count)
                {
                    if (ImGui.Selectable(L($"{Icons.Crosshair}  {Widgets.Fit(detail, ImGui.GetContentRegionAvail().X - 30 * UiTheme.Scale)}##scen{index}_{d}")))
                    {
                        state.SelectedScenario = points[d];
                        state.FocusOn(points[d].Position, 15f);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                        ImGui.SetTooltip(L("Sélectionner le point et y aller"));
                    continue;
                }
                if (item.Entities is { } entities && d < entities.Count)
                {
                    if (ImGui.Selectable(L($"{Icons.Crosshair}  {Widgets.Fit(detail, ImGui.GetContentRegionAvail().X - 30 * UiTheme.Scale)}##ent{index}_{d}")))
                    {
                        state.Select(entities[d]);
                        state.FocusOn(entities[d]);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                        ImGui.SetTooltip(L("Sélectionner l'objet et y aller"));
                    continue;
                }
                ImGui.TextUnformatted(Widgets.Fit(detail, ImGui.GetContentRegionAvail().X));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(detail);
            }
            if (item.Entities is { Count: > 1 } all
                && Widgets.Button(L($"{Icons.MousePointer}  Sélectionner ({all.Count})##selall{index}"),
                    L("Sélectionne ces objets dans la vue 3D (Suppr pour les supprimer).")))
            {
                var alive = all.Where(e => !e.IsDeleted).ToList();
                if (alive.Count > 0)
                {
                    state.SelectAll(alive);
                    state.FocusOn(alive[0]);
                }
            }
            foreach (var (conflict, n) in (item.Merge ?? []).Select((c, n) => (c, n)))
            {
                if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Reprendre les changements de « {conflict.Owner} »##merge{index}_{n}"),
                    L($"Applique à ta carte ce que « {conflict.Owner} » a changé dans {conflict.FileName} (déplacements, suppressions, ")
                    + L("ajouts). Un objet modifié des deux côtés garde ta version. Annulable (Ctrl+Z).")))
                    _merges[conflict.Path] = state.TakeChangesFrom(conflict.Path, conflict.Owner);
                if (_merges.TryGetValue(conflict.Path, out var merged))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, merged.Success ? UiTheme.Success : UiTheme.Error);
                    ImGui.TextWrapped(merged.Text);
                    ImGui.PopStyleColor();
                    foreach (string line in merged.Conflicts.Take(12))
                        ImGui.TextColored(UiTheme.Warning, Widgets.Fit(line, ImGui.GetContentRegionAvail().X));
                    if (merged.Conflicts.Count > 12)
                        ImGui.TextColored(UiTheme.Warning, L($"... et {merged.Conflicts.Count - 12} autres"));
                }
            }
            ImGui.TreePop();
        }
        if (check.Summary.Length > 0)
            Widgets.Hint(check.Summary, wrap: true);
        if (check.CollisionCuts.Count > 0)
            DrawCollisionCuts(check.CollisionCuts);
        if (check.OcclusionCuts.Count > 0)
            DrawOcclusionCuts(check.OcclusionCuts, showPreviewOption: check.CollisionCuts.Count == 0);
    }

    // Result of "take the changes" by other file.
    private readonly Dictionary<string, (string Text, bool Success, List<string> Conflicts)> _merges = new(StringComparer.OrdinalIgnoreCase);

    // Collision and occluder files the user unticked (by name), and whether the removed pieces are shown in the 3D view.
    private readonly HashSet<string> _excludedCollisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedOccluders = new(StringComparer.OrdinalIgnoreCase);
    private bool _preview = true;

    // The collision of deleted ground slabs is kept unless asked: without it, people fall through the map.
    private bool _removeGround;

    private List<CollisionFileCut> SelectedCuts() =>
        _check?.CollisionCuts.Where(c => !_excludedCollisions.Contains(c.FileName)).ToList() ?? [];

    private List<OccluderFileCut> SelectedOcclusionCuts() =>
        _check?.OcclusionCuts.Where(c => !_excludedOccluders.Contains(c.FileName)).ToList() ?? [];

    private void UpdatePreview()
    {
        state.CollisionPreview = _open && _preview ? SelectedCuts() : null;
        state.CollisionPreviewHovered = null;
        state.OcclusionPreview = _open && _preview ? SelectedOcclusionCuts() : null;
        state.OcclusionPreviewHovered = null;
    }

    private void DrawCollisionCuts(List<CollisionFileCut> cuts)
    {
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L("Collision des objets supprimés"));
        ImGui.PopFont();
        Widgets.Hint(L("Ces fichiers .ybn sont exportés sans les morceaux situés dans les objets supprimés (en rouge dans ")
            + L("la vue 3D). Décochez un fichier si l'aperçu retire autre chose (route, bâtiment voisin)."), wrap: true);
        if (ImGui.Checkbox(L("Aperçu dans la vue 3D (collision en rouge, occlusion en bleu)"), ref _preview))
            UpdatePreview();
        ImGui.BeginDisabled(_checking is not null);
        ImGui.Checkbox(L("Retirer aussi la collision du sol supprimé"), ref _removeGround); // The check restarts by itself.
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(L("Décoché : les dalles de sol supprimées (moins de 3 m de haut) gardent leur collision, on marche dessus.\n")
                + L("Coché : leur collision part aussi, on tombe sous la carte à cet endroit (pour y poser son propre sol ou un intérieur)."));

        CollisionFileCut? hovered = null;
        float height = Math.Min(cuts.Count, 7) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiTheme.Field);
        if (ImGui.BeginChild("##export_collisions", new Vector2(0, height), ImGuiChildFlags.None))
        {
            foreach (var cut in cuts)
            {
                bool included = !_excludedCollisions.Contains(cut.FileName);
                bool rowHovered = false;
                if (ImGui.Checkbox($"{cut.FileName}##cut", ref included))
                {
                    if (included)
                        _excludedCollisions.Remove(cut.FileName);
                    else
                        _excludedCollisions.Add(cut.FileName);
                    UpdatePreview();
                }
                rowHovered |= ImGui.IsItemHovered();
                ImGui.SameLine();
                ImGui.TextColored(UiTheme.TextMuted, L($"{cut.TriangleCount} triangles retirés, {KindLabel(cut.File.Kind)}"));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(L($"Fichier du jeu : {cut.File.Path}"));
                rowHovered |= ImGui.IsItemHovered();
                // Only a ticked file is in the preview.
                if (rowHovered && included)
                    hovered = cut;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        state.CollisionPreviewHovered = hovered;
    }

    private void DrawOcclusionCuts(List<OccluderFileCut> cuts, bool showPreviewOption)
    {
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(L("Occlusion des objets supprimés"));
        ImGui.PopFont();
        Widgets.Hint(L("Ces YMAP d'occlusion du jeu sont exportés sans les occluders qui touchent les objets supprimés (en bleu ")
            + L("dans la vue 3D). En retirer un de trop ne se voit pas en jeu, en laisser un fait des trous dans le décor."), wrap: true);
        if (showPreviewOption && ImGui.Checkbox(L("Aperçu en bleu dans la vue 3D"), ref _preview))
            UpdatePreview();

        OccluderFileCut? hovered = null;
        float height = Math.Min(cuts.Count, 5) * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiTheme.Field);
        if (ImGui.BeginChild("##export_occluders", new Vector2(0, height), ImGuiChildFlags.None))
        {
            foreach (var cut in cuts)
            {
                bool included = !_excludedOccluders.Contains(cut.FileName);
                bool rowHovered = false;
                if (ImGui.Checkbox($"{cut.FileName}##occl", ref included))
                {
                    if (included)
                        _excludedOccluders.Remove(cut.FileName);
                    else
                        _excludedOccluders.Add(cut.FileName);
                    UpdatePreview();
                }
                rowHovered |= ImGui.IsItemHovered();
                ImGui.SameLine();
                var parts = new List<string>();
                if (cut.Boxes.Count > 0)
                    parts.Add(cut.Boxes.Count == 1 ? L("1 boîte") : L($"{cut.Boxes.Count} boîtes"));
                if (cut.TriangleCount > 0)
                    parts.Add($"{cut.TriangleCount} triangles");
                ImGui.TextColored(UiTheme.TextMuted, L($"{string.Join(L(" et "), parts)} retirés"));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(L($"Fichier du jeu : {cut.GamePath}"));
                rowHovered |= ImGui.IsItemHovered();
                if (rowHovered && included)
                    hovered = cut;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        state.OcclusionPreviewHovered = hovered;
    }

    private static string KindLabel(CollisionKind kind) => kind switch
    {
        CollisionKind.Vehicle => L("véhicules"),
        CollisionKind.Standard => L("piétons et objets"),
        _ => "autre",
    };

    // Called once the file is chosen (Save dialog of the main window).
    public void ExportSolo(string dlcRpf)
    {
        if (_open && _solo && SoloPack.ToPackName(_name) == _name)
            Export(state.MapsToExport, state.PathAreasToSave, dlcRpf);
    }

    private void Export(IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string target) =>
        Export(maps, pathAreas, target, out _);

    private bool Export(IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string target, out string summary)
    {
        _resultInstalled = false;
        state.ExportResourceName = _name;
        var occlusion = SelectedOcclusionCuts();
        var report = _solo
            ? SoloPackExporter.Export(state.World, maps, pathAreas, _name, target, SelectedCuts(), occlusion, state.CollisionEdits,
                state.OccluderFileName, state.InteriorFilesToExport, state.SaveFolder, out var copied, out var fromLibrary)
            : ResourceExporter.Export(state.World, maps, pathAreas, target, SelectedCuts(), occlusion, state.CollisionEdits,
                state.OccluderFileName, state.InteriorFilesToExport, state.SaveFolder, out copied, out fromLibrary);
        var occluderNames = state.OccluderEdits.StreamFileNames.Concat(occlusion.Select(c => c.FileName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int collisionFiles = report.Saved.Count(f => f.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase));
        int pathFiles = report.Saved.Count(f => f.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase));
        int occlusionFiles = report.Saved.Count(occluderNames.Contains);
        int typeFiles = report.Saved.Count(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase));
        int audioFiles = report.Saved.Count(f => f.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".dat151.rel", StringComparison.OrdinalIgnoreCase));
        var extra = new List<string>();
        if (typeFiles > 0)
            extra.Add(L($"{typeFiles} intérieur(s) .ytyp"));
        if (collisionFiles > 0)
            extra.Add(L($"{collisionFiles} collision(s) .ybn"));
        if (occlusionFiles > 0)
            extra.Add(L($"{occlusionFiles} YMAP d'occlusion"));
        if (pathFiles > 0)
            extra.Add(L($"{pathFiles} pathnodes .ynd"));
        if (audioFiles > 0)
            extra.Add(L($"{audioFiles} fichier(s) audio d'intérieur"));
        if (copied.Count > 0)
            extra.Add(L($"{copied.Count} fichier(s) du projet"));
        if (fromLibrary.Count > 0)
            extra.Add(L($"{fromLibrary.Count} fichier(s) de la bibliothèque"));
        string extraText = extra.Count > 0 ? $" et {string.Join(" et ", extra)}" : "";
        string? folder = _solo ? Path.GetDirectoryName(target) : target;
        summary = $"{report.Saved.Count - collisionFiles - occlusionFiles - pathFiles - typeFiles - audioFiles} YMAP{extraText}";
        _result = report.Errors.Count == 0
            ? (L($"{report.Saved.Count - collisionFiles - occlusionFiles - pathFiles - typeFiles - audioFiles} YMAP{extraText} exporté(s) dans {target}"), true, folder)
            : (L($"Échec de l'export : {string.Join(" ; ", report.Errors)}"), false, report.Saved.Count > 0 && !_solo ? target : null);
        return report.Errors.Count == 0;
    }

    private void DrawResult((string Text, bool Success, string? Folder) result)
    {
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.PushStyleColor(ImGuiCol.Text, result.Success ? UiTheme.Success : UiTheme.Error);
        ImGui.TextWrapped($"{(result.Success ? Icons.Check : Icons.TriangleAlert)}  {result.Text}");
        ImGui.PopStyleColor();
        if (result.Success && _solo && _resultInstalled)
            Widgets.Hint(L("Lance GTA V en solo : la carte modifiée est chargée au démarrage du jeu."), wrap: true);
        else if (result.Success && _solo && result.Folder is not null)
        {
            string line = $"<Item>{SoloPack.ListEntry(_name)}</Item>";
            Widgets.Hint(L($@"Pour l'installer : place {SoloPack.ArchiveName} dans mods\update\x64\dlcpacks\{_name}\ du dossier du jeu, ")
                + L(@"puis ajoute cette ligne à la fin de la liste de dlclist.xml (mods\update\update.rpf\common\data) :"), wrap: true);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(UiTheme.AccentText, line);
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.Copy}  Copier"), L("Copier la ligne pour dlclist.xml")))
                ImGui.SetClipboardText(line);
        }
        else if (result.Success && !_solo)
        {
            string line = $"ensure {_name}";
            Widgets.Hint(L("Copie le dossier dans « resources » si besoin, puis ajoute cette ligne au server.cfg :"), wrap: true);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(UiTheme.AccentText, line);
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.Copy}  Copier"), L("Copier la ligne pour server.cfg")))
                ImGui.SetClipboardText(line);
        }
        if (result.Folder is { } folder)
        {
            ImGui.SameLine();
            if (Widgets.Button(L($"{Icons.FolderOpen}  Ouvrir le dossier")) && Directory.Exists(folder))
                System.Diagnostics.Process.Start("explorer.exe", folder);
        }
    }
}
