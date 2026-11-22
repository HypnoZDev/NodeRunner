using System.Numerics;
using ImGuiNET;
using NodeRunner.Editor;

namespace NodeRunner.App.Ui;

// "Historique" panel: the changes of the session in order; clicking one undoes or redoes up to it.
internal sealed class HistoryPanel(AppState state)
{
    public const string Title = $"{Icons.Undo}  Historique###History";

    // Rows and changes done when drawn last time: scrolls to the current row when they change.
    private int _shownCount = -1;
    private int _lastCount = -1;

    public void Draw(ref bool open)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(380f, 460f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(L(Title), ref open))
            DrawContent();
        ImGui.End();
    }

    private void DrawContent()
    {
        if (!state.Features.Editing)
        {
            Widgets.Hint(L("Dans ce jeu, la carte se consulte sans être modifiée."), wrap: true);
            return;
        }
        var history = state.History;
        bool guest = state.Online.IsGuest;
        Widgets.Hint(guest
            ? L("Invité d'une session Online : Ctrl+Z et Ctrl+Y passent par l'hôte, la liste se consulte seulement.")
            : L("Cliquez sur une ligne pour revenir à ce moment ; les lignes grises peuvent être rétablies."), wrap: true);
        ImGui.Separator();

        var done = history.Done;
        var undone = history.UndoneEdits;
        int rows = 1 + done.Count + undone.Count;
        bool scroll = rows != _shownCount || history.Count != _lastCount;
        _shownCount = rows;
        _lastCount = history.Count;

        if (!ImGui.BeginChild("##history_rows"))
        {
            ImGui.EndChild();
            return;
        }
        ImGui.BeginDisabled(guest);
        // Row 0 is the state before any change of the session; row i, after the i-th change.
        Row(0, L("Début (carte telle qu'ouverte)"), history.SavedTop is null);
        for (int i = 0; i < done.Count; i++)
            Row(i + 1, done[i].Description, done[i] == history.SavedTop);
        for (int k = undone.Count - 1; k >= 0; k--)
            Row(history.Count + (undone.Count - k), undone[k].Description, undone[k] == history.SavedTop);
        ImGui.EndDisabled();
        ImGui.EndChild();

        void Row(int count, string text, bool saved)
        {
            bool current = count == history.Count;
            bool redoable = count > history.Count;
            if (redoable)
                ImGui.PushStyleColor(ImGuiCol.Text, Widgets.Col(UiTheme.TextMuted));
            string mark = saved ? Icons.Save : " ";
            if (ImGui.Selectable($"{mark}  {text}###h{count}", current) && !current)
                state.UndoTo(count);
            if (saved && ImGui.IsItemHovered())
                ImGui.SetTooltip(L("État enregistré"));
            if (redoable)
                ImGui.PopStyleColor();
            if (current && scroll)
                ImGui.SetScrollHereY(0.5f);
        }
    }
}
