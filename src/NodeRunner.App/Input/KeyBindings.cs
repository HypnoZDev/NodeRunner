using ImGuiNET;
using NodeRunner.App.Configuration;

namespace NodeRunner.App.Input;

// Everything a key can be given to. The order is the one of the shortcuts page; names are kept in settings.json.
public enum KeyAction
{
    MoveForward,
    MoveBackward,
    MoveLeft,
    MoveRight,
    MoveUp,
    MoveDown,
    SpeedDown,
    SpeedUp,
    FrameModel,
    ToolSelect,
    ToolMove,
    ToolRotate,
    ToolScale,
    ToolCollision,
    Undo,
    Redo,
    Copy,
    Paste,
    Delete,
    PutOnGround,
    Search,
    Grid,
    NewProject,
    OpenProject,
    Save,
    Export,
    Settings,
    Shortcuts,
    Viewer,
    Archives,
    Peds,
    Cutscenes,
    Play,
}

// The base the user's changes apply to.
public enum KeyPreset
{
    NodeRunner,
    CodeWalker,
}

// A key with its modifiers. Camera keys (KeyBindings.IsCamera) have none: Shift makes the camera faster.
public readonly record struct KeyChord(ImGuiKey Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    // As kept in settings.json: "Ctrl+Shift+Z", key names of ImGuiKey.
    public string Save() => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;

    public static KeyChord? Parse(string text)
    {
        bool ctrl = false, shift = false, alt = false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "Ctrl": ctrl = true; break;
                case "Shift": shift = true; break;
                case "Alt": alt = true; break;
                default: return null;
            }
        }
        if (!Enum.TryParse<ImGuiKey>(parts[^1], out var key) || !KeyBindings.CanBind(key))
            return null;
        return new KeyChord(key, ctrl, shift, alt);
    }

    // As shown in the interface: "Ctrl+Maj+Z" in French.
    public override string ToString() =>
        (Ctrl ? "Ctrl+" : "") + (Shift ? L("Maj") + "+" : "") + (Alt ? "Alt+" : "") + KeyBindings.KeyName(Key);
}

// The default keys of each preset, and the user's changes from settings.json applied over them.
public static class KeyBindings
{
    public static readonly KeyAction[] All = Enum.GetValues<KeyAction>();

    public static bool IsCamera(KeyAction action) => action <= KeyAction.MoveDown;

    // Actions only acted upon on the map, or only in the workshop / ped viewer: they may share a key.
    private static bool MapOnly(KeyAction action) => action is KeyAction.Copy or KeyAction.Paste or KeyAction.Delete or KeyAction.PutOnGround
        or KeyAction.Undo or KeyAction.Redo or KeyAction.Grid or KeyAction.SpeedDown or KeyAction.SpeedUp;

    private static bool ViewerOnly(KeyAction action) => action is KeyAction.FrameModel;

    public static bool CanBind(ImGuiKey key) => key is not (ImGuiKey.None or ImGuiKey.Escape)
        && !(key >= ImGuiKey.LeftCtrl && key <= ImGuiKey.RightSuper) && !(key >= ImGuiKey.MouseLeft && key <= ImGuiKey.MouseWheelY)
        && key < ImGuiKey.NamedKey_END && key >= ImGuiKey.NamedKey_BEGIN && !key.ToString().StartsWith("Mod", StringComparison.Ordinal)
        && !key.ToString().StartsWith("Gamepad", StringComparison.Ordinal);

    // Ctrl+1 to 9 (with or without Shift) are the camera bookmarks.
    public static bool IsReserved(KeyChord chord) => chord.Ctrl && !chord.Alt && DigitOf(chord.Key) > 0;

    public static int DigitOf(ImGuiKey key) =>
        key >= ImGuiKey._0 && key <= ImGuiKey._9 ? key - ImGuiKey._0
        : key >= ImGuiKey.Keypad0 && key <= ImGuiKey.Keypad9 ? key - ImGuiKey.Keypad0
        : -1;

