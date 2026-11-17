using System.Numerics;
using ImGuiNET;
using NodeRunner.App.World;
using NodeRunner.Data.Installation;

namespace NodeRunner.App.Ui;

// Bar at the bottom of the window. Left: what is going on (loading, messages, unsaved changes), then what the mouse and
// the keys do right now (for the tool in use, the keys held). Right: errors, camera position, game edition and frames
// per second. The project, the map version and the DLC are in the toolbar.
internal sealed class StatusBar(AppState state, ModelLayer modelLayer, Action openErrors)
{
    private const float PaddingY = 3f;
    private const float Gap = 24f;

    // Set by the interface: a 3D handle is being dragged, the camera is looking around (right button held), and the
    // hints of the model workshop's tool.
    public Func<bool>? IsDragging { get; set; }
    public Func<bool>? IsLooking { get; set; }
    public Func<IReadOnlyList<(string Keys, string Action)>>? WorkshopHints { get; set; }
    public Func<IReadOnlyList<(string Keys, string Action)>>? PedHints { get; set; }

    public void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, PaddingY));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UiTheme.Chrome);
        if (DockLayout.BeginSideBar("##statusbar", ImGuiDir.Down, ImGui.GetTextLineHeight() + PaddingY * 2 + 2))
        {
            var right = RightTexts();
            float rightWidth = ImGui.CalcTextSize(right.Errors).X + ImGui.CalcTextSize(right.Position).X + ImGui.CalcTextSize(right.Edition).X
                + ImGui.CalcTextSize(right.Fps).X + Gap * 3;
            float rightX = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - rightWidth;
            bool any = DrawStatus();
            DrawHints(any, rightX - Gap);
            DrawRight(right, rightX);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    // What is going on; true when something was written.
    private bool DrawStatus()
    {
        bool any = false;
        void Next()
        {
            if (any)
                Separator();
            any = true;
        }

        if (state.IsGta4)
        {
            Next();
            Item(Icons.Globe, World.Gta4World.EpisodeName(state.Gta4.Episode));
        }
        if (state.IsClassic)
        {
            var classic = state.Classic;
            Next();
            Item(Icons.Globe, classic.GameName);
            if (classic.Error is { } classicError)
            {
                Next();
                Item(Icons.TriangleAlert, classicError, UiTheme.Error);
            }
            else if (classic.IsBusy)
            {
                Next();
                Item(Icons.LoaderCircle, classic.Status, UiTheme.Accent with { X = 0.42f, Y = 0.70f, Z = 1f });
            }
        }
        var world = state.World;
        if (!state.IsOtherGame && world.Error is not null)
        {
            Next();
            Item(Icons.TriangleAlert, world.Status, UiTheme.Error);
            Clickable(L("Clic : voir l'erreur et copier le rapport"));
        }
        else if (!state.IsOtherGame && world.IsBusy)
        {
            Next();
            Item(Icons.LoaderCircle, world.Status, UiTheme.Accent with { X = 0.42f, Y = 0.70f, Z = 1f });
        }
        else if (modelLayer.PendingModelCount > 0)
        {
            Next();
            Item(Icons.LoaderCircle, L($"{modelLayer.PendingModelCount} modèles en cours de chargement"), ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        if (state.Online.Session is { IsOver: false } session)
        {
            Next();
            string host = session.Participants.Count > 0 ? session.Participants[0] : L("l'hôte");
            Item(Icons.Users, session.Role == Editor.Online.SessionRole.Host
                ? L($"Session Online : {session.Participants.Count} participant(s)")
                : L($"Session Online de {host} : c'est {host} qui enregistre"), UiTheme.Accent);
        }

        if (state.History.HasUnsavedChanges && !state.Online.IsGuest)
        {
            Next();
            Item(Icons.Save, L($"Modifications non enregistrées ({state.PendingSaveText}) : Ctrl+S"), UiTheme.Warning);
        }
        else if (state.LastSave is { } save && (DateTime.Now - save.Time).TotalSeconds < 8)
        {
            Next();
            Item(save.Success ? Icons.Check : Icons.TriangleAlert, save.Text, save.Success ? UiTheme.Success : UiTheme.Error);
        }
        return any;
    }

    // What the mouse and the keys do now, as many as fit before the right part.
    private void DrawHints(bool afterStatus, float limitX)
    {
        var hints = Hints();
        if (hints.Count == 0)
            return;
        float keyPadding = 4f;
        bool first = true;
        var drawList = ImGui.GetWindowDrawList();
        foreach (var (keys, action) in hints)
        {
            float width = ImGui.CalcTextSize(keys).X + keyPadding * 2f + 6f + ImGui.CalcTextSize(action).X;
            bool separator = first && afterStatus;
            if (separator)
                ImGui.SameLine(0, 12f);
            else if (!first)
                ImGui.SameLine(0, 16f);
            if (ImGui.GetCursorPosX() + width + (separator ? 24f : 0f) > limitX)
                return; // No room left before the right part.
            if (separator)
            {
                ImGui.TextColored(UiTheme.Rgb(0x34363C), "|");
                ImGui.SameLine(0, 12f);
            }
            first = false;
            // The key as a small cap, then what it does.
            var at = ImGui.GetCursorScreenPos();
            var size = ImGui.CalcTextSize(keys);
            drawList.AddRectFilled(at - new Vector2(0, 1), at + new Vector2(size.X + keyPadding * 2f, size.Y + 1), Widgets.Col(UiTheme.Field), 3f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + keyPadding);
            ImGui.TextUnformatted(keys);
            ImGui.SameLine(0, keyPadding + 6f);
            ImGui.TextColored(UiTheme.TextMuted, action);
        }
    }

    private IReadOnlyList<(string Keys, string Action)> Hints()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput)
            return [(L("Entrée"), L("valider")), (L("Échap"), L("annuler la saisie"))];
        if (state.WorkshopShown && WorkshopHints?.Invoke() is { } workshop)
            return workshop;
        if (state.PedShown && PedHints?.Invoke() is { } ped)
            return ped;
        string move = state.Settings.MovementKeys == Input.MovementKeys.Wasd ? "W A S D" : state.Settings.MovementKeys == Input.MovementKeys.Zqsd ? "Z Q S D" : "Z Q S D / W A S D";
        if (IsLooking?.Invoke() == true)
        {
            return [(move, L("se déplacer")), (L("Espace / C"), L("monter / descendre")), (L("Maj"), L("5 fois plus vite")), (L("Molette"), L("vitesse"))];
        }
        if (IsDragging?.Invoke() == true)
            return [("Ctrl", L("par pas")), (L("Échap"), L("annuler le déplacement"))];
        if (io.KeyCtrl)
        {
            return state.IsOtherGame
                ? [("Ctrl+K", L("rechercher"))]
                : [(L("Ctrl+clic"), L("ajouter à la sélection")), ("Ctrl+Z / Y", L("annuler / rétablir")), ("Ctrl+S", L("enregistrer")),
                   ("Ctrl+C / V", L("copier / coller")), ("Ctrl+K", L("rechercher")), ("Ctrl+E", L("exporter"))];
        }
        if (state.IsOtherGame)
        {
            return [(L("Clic"), L("propriétés (lecture seule)")), (L("Clic droit maintenu"), L("regarder autour")), (move, L("se déplacer")),
                    ("Ctrl+K", L("rechercher"))];
        }
        if (state.OpeningTool is not null)
            return [(L("Clic sur le mur"), L("placer l'ouverture")), (L("Échap"), L("arrêter"))];
        if (state.Tool == TransformTool.Collision)
        {
            return [(L("Clic"), L("choisir une surface")), (L("Ctrl+clic"), L("ajouter / retirer")), (L("Glisser"), L("rectangle")),
                    (L("Suppr"), L("supprimer")), ("1", L("revenir à la sélection"))];
        }
        if (state.Selection is not null)
        {
            return state.Tool == TransformTool.Select
                ? [(L("Double-clic"), L("modifier le modèle (porte : l'ouvrir)")), (L("Clic droit"), L("menu")), ("2 / 3 / 4", L("déplacer / tourner / échelle")),
                   (L("Fin"), L("poser au sol")), (L("Suppr"), L("supprimer")), (L("Échap"), L("désélectionner"))]
                : [(L("Tirer une poignée"), L("modifier")), ("Ctrl", L("par pas")), (L("Fin"), L("poser au sol")), (L("Suppr"), L("supprimer")),
                   ("1", L("sélection")), (L("Échap"), L("désélectionner"))];
        }
        return [(L("Clic"), L("sélectionner")), (L("Clic droit maintenu"), L("regarder autour")), (move, L("se déplacer")), (L("Molette"), L("vitesse")),
                ("Ctrl+K", L("rechercher")), ("F1", L("raccourcis"))];
    }

    private readonly record struct RightPart(string Errors, string Position, string Edition, string Fps, int ErrorCount);

    private RightPart RightTexts()
    {
        var p = state.Camera.Position;
        string position = $"{Icons.MapPinned}  X {p.X:0}   Y {p.Y:0}   Z {p.Z:0}";
        string edition = state.IsGta4 ? $"{Icons.HardDrive}  GTA IV" : state.IsClassic ? $"{Icons.HardDrive}  {state.Classic.GameName}" : state.Game is { } game ? L($"{Icons.HardDrive}  GTA V {(game.Edition == GameEdition.Legacy ? "Legacy" : "Enhanced")}") : L($"{Icons.HardDrive}  GTA V introuvable");
        string fps = $"{Icons.Gauge}  {ImGui.GetIO().Framerate:0} FPS";
        int errorCount = Diagnostics.ErrorLog.Count;
        string errors = errorCount > 0 ? $"{Icons.TriangleAlert}  {errorCount}" : Icons.TriangleAlert;
        return new RightPart(errors, position, edition, fps, errorCount);
    }

    private void DrawRight(RightPart right, float x)
    {
        ImGui.SameLine();
        if (x <= ImGui.GetCursorPosX() + Gap)
            return; // Window too narrow: the left part wins.

        ImGui.SetCursorPosX(x);
        if (right.ErrorCount > 0)
            ImGui.TextColored(Diagnostics.ErrorLog.Unseen > 0 ? UiTheme.Error : UiTheme.Warning, right.Errors);
        else
            ImGui.TextDisabled(right.Errors);
        Clickable(right.ErrorCount > 0 ? L($"{right.ErrorCount} erreur(s) : clic pour les voir et copier le rapport") : L("Aucune erreur. Clic : rapport à envoyer en cas de problème"));
        ImGui.SameLine(0, Gap);
        ImGui.TextDisabled(right.Position);
        ImGui.SameLine(0, Gap);
        ImGui.TextDisabled(right.Edition);
        ImGui.SameLine(0, Gap);
        ImGui.TextUnformatted(right.Fps);
    }

    private static void Item(string icon, string text, Vector4? color = null)
    {
        if (color is { } c)
            ImGui.TextColored(c, $"{icon}  {text}");
        else
            ImGui.TextUnformatted($"{icon}  {text}");
    }

    private void Clickable(string tooltip)
    {
        if (!ImGui.IsItemHovered())
            return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetTooltip(tooltip);
        if (ImGui.IsItemClicked())
            openErrors();
    }

    private static void Separator()
    {
        ImGui.SameLine(0, 12);
        ImGui.TextColored(UiTheme.Rgb(0x34363C), "|");
        ImGui.SameLine(0, 12);
    }
}
