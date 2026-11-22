using ImGuiNET;
using NodeRunner.App.Input;

namespace NodeRunner.Tests;

public class KeyBindingsTests
{
    [Theory]
    [InlineData(KeyPreset.NodeRunner, MovementKeys.Both)]
    [InlineData(KeyPreset.NodeRunner, MovementKeys.Zqsd)]
    [InlineData(KeyPreset.NodeRunner, MovementKeys.Wasd)]
    [InlineData(KeyPreset.CodeWalker, MovementKeys.Both)]
    public void Presets_give_every_action_and_no_key_twice(KeyPreset preset, MovementKeys letters)
    {
        var map = KeyBindings.Defaults(preset, letters);
        Assert.All(KeyBindings.All, a => Assert.True(map.ContainsKey(a), $"{a} missing"));
        foreach (var (action, chords) in map)
        {
            foreach (var chord in chords)
            {
                Assert.Null(KeyBindings.Conflict(map, action, chord));
                Assert.False(KeyBindings.IsReserved(chord));
                if (KeyBindings.IsCamera(action))
                    Assert.False(chord.Ctrl || chord.Shift || chord.Alt);
            }
        }
    }

    [Fact]
    public void Keys_are_kept_by_name_and_read_back()
    {
        var chord = new KeyChord(ImGuiKey.Z, Ctrl: true, Shift: true);
        Assert.Equal("Ctrl+Shift+Z", chord.Save());
        Assert.Equal(chord, KeyChord.Parse(chord.Save()));
        Assert.Equal(new KeyChord(ImGuiKey.Comma, Ctrl: true), KeyChord.Parse("Ctrl+Comma"));
        Assert.Null(KeyChord.Parse("Ctrl+NotAKey"));
        Assert.Null(KeyChord.Parse("Escape"));
        Assert.Null(KeyChord.Parse("Super+A"));
    }

    [Fact]
    public void User_changes_replace_the_preset_keys_of_their_action_only()
    {
        var changes = new Dictionary<string, List<string>>
        {
            ["Save"] = ["F6"],
            // Modifiers on a camera key, a bookmark key and an unknown action are dropped.
            ["MoveUp"] = ["Ctrl+R", "R"],
            ["Grid"] = ["Ctrl+_3"],
            ["NoSuchAction"] = ["A"],
        };
        var map = KeyBindings.Effective(KeyPreset.NodeRunner, MovementKeys.Zqsd, changes);
        Assert.Equal([new KeyChord(ImGuiKey.F6)], map[KeyAction.Save]);
        Assert.Equal([new KeyChord(ImGuiKey.R)], map[KeyAction.MoveUp]);
        Assert.Empty(map[KeyAction.Grid]);
        Assert.Equal([new KeyChord(ImGuiKey.Z, Ctrl: true)], map[KeyAction.Undo]);
    }

    [Fact]
    public void A_key_used_elsewhere_is_a_conflict_unless_never_active_together()
    {
        var map = KeyBindings.Defaults(KeyPreset.NodeRunner, MovementKeys.Zqsd);
        // G toggles the grid: as a camera key (held with or without Shift) it would do both.
        Assert.Equal(KeyAction.Grid, KeyBindings.Conflict(map, KeyAction.MoveUp, new KeyChord(ImGuiKey.G)));
        Assert.Equal(KeyAction.Grid, KeyBindings.Conflict(map, KeyAction.MoveUp, new KeyChord(ImGuiKey.G, Shift: true)));
        // Ctrl+Z (undo) and Ctrl+Shift+Z (redo) differ.
        Assert.Equal(KeyAction.Redo, KeyBindings.Conflict(map, KeyAction.Save, new KeyChord(ImGuiKey.Z, Ctrl: true, Shift: true)));
        Assert.Null(KeyBindings.Conflict(map, KeyAction.Save, new KeyChord(ImGuiKey.S, Ctrl: true, Shift: true)));
        // Framing the model (workshop) and the grid (map) are never active together.
        Assert.Null(KeyBindings.Conflict(map, KeyAction.FrameModel, new KeyChord(ImGuiKey.G)));
    }
}
