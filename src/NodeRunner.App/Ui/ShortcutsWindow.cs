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
            string moveKeys = state.Settings.MovementKeys switch
            {
                MovementKeys.Zqsd => "Z Q S D",
                MovementKeys.Wasd => "W A S D",
                _ => "Z Q S D / W A S D",
            };
            Group(L("Caméra"),
                (moveKeys + L(" ou flèches"), L("Se déplacer")),
                (L("Clic droit maintenu + souris"), L("Regarder autour")),
                (L("Maj"), L("Aller plus vite")),
                (L("Molette"), L("Changer la vitesse")),
                (L("Espace / E"), L("Monter")),
                ("C", L("Descendre")));
            Group(L("Sélection et outils"),
                (L("Clic gauche"), L("Sélectionner")),
                (L("Ctrl + clic"), L("Ajouter ou retirer de la sélection")),
                (L("Double-clic sur une porte"), L("L'ouvrir ou la fermer (vue 3D seulement)")),
                (L("Échap"), L("Désélectionner")),
                ("1", L("Outil Sélection")),
                ("2", L("Outil Déplacer")),
                ("3", L("Outil Tourner")),
                ("4", L("Outil Échelle")),
                ("5", L("Outil Collisions")),
                (L("Ctrl (en glissant une poignée)"), L("Avancer par crans")));
            Group(L("Édition"),
                ("Ctrl+Z", L("Annuler")),
                (L("Ctrl+Y / Ctrl+Maj+Z"), L("Rétablir")),
                ("Ctrl+C", L("Copier les objets sélectionnés")),
                ("Ctrl+V", L("Coller")),
                (L("Suppr"), L("Supprimer")),
                (L("Fin"), L("Poser au sol")),
                ("Ctrl+K", L("Rechercher (objet, YMAP, type, position)")));
            Group(L("Projet"),
                ("Ctrl+N", L("Nouveau projet")),
                ("Ctrl+O", L("Ouvrir un projet")),
                ("Ctrl+S", L("Enregistrer")),
                ("Ctrl+E", L("Exporter en ressource FiveM")));
            Group(L("Outils et affichage"),
                ("F1", L("Raccourcis clavier")),
                ("F2", L("Visionneuse de modèles")),
                ("F3", L("Explorateur d'archives")),
                ("Ctrl+,", L("Paramètres")),
                ("G", L("Grille au sol")));
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