    public static IReadOnlyDictionary<KeyAction, KeyChord[]> Defaults(KeyPreset preset, MovementKeys letters)
    {
        static KeyChord K(ImGuiKey key) => new(key);
        static KeyChord C(ImGuiKey key, bool shift = false) => new(key, Ctrl: true, Shift: shift);
        var map = new Dictionary<KeyAction, KeyChord[]>
        {
            [KeyAction.MoveBackward] = [K(ImGuiKey.S), K(ImGuiKey.DownArrow)],
            [KeyAction.MoveRight] = [K(ImGuiKey.D), K(ImGuiKey.RightArrow)],
            [KeyAction.SpeedDown] = [],
            [KeyAction.SpeedUp] = [],
            [KeyAction.FrameModel] = [K(ImGuiKey.F)],
            [KeyAction.ToolSelect] = [K(ImGuiKey._1), K(ImGuiKey.Keypad1)],
            [KeyAction.ToolMove] = [K(ImGuiKey._2), K(ImGuiKey.Keypad2)],
            [KeyAction.ToolRotate] = [K(ImGuiKey._3), K(ImGuiKey.Keypad3)],
            [KeyAction.ToolScale] = [K(ImGuiKey._4), K(ImGuiKey.Keypad4)],
            [KeyAction.ToolCollision] = [K(ImGuiKey._5), K(ImGuiKey.Keypad5)],
            [KeyAction.Undo] = [C(ImGuiKey.Z)],
            [KeyAction.Redo] = [C(ImGuiKey.Y), C(ImGuiKey.Z, shift: true)],
            [KeyAction.Copy] = [C(ImGuiKey.C)],
            [KeyAction.Paste] = [C(ImGuiKey.V)],
            [KeyAction.Delete] = [K(ImGuiKey.Delete)],
            [KeyAction.PutOnGround] = [K(ImGuiKey.End)],
            [KeyAction.Search] = [C(ImGuiKey.K)],
            [KeyAction.Grid] = [K(ImGuiKey.G)],
            [KeyAction.NewProject] = [C(ImGuiKey.N)],
            [KeyAction.OpenProject] = [C(ImGuiKey.O)],
            [KeyAction.Save] = [C(ImGuiKey.S)],
            [KeyAction.Export] = [C(ImGuiKey.E)],
            [KeyAction.Settings] = [C(ImGuiKey.Comma)],
            [KeyAction.Shortcuts] = [K(ImGuiKey.F1)],
            [KeyAction.Viewer] = [K(ImGuiKey.F2)],
            [KeyAction.Archives] = [K(ImGuiKey.F3)],
            [KeyAction.Peds] = [K(ImGuiKey.F4)],
            [KeyAction.Cutscenes] = [K(ImGuiKey.F5)],
            [KeyAction.Play] = [new KeyChord(ImGuiKey.P, Alt: true)],
        };
        if (preset == KeyPreset.CodeWalker)
        {
            // CodeWalker's keys (QWERTY letters). Its W / E / R tools would also move the camera here: the tools
            // keep their digits, Q selects as its "exit edit mode"; G frames the model (F moves down).
            map[KeyAction.MoveForward] = [K(ImGuiKey.W), K(ImGuiKey.UpArrow)];
            map[KeyAction.MoveLeft] = [K(ImGuiKey.A), K(ImGuiKey.LeftArrow)];
            map[KeyAction.MoveUp] = [K(ImGuiKey.R), K(ImGuiKey.PageUp)];
            map[KeyAction.MoveDown] = [K(ImGuiKey.F), K(ImGuiKey.PageDown)];
            map[KeyAction.SpeedDown] = [K(ImGuiKey.Z)];
            map[KeyAction.SpeedUp] = [K(ImGuiKey.X)];
            map[KeyAction.FrameModel] = [K(ImGuiKey.G)];
            map[KeyAction.ToolSelect] = [K(ImGuiKey.Q), K(ImGuiKey._1), K(ImGuiKey.Keypad1)];
            return map;
        }
        // Key codes follow the keyboard layout: the forward key is Z on AZERTY, W on QWERTY.
        map[KeyAction.MoveForward] = letters switch
        {
            MovementKeys.Zqsd => [K(ImGuiKey.Z), K(ImGuiKey.UpArrow)],
            MovementKeys.Wasd => [K(ImGuiKey.W), K(ImGuiKey.UpArrow)],
            _ => [K(ImGuiKey.Z), K(ImGuiKey.W), K(ImGuiKey.UpArrow)],
        };
        map[KeyAction.MoveLeft] = letters switch
        {
            MovementKeys.Zqsd => [K(ImGuiKey.Q), K(ImGuiKey.LeftArrow)],
            MovementKeys.Wasd => [K(ImGuiKey.A), K(ImGuiKey.LeftArrow)],
            _ => [K(ImGuiKey.Q), K(ImGuiKey.A), K(ImGuiKey.LeftArrow)],
        };
        map[KeyAction.MoveUp] = [K(ImGuiKey.Space), K(ImGuiKey.E), K(ImGuiKey.PageUp)];
        map[KeyAction.MoveDown] = [K(ImGuiKey.C), K(ImGuiKey.PageDown)];
        return map;
    }

    // The keys in use: the preset's, with the user's changes (action name -> keys) over them.
    public static Dictionary<KeyAction, KeyChord[]> Effective(KeyPreset preset, MovementKeys letters, IReadOnlyDictionary<string, List<string>>? changes)
    {
        var map = Defaults(preset, letters).ToDictionary(p => p.Key, p => p.Value);
        if (changes is null)
            return map;
        foreach (var (name, texts) in changes)
        {
            if (!Enum.TryParse<KeyAction>(name, out var action) || !Enum.IsDefined(action))
                continue;
            var chords = texts.Select(KeyChord.Parse).OfType<KeyChord>()
                .Where(c => !IsReserved(c) && (!IsCamera(action) || c is { Ctrl: false, Shift: false, Alt: false }))
                .Distinct().ToArray();
            map[action] = chords;
        }
        return map;
    }

