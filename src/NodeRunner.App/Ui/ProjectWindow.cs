using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "New project" window: name and location; the project is created by the main form (it may first ask about unsaved
// changes).
internal sealed class ProjectWindow(AppState state, Action chooseLocation)
{
    public const string Title = $"{Icons.FolderOpen}  Nouveau projet###NouveauProjet";

    private bool _open;
    private bool _focusName;
    private string _name = "";
    private bool _saveAfter;
    private bool _exitAfter;

    public (string Folder, string Name, bool SaveAfter, bool ExitAfter)? Requested { get; set; }

    public string? OpenRequested { get; set; }

    public void Open(bool saveAfter = false, bool exitAfter = false)
    {
        _open = true;
        _focusName = true;
        _name = "";
        _saveAfter = saveAfter;
        _exitAfter = exitAfter;
    }

    public void Close() => _open = false;

    public void Draw()
    {
        if (!_open)
            return;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(500f, 0f), new Vector2(500f, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        Widgets.Hint(_saveAfter
            ? "Les modifications sont enregistrées dans un projet : un dossier par lieu (hôpital, commissariat, garage...), "
              + "qui donnera une ressource FiveM."
            : "Un projet = un dossier : les YMAP modifiés (dans « stream »), la version de la carte et le nom de la "
              + "ressource FiveM. Un projet par lieu (hôpital, commissariat, garage...).", wrap: true);
        ImGui.Dummy(new Vector2(0, 4));

        string location = state.ProjectsFolder;
        string trimmed = _name.Trim();
        string? folder = trimmed.Length > 0 ? Path.Combine(location, MapProject.ToFolderName(trimmed)) : null;
        bool isProject = folder is not null && MapProject.Exists(folder);
        bool occupied = folder is not null && !isProject && Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

        if (Widgets.BeginProperties("##new_project", 0.28f))
        {
            Widgets.Property("Nom", "Nom du projet (texte libre), aussi le nom de son dossier");
            if (_focusName)
            {
                ImGui.SetKeyboardFocusHere();
                _focusName = false;
            }
            ImGui.SetNextItemWidth(-1);
            bool enter = ImGui.InputTextWithHint("##project_name", "ex. Hôpital Pillbox", ref _name, 80, ImGuiInputTextFlags.EnterReturnsTrue);

            Widgets.Property("Emplacement", "Dossier où sont créés les projets (chacun dans son sous-dossier)");
            if (Widgets.Button($"{Icons.FolderOpen}  Choisir...", "Choisir le dossier des projets"))
                chooseLocation();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Widgets.Fit(location, ImGui.GetContentRegionAvail().X));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(location);

            if (folder is not null)
            {
                var (text, color) = isProject ? ("un projet existe déjà ici : l'ouvrir ?", UiTheme.Warning)
                    : occupied ? ("ce dossier existe déjà et n'est pas vide : choisis un autre nom", UiTheme.Error)
                    : ($"{MapProject.ToFolderName(trimmed)}  ·  ressource « {FiveMResource.ToValidName(trimmed)} »", UiTheme.TextMuted);
                Widgets.Property("Dossier");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(color, Widgets.Fit(text, ImGui.GetContentRegionAvail().X));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(folder);
            }
            Widgets.EndProperties();

            if (enter && folder is not null && !isProject && !occupied)
                Create(folder, trimmed);
        }

        if (state.Project is null && state.History.HasUnsavedChanges)
        {
            ImGui.Dummy(new Vector2(0, 2));
            Widgets.Hint($"{Icons.Info}  Les modifications en cours ({state.PendingSaveText}) iront dans ce projet.", wrap: true);
        }

        ImGui.Dummy(new Vector2(0, 4));
        if (isProject)
        {
            if (Widgets.PrimaryButton($"{Icons.FolderOpen}  Ouvrir", 120f))
            {
                OpenRequested = folder;
                _open = false;
            }
        }
        else
        {
            ImGui.BeginDisabled(folder is null || occupied);
            if (Widgets.PrimaryButton(_saveAfter ? $"{Icons.Save}  Créer et enregistrer" : $"{Icons.Check}  Créer", _saveAfter ? 170f : 120f) && folder is not null)
                Create(folder, trimmed);
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        if (Widgets.Button("Annuler", width: 90f))
            _open = false;
    }

    private void Create(string folder, string name)
    {
        Requested = (folder, name, _saveAfter, _exitAfter);
        _open = false;
    }
}
