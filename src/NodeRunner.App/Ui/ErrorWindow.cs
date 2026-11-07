using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Diagnostics;
using NodeRunner.App.World;

namespace NodeRunner.App.Ui;

// "Errors" window (triangle of the status bar, Help menu): errors met since start-up and a report to copy and send.
internal sealed class ErrorWindow(AppState state, ModelLayer modelLayer, CollisionLayer collisionLayer, NavMeshLayer navMeshLayer)
{
    public const string Title = $"{Icons.TriangleAlert}  Erreurs###Errors";

    private bool _open;
    private DateTime _copiedAt;

    public void Open() => _open = true;

    public void Draw()
    {
        if (!_open)
            return;
        ErrorLog.MarkSeen();
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(620f, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420f, 0f), new Vector2(1400f, 900f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.PopStyleVar();
        if (visible)
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        var errors = ErrorLog.Snapshot();
        var problems = ErrorReport.Problems(state, modelLayer, collisionLayer, navMeshLayer);
        int problemCount = problems.Sum(p => p.Items.Count);

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 588f);
        if (errors.Count == 0)
            ImGui.TextColored(UiTheme.Success, $"{Icons.Check}  Aucune erreur depuis le lancement.");
        else
            ImGui.TextColored(UiTheme.Error, $"{Icons.TriangleAlert}  {errors.Count} erreur(s) depuis le lancement.");
        Widgets.Hint("En cas de problème, copie le rapport et envoie-le au développeur (Discord, GitHub). "
            + "Il ne contient aucun fichier du jeu ni ton nom d'utilisateur Windows.", wrap: true);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (Widgets.PrimaryButton($"{Icons.Copy}  Copier le rapport"))
        {
            ImGui.SetClipboardText(ErrorReport.Build(state, problems));
            _copiedAt = DateTime.Now;
        }
        ImGui.SameLine();
        if (Widgets.Button($"{Icons.Trash}  Vider la liste", enabled: errors.Count > 0))
            ErrorLog.Clear();
        if ((DateTime.Now - _copiedAt).TotalSeconds < 4)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(UiTheme.Success, $"{Icons.Check}  Rapport copié : colle-le dans ton message (Ctrl+V).");
        }

        if (errors.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            float height = MathF.Min(420f, errors.Count * 64f + 8f);
            if (ImGui.BeginChild("##errors", new Vector2(588f, height)))
            {
                for (int i = errors.Count - 1; i >= 0; i--)
                {
                    var error = errors[i];
                    ImGui.PushID(i);
                    ImGui.TextDisabled($"{error.Time:HH:mm:ss}");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(error.Where);
                    ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 4f);
                    ImGui.TextColored(UiTheme.Error, error.Message);
                    ImGui.PopTextWrapPos();
                    if (error.Details is not null && ImGui.TreeNode("Détails"))
                    {
                        Widgets.Hint(error.Details, wrap: true);
                        ImGui.TreePop();
                    }
                    ImGui.Spacing();
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }

        if (problemCount > 0)
        {
            ImGui.Spacing();
            Widgets.Hint($"{problemCount} fichier(s) du jeu n'ont pas pu être lus (souvent normal) : détail dans le panneau État > Problèmes, "
                + "et dans le rapport.", wrap: true);
        }
    }
}
