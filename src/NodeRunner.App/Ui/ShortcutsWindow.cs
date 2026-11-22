using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;

namespace NodeRunner.App.Ui;

// Help > Keyboard shortcuts (F1): every shortcut of the program, grouped.
internal sealed class ShortcutsWindow(AppState state)
{
    public const string Title = "Raccourcis clavier###Raccourcis";

    private bool _open;

    public void Toggle() => _open = !_open;

    // Opens Paramètres > Raccourcis.
    public Action? OpenSettings { get; set; }

    public void Draw()
    {
        if (!_open)
            return;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560f, 620f), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        bool visible = ImGui.Begin(L(Title), ref _open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        if (visible)
        {
            // Keys as set in Paramètres > Raccourcis.
            var keys = state.Keys;
            string K(KeyAction action) => keys.Text(action) is { Length: > 0 } text ? text : "-";
            (string, string) A(KeyAction action) => (K(action), KeyBindingsPage.Name(action));
            if (Widgets.Button(L($"{Icons.Keyboard}  Changer les touches...")))
                OpenSettings?.Invoke();
            Group(L("Caméra"),
                A(KeyAction.MoveForward), A(KeyAction.MoveBackward), A(KeyAction.MoveLeft), A(KeyAction.MoveRight),
                A(KeyAction.MoveUp), A(KeyAction.MoveDown),
                (L("Clic droit maintenu + souris"), L("Regarder autour")),
                (L("Maj"), L("Aller plus vite")),
                (L("Molette"), L("Changer la vitesse")),
                A(KeyAction.SpeedDown), A(KeyAction.SpeedUp), A(KeyAction.FrameModel));
            Group(L("Sélection et outils"),
                (L("Clic gauche"), L("Sélectionner")),
                (L("Ctrl + clic"), L("Ajouter ou retirer de la sélection")),
                (L("Double-clic sur une porte"), L("L'ouvrir ou la fermer (vue 3D seulement)")),
                (L("Double-clic sur un objet"), L("Modifier son modèle dans l'onglet Atelier")),
                (L("Touches des 5 outils, dans l'Atelier"), L("Outils : Choisir, Déplacer, Percer, Ajouter, Textures")),
                (L("Échap"), L("Désélectionner")),
                A(KeyAction.ToolSelect), A(KeyAction.ToolMove), A(KeyAction.ToolRotate), A(KeyAction.ToolScale), A(KeyAction.ToolCollision),
                (L("Ctrl (en glissant une poignée)"), L("Avancer par crans")));
            Group(L("Édition"),
                A(KeyAction.Undo), A(KeyAction.Redo), A(KeyAction.Copy), A(KeyAction.Paste), A(KeyAction.Delete), A(KeyAction.PutOnGround),
                A(KeyAction.Search));
            Group(L("Projet"),
                A(KeyAction.NewProject), A(KeyAction.OpenProject), A(KeyAction.Save), A(KeyAction.Export));
            Group(L("Outils et affichage"),
                A(KeyAction.Shortcuts), A(KeyAction.Viewer), A(KeyAction.Archives), A(KeyAction.Peds), A(KeyAction.Cutscenes),
                A(KeyAction.Settings), A(KeyAction.Grid),
                (L("Ctrl+Maj+1 à 9"), L("Enregistrer la vue dans un signet de caméra")),
                (L("Ctrl+1 à 9"), L("Revenir à un signet de caméra")));
            Group(L("Visionneuse de personnages"),
                (L("Espace"), L("Lecture / pause de l'animation")),
                (L("Flèche gauche / droite"), L("Image précédente / suivante")),
                (L("Origine / Fin"), L("Début / fin de l'animation")),
                ("F", L("Vue par défaut")));
            Group(L("Mode Play"),
                A(KeyAction.Play),
                (state.Keys.MoveText(), L("Se déplacer (course)")),
                (L("Maj"), L("Sprinter")),
                (L("Alt"), L("Marcher")),
                (L("Espace"), L("Sauter, ou grimper face à un rebord (aussi pour sortir de l'eau)")),
                (L("Souris"), L("Tourner la caméra")),
                (L("Molette"), L("Rapprocher ou éloigner la caméra")),
                (L("Échap"), L("Quitter le mode Play")));
        }
        ImGui.End();
    }

    private static void Group(string title, params (string Keys, string Action)[] rows)
    {
        ImGui.Spacing();
        ImGui.PushFont(UiTheme.SemiboldFont);
        ImGui.TextUnformatted(title);
        ImGui.PopFont();
        ImGui.Separator();
        if (!ImGui.BeginTable($"##{title}", 2, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("keys", ImGuiTableColumnFlags.WidthFixed, 230f);
        ImGui.TableSetupColumn("action", ImGuiTableColumnFlags.WidthStretch);
        foreach (var (keys, action) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(UiTheme.AccentText, keys);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(action);
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }
}
