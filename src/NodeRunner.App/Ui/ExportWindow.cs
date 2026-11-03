using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Collisions;
using NodeRunner.Data.Maps;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "Export as a FiveM resource" window: resource name, destination folder (a server's "resources" folder), list of the
// map files, the check before export (conflicts with other resources and projects, deleted scenery), then the line to
// add to server.cfg.
internal sealed class ExportWindow(AppState state, Action chooseFolder)
{
    public const string Title = $"{Icons.Package}  Exporter en ressource FiveM###Export";

    private bool _open;
    private string _name = FiveMResource.DefaultName;
    private (string Text, bool Success, string? Folder)? _result;

    // Check before export, run in the background when the window opens and when the folder or the name changes.
    private Task<ExportChecker.Result>? _checking;
    private ExportChecker.Result? _check;
    private (string? Folder, string Name, int Maps, bool RemoveGround) _checkedFor;

    public void Open()
    {
        _open = true;
        _name = state.ExportResourceName;
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
        bool visible = ImGui.Begin(Title, ref _open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
        if (!_open && (state.CollisionPreview is not null || state.OcclusionPreview is not null))
            UpdatePreview(); // Closed by its cross or the Close button.
    }

    private void DrawContent()
    {
        Widgets.Hint("Crée un dossier prêt pour le dossier « resources » d'un serveur FiveM : fxmanifest.lua "
            + "et les fichiers modifiés (YMAP, pathnodes) dans « stream ». Les fichiers du jeu ne sont pas touchés.", wrap: true);
        ImGui.Dummy(new Vector2(0, 4));

        var maps = state.MapsToExport;
        var pathAreas = state.PathAreasToSave;
        string? folder = state.ExportFolder;
        bool nameValid = FiveMResource.IsValidName(_name);
        string? target = folder is not null && nameValid ? Path.Combine(folder, _name) : null;
        var targetState = target is not null ? ResourceExporter.Inspect(target) : ResourceExporter.TargetState.New;

        if (Widgets.BeginProperties("##export", 0.30f))
        {
            Widgets.Property("Nom de la ressource", "Nom utilisé dans server.cfg (ensure ...) : minuscules, chiffres, _ et -");
            if (ImGui.InputText("##resource_name", ref _name, 64))
                _result = null;
            if (ImGui.IsItemDeactivatedAfterEdit() && !FiveMResource.IsValidName(_name))
                _name = FiveMResource.ToValidName(_name); // Fixed when the field is left: "Ma carte" -> "ma_carte".

            Widgets.Property("Dossier", "Le dossier « resources » de ton serveur FiveM, par exemple");
            // Button first: the path takes what is left of the cell.
            if (Widgets.Button($"{Icons.FolderOpen}  Choisir...", "Choisir le dossier où créer la ressource"))
            {
                _result = null;
                chooseFolder();
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            if (folder is null)
                ImGui.TextColored(UiTheme.TextFaint, "aucun dossier choisi");
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
                    ResourceExporter.TargetState.Ours => ("existe déjà (export précédent) : sera remplacée", UiTheme.Warning),
                    ResourceExporter.TargetState.Foreign => ("ce dossier existe et n'a pas été créé par NodeRunner : choisis un autre nom", UiTheme.Error),
                    _ => ("nouvelle ressource", UiTheme.TextMuted),
                };
                Widgets.Property("Ressource");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(color, Widgets.Fit(text, ImGui.GetContentRegionAvail().X));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip($"{target}\n{text}");
            }
            else if (!nameValid)
            {
                Widgets.Property("Ressource");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(UiTheme.Error, "nom invalide (minuscules, chiffres, _ et -)");
            }

            Widgets.Property("YMAP", "Les YMAP modifiés pendant cette session et ceux déjà enregistrés");
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(maps.Count == 0 ? pathAreas.Count == 0 ? "aucune modification à exporter" : "aucun" : $"{maps.Count} fichier(s)");
            if (pathAreas.Count > 0)
            {
                Widgets.Property("Pathnodes", "Fichiers du réseau routier (nodesN.ynd) dont des nœuds ont été déplacés");
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Join(", ", pathAreas.Select(Data.Paths.PathNetwork.FileName)));
            }
            Widgets.EndProperties();
        }

        if (maps.Count > 0)
            DrawMapList(maps);
        if (maps.Count > 0)
            DrawCheck(maps.Count);

        ImGui.Dummy(new Vector2(0, 4));
        bool canExport = (maps.Count > 0 || pathAreas.Count > 0) && target is not null && targetState != ResourceExporter.TargetState.Foreign;
        ImGui.BeginDisabled(!canExport);
        if (Widgets.PrimaryButton($"{Icons.Package}  Exporter", 120f) && target is not null)
            Export(maps, pathAreas, target);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Widgets.Button("Fermer", width: 90f))
            _open = false;

        if (_result is { } result)
            DrawResult(result);
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
                    ImGui.SetTooltip($"Fichier du jeu : {p}");
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
                : new ExportChecker.Result([new ExportCheckItem(true, "La vérification a échoué", [finished.Exception?.GetBaseException().Message ?? "?"])], "", [], []);
            _excludedCollisions.Clear();
            _excludedOccluders.Clear();
            UpdatePreview();
        }
        if (_checking is null && _checkedFor != (state.ExportFolder, _name, mapCount, _removeGround) && FiveMResource.IsValidName(_name))
            StartCheck();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted("Vérification avant export");
        ImGui.PopFont();
        ImGui.SameLine();
        if (Widgets.IconButton(Icons.RefreshCw, "recheck", "Vérifier à nouveau", enabled: _checking is null))
            StartCheck();

        if (_checking is not null)
        {
            ImGui.TextColored(UiTheme.TextMuted, $"{Icons.LoaderCircle}  Vérification en cours...");
            return;
        }
        if (_check is not { } check)
            return;

        if (check.Items.Count == 0)
            ImGui.TextColored(UiTheme.Success, $"{Icons.Check}  Aucun problème trouvé");
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
            foreach (string detail in item.Details)
            {
                ImGui.TextUnformatted(Widgets.Fit(detail, ImGui.GetContentRegionAvail().X));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(detail);
            }
            foreach (var (conflict, n) in (item.Merge ?? []).Select((c, n) => (c, n)))
            {
                if (Widgets.Button($"{Icons.ArrowDownToLine}  Reprendre les changements de « {conflict.Owner} »##merge{index}_{n}",
                    $"Applique à ta carte ce que « {conflict.Owner} » a changé dans {conflict.FileName} (déplacements, suppressions, "
                    + "ajouts). Un objet modifié des deux côtés garde ta version. Annulable (Ctrl+Z)."))
                    _merges[conflict.Path] = state.TakeChangesFrom(conflict.Path, conflict.Owner);
                if (_merges.TryGetValue(conflict.Path, out var merged))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, merged.Success ? UiTheme.Success : UiTheme.Error);
                    ImGui.TextWrapped(merged.Text);
                    ImGui.PopStyleColor();
                    foreach (string line in merged.Conflicts.Take(12))
                        ImGui.TextColored(UiTheme.Warning, Widgets.Fit(line, ImGui.GetContentRegionAvail().X));
                    if (merged.Conflicts.Count > 12)
                        ImGui.TextColored(UiTheme.Warning, $"... et {merged.Conflicts.Count - 12} autres");
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
        ImGui.TextUnformatted("Collision des objets supprimés");
        ImGui.PopFont();
        Widgets.Hint("Ces fichiers .ybn sont exportés sans les morceaux situés dans les objets supprimés (en rouge dans "
            + "la vue 3D). Décochez un fichier si l'aperçu retire autre chose (route, bâtiment voisin).", wrap: true);
        if (ImGui.Checkbox("Aperçu dans la vue 3D (collision en rouge, occlusion en bleu)", ref _preview))
            UpdatePreview();
        ImGui.BeginDisabled(_checking is not null);
        ImGui.Checkbox("Retirer aussi la collision du sol supprimé", ref _removeGround); // The check restarts by itself.
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort | ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Décoché : les dalles de sol supprimées (moins de 3 m de haut) gardent leur collision, on marche dessus.\n"
                + "Coché : leur collision part aussi, on tombe sous la carte à cet endroit (pour y poser son propre sol ou un intérieur).");

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
                ImGui.TextColored(UiTheme.TextMuted, $"{cut.TriangleCount} triangles retirés, {KindLabel(cut.File.Kind)}");
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip($"Fichier du jeu : {cut.File.Path}");
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
        ImGui.TextUnformatted("Occlusion des objets supprimés");
        ImGui.PopFont();
        Widgets.Hint("Ces YMAP d'occlusion du jeu sont exportés sans les occluders qui touchent les objets supprimés (en bleu "
            + "dans la vue 3D). En retirer un de trop ne se voit pas en jeu, en laisser un fait des trous dans le décor.", wrap: true);
        if (showPreviewOption && ImGui.Checkbox("Aperçu en bleu dans la vue 3D", ref _preview))
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
                    parts.Add(cut.Boxes.Count == 1 ? "1 boîte" : $"{cut.Boxes.Count} boîtes");
                if (cut.TriangleCount > 0)
                    parts.Add($"{cut.TriangleCount} triangles");
                ImGui.TextColored(UiTheme.TextMuted, $"{string.Join(" et ", parts)} retirés");
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip($"Fichier du jeu : {cut.GamePath}");
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
        CollisionKind.Vehicle => "véhicules",
        CollisionKind.Standard => "piétons et objets",
        _ => "autre",
    };

    private void Export(IReadOnlyList<MapData> maps, IReadOnlyList<int> pathAreas, string target)
    {
        state.ExportResourceName = _name;
        var occlusion = SelectedOcclusionCuts();
        var report = ResourceExporter.Export(state.World, maps, pathAreas, target, SelectedCuts(), occlusion);
        int collisionFiles = report.Saved.Count(f => f.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase));
        int pathFiles = report.Saved.Count(f => f.EndsWith(".ynd", StringComparison.OrdinalIgnoreCase));
        int occlusionFiles = report.Saved.Count(f => occlusion.Any(c => string.Equals(c.FileName, f, StringComparison.OrdinalIgnoreCase)));
        var extra = new List<string>();
        if (collisionFiles > 0)
            extra.Add($"{collisionFiles} collision(s) .ybn");
        if (occlusionFiles > 0)
            extra.Add($"{occlusionFiles} YMAP d'occlusion");
        if (pathFiles > 0)
            extra.Add($"{pathFiles} pathnodes .ynd");
        string extraText = extra.Count > 0 ? $" et {string.Join(" et ", extra)}" : "";
        _result = report.Errors.Count == 0
            ? ($"{report.Saved.Count - collisionFiles - occlusionFiles - pathFiles} YMAP{extraText} exporté(s) dans {target}", true, target)
            : ($"Échec de l'export : {string.Join(" ; ", report.Errors)}", false, report.Saved.Count > 0 ? target : null);
    }

    private void DrawResult((string Text, bool Success, string? Folder) result)
    {
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.PushStyleColor(ImGuiCol.Text, result.Success ? UiTheme.Success : UiTheme.Error);
        ImGui.TextWrapped($"{(result.Success ? Icons.Check : Icons.TriangleAlert)}  {result.Text}");
        ImGui.PopStyleColor();
        if (result.Success)
        {
            string line = $"ensure {_name}";
            Widgets.Hint("Copie le dossier dans « resources » si besoin, puis ajoute cette ligne au server.cfg :", wrap: true);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(UiTheme.AccentText, line);
            ImGui.SameLine();
            if (Widgets.Button($"{Icons.Copy}  Copier", "Copier la ligne pour server.cfg"))
                ImGui.SetClipboardText(line);
        }
        if (result.Folder is { } folder)
        {
            ImGui.SameLine();
            if (Widgets.Button($"{Icons.FolderOpen}  Ouvrir le dossier") && Directory.Exists(folder))
                System.Diagnostics.Process.Start("explorer.exe", folder);
        }
    }
}
