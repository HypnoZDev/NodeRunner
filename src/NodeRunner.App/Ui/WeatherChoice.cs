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

    // A drop-down list of the weathers; true when the choice changed.
    public static bool Combo(string id, AppSettings settings)
    {
        bool changed = false;
        if (ImGui.BeginCombo(id, Name(settings.Weather)))
        {
            foreach (var (weather, label) in All)
            {
                if (ImGui.Selectable(label, settings.Weather == weather))
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