    // The other action that chord already starts, if both can be acted upon at the same time.
    public static KeyAction? Conflict(IReadOnlyDictionary<KeyAction, KeyChord[]> map, KeyAction action, KeyChord chord)
    {
        foreach (var (other, chords) in map)
        {
            if (other == action || MapOnly(action) && ViewerOnly(other) || ViewerOnly(action) && MapOnly(other))
                continue;
            foreach (var c in chords)
            {
                // A camera key is held without modifiers, but also with Shift (faster).
                bool same = c.Key == chord.Key && c.Ctrl == chord.Ctrl && c.Alt == chord.Alt
                    && (c.Shift == chord.Shift || !c.Ctrl && (IsCamera(other) || IsCamera(action)));
                if (same)
                    return other;
            }
        }
        return null;
    }

    public static string KeyName(ImGuiKey key)
    {
        int digit = DigitOf(key);
        if (key >= ImGuiKey._0 && key <= ImGuiKey._9)
            return $"{digit}";
        if (digit >= 0)
            return L($"Pavé {digit}");
        return key switch
        {
            ImGuiKey.UpArrow => L("Flèche haut"),
            ImGuiKey.DownArrow => L("Flèche bas"),
            ImGuiKey.LeftArrow => L("Flèche gauche"),
            ImGuiKey.RightArrow => L("Flèche droite"),
            ImGuiKey.Space => L("Espace"),
            ImGuiKey.Delete => L("Suppr"),
            ImGuiKey.End => L("Fin"),
            ImGuiKey.Home => L("Début"),
            ImGuiKey.Insert => L("Inser"),
            ImGuiKey.PageUp => L("Page haut"),
            ImGuiKey.PageDown => L("Page bas"),
            ImGuiKey.Enter => L("Entrée"),
            ImGuiKey.Backspace => L("Retour arrière"),
            ImGuiKey.Tab => "Tab",
            ImGuiKey.Comma => ",",
            ImGuiKey.Period => ".",
            ImGuiKey.Semicolon => ";",
            ImGuiKey.Slash => "/",
            ImGuiKey.Minus => "-",
            ImGuiKey.Equal => "=",
            ImGuiKey.KeypadAdd => L("Pavé +"),
            ImGuiKey.KeypadSubtract => L("Pavé -"),
            ImGuiKey.KeypadMultiply => L("Pavé *"),
            ImGuiKey.KeypadDivide => L("Pavé /"),
            ImGuiKey.KeypadDecimal => L("Pavé ."),
            ImGuiKey.KeypadEnter => L("Pavé Entrée"),
            _ => key.ToString(),
        };
    }
}

// The keys in use, read from the settings (rebuilt when they change), and whether an action's key is pressed now.
public sealed class KeyMap(AppSettings settings)
{
    private Dictionary<KeyAction, KeyChord[]>? _map;
    private (KeyPreset, MovementKeys, int) _builtFor;
    private int _revision;

    // Call after changing settings.KeyBindings.
    public void Changed() => _revision++;

    public IReadOnlyDictionary<KeyAction, KeyChord[]> Map
    {
        get
        {
            var key = (settings.KeyPreset, settings.MovementKeys, _revision);
            if (_map is null || key != _builtFor)
            {
                _map = KeyBindings.Effective(settings.KeyPreset, settings.MovementKeys, settings.KeyBindings);
                _builtFor = key;
            }
            return _map;
        }
    }

    public KeyChord[] Chords(KeyAction action) => Map.TryGetValue(action, out var chords) ? chords : [];

    // The keys of action as shown in menus: "Ctrl+Y / Ctrl+Maj+Z"; "" if none.
    public string Text(KeyAction action) => string.Join(" / ", Chords(action).Select(c => c.ToString()));

    // The first key only, for menus.
    public string? First(KeyAction action) => Chords(action) is [var first, ..] ? first.ToString() : null;

    // One of action's keys was just pressed with exactly its modifiers (Shift also allowed on digits: the top row
    // of an AZERTY keyboard needs it).
    public bool Pressed(KeyAction action)
    {
        var io = ImGui.GetIO();
        foreach (var c in Chords(action))
        {
            if (!ImGui.IsKeyPressed(c.Key, false) || c.Ctrl != io.KeyCtrl || c.Alt != io.KeyAlt)
                continue;
            if (c.Shift == io.KeyShift || !c.Shift && !c.Ctrl && KeyBindings.DigitOf(c.Key) >= 0)
                return true;
        }
        return false;
    }

    // One of action's keys is held (camera keys: modifiers not checked).
    public bool Held(KeyAction action)
    {
        foreach (var c in Chords(action))
        {
            if (ImGui.IsKeyDown(c.Key))
                return true;
        }
        return false;
    }

    // The four move keys, for hints: "Z Q S D".
    public string MoveText() => string.Join(" ", new[] { KeyAction.MoveForward, KeyAction.MoveLeft, KeyAction.MoveBackward, KeyAction.MoveRight }
        .Select(a => First(a) ?? "-"));

    public bool Uses(KeyAction action, ImGuiKey key) => Chords(action).Any(c => c.Key == key);
}
