using System.Diagnostics;
using System.Numerics;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Classic;
using NodeRunner.Render;

namespace NodeRunner.App.World;

// Light of the GTA III, Vice City and San Andreas maps from the game's own time cycle, each game lit as it lights
// itself: sky colours of the hour; buildings lit by their pre-lit colours plus the ambient light of the hour (GTA III,
// Vice City) or by their day colours blended into their night colours (San Andreas, whose ambient stays out of it).
internal sealed class ClassicLighting
{
    // How much the sun shadows darken the buildings in full day (the games have none: kept light).
    private const float ShadowStrength = 0.3f;

    // Shadow darkness of the time cycles in good weather (150 of 255).
    private const float GoodWeatherShadow = 150f / 255f;

    // Ambient light of the viewers (no hour there): GTA III and Vice City colours are dark without it.
    private static readonly Vector3 ViewerAmbient = new(0.35f);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private ClassicTimeKey? _shown;
    private ClassicTimeCycle? _cycle;

    public static Vector3 ViewerAmbientOf(ClassicGame game) => game == ClassicGame.SanAndreas ? Vector3.Zero : ViewerAmbient;

    // The weather of the menu as the games have it (no snow there: grey sky).
    public static ClassicSky SkyOf(Weather weather) => weather switch
    {
        Weather.ExtraSunny => ClassicSky.ExtraSunny,
        Weather.Smog => ClassicSky.Smog,
        Weather.Clouds or Weather.Clearing or Weather.Overcast or Weather.Snow => ClassicSky.Cloudy,
        Weather.Foggy => ClassicSky.Foggy,
        Weather.Rain => ClassicSky.Rainy,
        Weather.Thunder => ClassicSky.Storm,
        _ => ClassicSky.Sunny,
    };

    public SceneLighting Apply(SceneLighting lighting, ClassicTimeCycle? cycle, float hour, Vector3 camera, ClassicSky sky = ClassicSky.Sunny)
    {
        var game = cycle?.Game ?? ClassicGame.SanAndreas;
        float night = game == ClassicGame.SanAndreas ? NightBlend(hour) : 0f;
        var withoutCycle = lighting with
        {
            ClassicAmbient = ViewerAmbientOf(game),
            ClassicNight = night,
            ClassicShadow = ShadowStrength * lighting.SunDisc,
        };
        if (cycle is null || cycle.Weathers.Count == 0)
            return withoutCycle;

        // A change of weather (San Andreas: another area) fades in over a few seconds, as in the game.
        var target = cycle.Sample(cycle.WeatherAt(camera, sky), hour);
        double now = _clock.Elapsed.TotalSeconds;
        float step = (float)Math.Clamp(now - _last, 0.0, 0.5);
        _last = now;
        _shown = _shown is { } shown && ReferenceEquals(cycle, _cycle) && MathF.Abs(shown.Hour - target.Hour) < 0.05f
            ? ClassicTimeCycle.Blend(shown, target, 1f - MathF.Exp(-step * 1.5f))
            : target;
        _cycle = cycle;
        var key = _shown.Value;

        // Bad weathers: softer shadows (the game's shadow darkness, full in good weather), the sun hidden behind the
        // clouds, and the view closed in at the game's distance in rain and fog.
        float shadows = key.Shadow > 0f ? Math.Clamp(key.Shadow / GoodWeatherShadow, 0f, 1f) : 1f;
        bool closed = sky is ClassicSky.Rainy or ClassicSky.Foggy or ClassicSky.Storm;
        float sun = closed ? 0f : sky == ClassicSky.Cloudy ? 0.5f : 1f;
        float fog = closed && key.FarClip > 1f ? (lighting.FogDistance > 0f ? MathF.Min(lighting.FogDistance, key.FarClip) : key.FarClip)
            : lighting.FogDistance;
        return withoutCycle with
        {
            Zenith = key.SkyTop,
            Horizon = key.SkyBottom,
            ClassicAmbient = game == ClassicGame.SanAndreas ? Vector3.Zero : key.Ambient,
            ClassicShadow = withoutCycle.ClassicShadow * shadows * sun,
            SunDisc = lighting.SunDisc * sun,
            FogDistance = fog,
        };
    }

    // San Andreas: night colours from 21:00 to 5:00, blended in from 19:00 and out until 7:00.
    private static float NightBlend(float hour)
    {
        hour = ((hour % 24f) + 24f) % 24f;
        if (hour >= 21f || hour <= 5f)
            return 1f;
        if (hour < 7f)
            return (7f - hour) / 2f;
        if (hour > 19f)
            return (hour - 19f) / 2f;
        return 0f;
    }
}
