using System.Numerics;
using ImGuiNET;

namespace NodeRunner.App.Ui;

// Welcome screen: shown once at first launch (then on demand, Help > Welcome screen): GTA V folder, start a project,
// recent projects, first steps.
internal sealed class WelcomeWindow(AppState state, MainUi ui, Action showShortcuts)
{
    public const string Title = "Bienvenue###Bienvenue";

    private const int RecentShown = 5;

    private bool _open;
    private bool _checkedStartup;

    public void Open() => _open = true;

    public void Draw()
    {
        if (!_checkedStartup)
        {
            _checkedStartup = true;
            _open = !state.Settings.WelcomeSeen || state.Settings.WelcomeAtStartup;
        }
        if (!_open)
            return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560f, 0f), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 18));
        bool wasOpen = _open;
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
        if (wasOpen && !_open)
            Close();
    }

    private void Close()
    {
        _open = false;
        if (!state.Settings.WelcomeSeen)
        {
            state.Settings.WelcomeSeen = true;
            state.SaveSettings();
        }
    }

    private void DrawContent()
    {
        const float width = 516f;
        ImGui.PushFont(UiTheme.TitleFont);
        ImGui.TextUnformatted(L($"Bienvenue dans {Program.Name}"));
        ImGui.PopFont();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(UiTheme.TextMuted, L("Voir, modifier et exporter la carte de GTA V pour FiveM. Trois étapes pour commencer :"));
        ImGui.PopTextWrapPos();

        // 1. GTA V folder.
        Step("1", L("Dossier de GTA V"));
        if (state.Game is { } game)
        {
            ImGui.TextColored(UiTheme.Success, $"{Icons.Check}  " + L("Trouvé"));
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.TextMuted, Widgets.Fit(game.FolderPath, width - ImGui.GetCursorPosX() + ImGui.GetStyle().WindowPadding.X));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip(game.FolderPath);
        }
        else
        {
            ImGui.TextColored(UiTheme.Warning, $"{Icons.TriangleAlert}  " + L("GTA V n'a pas été trouvé automatiquement."));
            if (Widgets.PrimaryButton(L($"{Icons.FolderOpen}  Choisir le dossier de GTA V..."), width))
                ui.BrowseGameFolderRequested = true;
        }

        // 2. Project.
        Step("2", L("Un projet"));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(UiTheme.TextMuted, L("Un projet = un dossier avec vos modifications, prêt à devenir une ressource FiveM."));
        ImGui.PopTextWrapPos();
        float third = (width - ImGui.GetStyle().ItemSpacing.X * 2f) / 3f;
        if (Widgets.PrimaryButton(L($"{Icons.FolderPlus}  Nouveau projet"), third))
        {
            ui.ProjectWindow.Open();
            Close();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
            ImGui.SetTooltip("Ctrl+N");
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.FolderOpen}  Ouvrir un projet"), "Ctrl+O", third))
        {
            ui.OpenProjectRequested = true;
            Close();
        }
        ImGui.SameLine();
        if (Widgets.Button(L($"{Icons.ArrowDownToLine}  Importer un mapping"),
                L("Ouvre le mapping de quelqu'un d'autre (ressource FiveM, dossier stream, .oiv, .zip) dans un nouveau projet.\nAstuce : on peut aussi le glisser sur la fenêtre."), third))
        {
            ui.ImportMappingRequested = true;
            Close();
        }

        var recent = state.Settings.RecentProjects
            .Where(f => !string.Equals(f, state.Project?.Folder, StringComparison.OrdinalIgnoreCase) && NodeRunner.Editor.MapProject.Exists(f))
            .Take(RecentShown).ToList();
        if (recent.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.TextMuted, L("Projets récents"));
            foreach (var folder in recent)
            {
                if (ImGui.Selectable($"{Icons.FolderOpen}  {Path.GetFileName(folder)}##{folder}", false, ImGuiSelectableFlags.None, new Vector2(width, 0)))
                {
                    ui.RecentProjectRequested = folder;
                    Close();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                    ImGui.SetTooltip(folder);
            }
        }
        else
        {
            ImGui.Spacing();
            Widgets.Hint(L("Sans projet, vous pouvez déjà vous promener sur la carte et regarder les objets."), wrap: true);
        }

        // 3. First steps.
        Step("3", L("Premiers pas"));
        string moveKeys = state.Settings.MovementKeys == Input.MovementKeys.Wasd ? "W A S D" : "Z Q S D";
        Tip(L($"{moveKeys} + clic droit maintenu"), L("se déplacer et regarder autour (Maj = plus vite)"));
        Tip(L("Clic gauche"), L("sélectionner un objet, ses propriétés s'affichent à droite"));
        Tip(L("Clic droit sur un objet"), L("menu rapide (aller à, copier la position, supprimer...)"));
        Tip("2 / 3 / 4", L("déplacer, tourner, mettre à l'échelle avec les poignées"));
        Tip("Ctrl+K", L("rechercher un objet, un YMAP ou une position FiveM"));
        Tip("Ctrl+S", L("enregistrer, puis Ctrl+E pour exporter en ressource FiveM"));
        if (Widgets.Button(L("Tous les raccourcis clavier (F1)"), null, width))
            showShortcuts();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        bool atStartup = state.Settings.WelcomeAtStartup;
        if (ImGui.Checkbox(L("Afficher à chaque démarrage"), ref atStartup))
        {
            state.Settings.WelcomeAtStartup = atStartup;
            state.SaveSettings();
        }
        ImGui.SetItemTooltip(L("Toujours disponible dans Aide > Écran d'accueil."));
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetStyle().WindowPadding.X + width - 120f);
        if (Widgets.PrimaryButton(L("Commencer"), 120f))
            Close();
    }

    private static void Step(string number, string title)
    {
        ImGui.Dummy(new Vector2(0, 8));
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextColored(UiTheme.AccentText, number);
        ImGui.SameLine();
        ImGui.TextUnformatted(title);
        ImGui.PopFont();
        ImGui.Dummy(new Vector2(0, 2));
    }

    private static void Tip(string keys, string action)
    {
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(keys);
        ImGui.PopFont();
        ImGui.SameLine(0, 8);
        ImGui.TextColored(UiTheme.TextMuted, action);
    }
}
