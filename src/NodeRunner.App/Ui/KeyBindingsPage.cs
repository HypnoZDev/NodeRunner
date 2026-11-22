using System.Numerics;
using ImGuiNET;
using NodeRunner.App.Input;

namespace NodeRunner.App.Ui;

// Paramètres > Raccourcis: the preset, then every action with its keys; clicking a key waits for a new one.
internal sealed class KeyBindingsPage(AppState state)
{
    private (KeyAction Action, int Index)? _waiting; // Index -1: a key added to the action.
    private string? _message;

    private static (string Name, KeyAction[] Actions)[] Groups =>
    [
        (L("Caméra"), [KeyAction.MoveForward, KeyAction.MoveBackward, KeyAction.MoveLeft, KeyAction.MoveRight, KeyAction.MoveUp, KeyAction.MoveDown,
            KeyAction.SpeedDown, KeyAction.SpeedUp, KeyAction.FrameModel]),
        (L("Outils"), [KeyAction.ToolSelect, KeyAction.ToolMove, KeyAction.ToolRotate, KeyAction.ToolScale, KeyAction.ToolCollision]),
        (L("Édition"), [KeyAction.Undo, KeyAction.Redo, KeyAction.Copy, KeyAction.Paste, KeyAction.Delete, KeyAction.PutOnGround, KeyAction.Search, KeyAction.Grid]),
        (L("Projet"), [KeyAction.NewProject, KeyAction.OpenProject, KeyAction.Save, KeyAction.Export]),
        (L("Fenêtres"), [KeyAction.Settings, KeyAction.Shortcuts, KeyAction.Viewer, KeyAction.Archives, KeyAction.Peds, KeyAction.Cutscenes]),
    ];

    public static string Name(KeyAction action) => action switch
    {
        KeyAction.MoveForward => L("Avancer"),
        KeyAction.MoveBackward => L("Reculer"),
        KeyAction.MoveLeft => L("Aller à gauche"),
        KeyAction.MoveRight => L("Aller à droite"),
        KeyAction.MoveUp => L("Monter"),
        KeyAction.MoveDown => L("Descendre"),
        KeyAction.SpeedDown => L("Ralentir la caméra"),
        KeyAction.SpeedUp => L("Accélérer la caméra"),
        KeyAction.FrameModel => L("Cadrer le modèle (Atelier, personnages)"),
        KeyAction.ToolSelect => L("Outil Sélection"),
        KeyAction.ToolMove => L("Outil Déplacer"),
        KeyAction.ToolRotate => L("Outil Tourner"),
        KeyAction.ToolScale => L("Outil Échelle"),
        KeyAction.ToolCollision => L("Outil Collisions"),
        KeyAction.Undo => L("Annuler"),
        KeyAction.Redo => L("Rétablir"),
        KeyAction.Copy => L("Copier les objets sélectionnés"),
        KeyAction.Paste => L("Coller"),
        KeyAction.Delete => L("Supprimer"),
        KeyAction.PutOnGround => L("Poser au sol"),
        KeyAction.Search => L("Rechercher (objet, YMAP, type, position)"),
        KeyAction.Grid => L("Grille au sol"),
        KeyAction.NewProject => L("Nouveau projet"),
        KeyAction.OpenProject => L("Ouvrir un projet"),
        KeyAction.Save => L("Enregistrer"),
        KeyAction.Export => L("Exporter en ressource FiveM"),
        KeyAction.Settings => L("Paramètres"),
        KeyAction.Shortcuts => L("Raccourcis clavier"),
        KeyAction.Viewer => L("Visionneuse de modèles"),
        KeyAction.Archives => L("Explorateur d'archives"),
        KeyAction.Peds => L("Visionneuse de personnages"),
        KeyAction.Cutscenes => L("Visionneuse de cinématiques"),
        _ => action.ToString(),
    };

