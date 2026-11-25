using ImGuiNET;
using NodeRunner.App.Configuration;
using NodeRunner.Render;

namespace NodeRunner.App.Ui;

// The game's weathers as offered in the interface (time menu of the 3D view, display settings).
internal static class WeatherChoice
{
    public static (Weather Weather, string Label)[] All =>
    [
        (Weather.ExtraSunny, L("Grand soleil")),
        (Weather.Clear, L("Beau temps")),
        (Weather.Clouds, L("Nuageux")),
        (Weather.Clearing, L("Éclaircies")),
        (Weather.Overcast, L("Couvert")),
        (Weather.Smog, L("Smog")),
        (Weather.Foggy, L("Brouillard épais")),
        (Weather.Rain, L("Pluie")),
        (Weather.Thunder, L("Orage")),
        (Weather.Snow, L("Neige")),
    ];

    public static string Name(Weather weather) => All.FirstOrDefault(w => w.Weather == weather).Label ?? weather.ToString();

    // The weathers the game has (GTA V: all of them).
    public static (Weather Weather, string Label)[] For(GameKind game)
    {
        Weather[]? kept = game switch
        {
            GameKind.Gta4 => [Weather.ExtraSunny, Weather.Clear, Weather.Clouds, Weather.Foggy, Weather.Rain, Weather.Thunder],
            GameKind.Gta3 => [Weather.Clear, Weather.Clouds, Weather.Foggy, Weather.Rain],
            GameKind.ViceCity => [Weather.ExtraSunny, Weather.Clear, Weather.Clouds, Weather.Foggy, Weather.Rain, Weather.Thunder],
            GameKind.SanAndreas => [Weather.ExtraSunny, Weather.Clear, Weather.Clouds, Weather.Smog, Weather.Foggy, Weather.Rain],
            _ => null,
        };
        return kept is null ? All : All.Where(w => kept.Contains(w.Weather)).ToArray();
    }

    // The weather shown in the game: the one chosen, or good weather when the game does not have it (the choice is
    // kept for GTA V).
    public static Weather Shown(AppSettings settings, GameKind game) =>
        For(game).Any(w => w.Weather == settings.Weather) ? settings.Weather : Weather.Clear;

    // A drop-down list of the game's weathers; true when the choice changed.
    public static bool Combo(string id, AppSettings settings, GameKind game)
    {
        bool changed = false;
        var shown = Shown(settings, game);
        if (ImGui.BeginCombo(id, Name(shown)))
        {
            foreach (var (weather, label) in For(game))
            {
                if (ImGui.Selectable(label, shown == weather))
                {
                    settings.Weather = weather;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}