    public void Draw()
    {
        var settings = state.Settings;
        int preset = (int)settings.KeyPreset;
        string[] presets = ["NodeRunner", "CodeWalker"];
        string?[] tips =
        [
            L("Touches de NodeRunner : ZQSD / WASD (Paramètres > Caméra), Espace et C pour monter et descendre, 1 à 5 pour les outils."),
            L("Touches de CodeWalker (clavier QWERTY) : WASD, R et F pour monter et descendre, Z et X pour la vitesse, Q pour sélectionner. Ses touches W, E, R des outils déplacent aussi la caméra : les outils gardent 1 à 5."),
        ];
        if (Widgets.Segmented("##key_preset", presets, ref preset, tips) && preset != (int)settings.KeyPreset)
        {
            settings.KeyPreset = (KeyPreset)preset;
            settings.KeyBindings.Clear();
            Changed();
            _message = L("Préréglage appliqué : les touches changées à la main ont été remises à celles du préréglage.");
        }
        Widgets.Hint(L("Cliquez sur une touche pour la changer, sur + pour en ajouter une, clic droit pour la retirer. Fixes : Échap (annuler), Ctrl+1 à 9 et Ctrl+Maj+1 à 9 (signets de caméra)."), wrap: true);
        if (settings.KeyBindings.Count > 0)
        {
            if (Widgets.Button(L("Tout remettre par défaut"), L("Remet toutes les touches du préréglage choisi.")))
            {
                settings.KeyBindings.Clear();
                Changed();
                _message = null;
            }
        }
        if (_message is { } message)
            ImGui.TextColored(UiTheme.Warning, message);
        if (_waiting is { } waiting)
            Wait(waiting.Action, waiting.Index);
        ImGui.Dummy(new Vector2(0, 4));

        foreach (var (group, actions) in Groups)
        {
            ImGui.PushFont(UiTheme.TitleFont);
            ImGui.TextUnformatted(group);
            ImGui.PopFont();
            if (!Widgets.BeginProperties($"##keys_{group}", 0.42f))
                continue;
            foreach (var action in actions)
                Row(action);
            Widgets.EndProperties();
            ImGui.Dummy(new Vector2(0, 4));
        }
    }

    private void Row(KeyAction action)
    {
        var settings = state.Settings;
        Widgets.Property(Name(action), KeyBindings.IsCamera(action) ? L("Touche tenue, sans Ctrl ni Alt ; Maj = plus vite.") : null);
        var chords = state.Keys.Chords(action);
        for (int i = 0; i < chords.Length; i++)
        {
            bool waiting = _waiting == (action, i);
            if (ImGui.Button($"{(waiting ? L("Appuyez sur une touche...") : chords[i].ToString())}##{action}_{i}"))
                _waiting = (action, i);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                Set(action, [.. chords.Where((_, n) => n != i)]);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L("Clic : changer cette touche. Clic droit : la retirer."));
            ImGui.SameLine();
        }
        bool adding = _waiting == (action, -1);
        if (ImGui.Button(adding ? $"{L("Appuyez sur une touche...")}##{action}_add" : $"{Icons.Plus}##{action}_add"))
            _waiting = (action, -1);
        if (ImGui.IsItemHovered() && !adding)
            ImGui.SetTooltip(L("Ajouter une touche"));
        if (settings.KeyBindings.ContainsKey(action.ToString()))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{L("Par défaut")}##{action}_reset"))
            {
                settings.KeyBindings.Remove(action.ToString());
                Changed();
            }
        }
    }

    // Waits for the new key of action: the first key pressed this frame, with the modifiers held.
    private void Wait(KeyAction action, int index)
    {
        state.KeyCaptureFrame = ImGui.GetFrameCount();
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            Stop();
            return;
        }
        var io = ImGui.GetIO();
        for (var key = ImGuiKey.NamedKey_BEGIN; key < ImGuiKey.NamedKey_END; key++)
        {
            if (!KeyBindings.CanBind(key) || !ImGui.IsKeyPressed(key, false))
                continue;
            var chord = KeyBindings.IsCamera(action) ? new KeyChord(key) : new KeyChord(key, io.KeyCtrl, io.KeyShift, io.KeyAlt);
            if (KeyBindings.IsCamera(action) && (io.KeyCtrl || io.KeyAlt))
                _message = L("Les touches de la caméra se tiennent sans Ctrl ni Alt : seule la touche est gardée.");
            else
                _message = null;
            if (KeyBindings.IsReserved(chord))
            {
                _message = L($"{chord} est réservé aux signets de caméra.");
                Stop();
                return;
            }
            var chords = state.Keys.Chords(action).ToList();
            if (chords.Contains(chord))
            {
                Stop();
                return;
            }
            if (KeyBindings.Conflict(state.Keys.Map, action, chord) is { } other)
            {
                // The key moves: the other action loses it.
                var kept = state.Keys.Chords(other).Where(c => !(c.Key == chord.Key && c.Ctrl == chord.Ctrl && c.Alt == chord.Alt
                    && (c.Shift == chord.Shift || !c.Ctrl))).ToArray();
                Set(other, kept);
                _message = L($"{chord} servait à « {Name(other)} » : retiré de cette action.");
            }
            if (index >= 0 && index < chords.Count)
                chords[index] = chord;
            else
                chords.Add(chord);
            Set(action, [.. chords]);
            Stop();
            return;
        }
    }

    private void Stop()
    {
        _waiting = null;
        state.KeyCaptureFrame = ImGui.GetFrameCount();
    }

    private void Set(KeyAction action, KeyChord[] chords)
    {
        state.Settings.KeyBindings[action.ToString()] = [.. chords.Select(c => c.Save())];
        Changed();
    }

    private void Changed()
    {
        state.Keys.Changed();
        state.SaveSettings();
    }
}
