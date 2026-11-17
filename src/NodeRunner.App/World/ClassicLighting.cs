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

    // Ambient light of the viewers (no hour there): GTA III and Vice City colours are dark without it.
    private static readonly Vector3 ViewerAmbient = new(0.35f);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private ClassicTimeKey? _shown;
    private ClassicTimeCycle? _cycle;

    public static Vector3 ViewerAmbientOf(ClassicGame game) => game == ClassicGame.SanAndreas ? Vector3.Zero : ViewerAmbient;

    public SceneLighting Apply(SceneLighting lighting, ClassicTimeCycle? cycle, float hour, Vector3 camera)
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
        var target = cycle.Sample(cycle.SunnyWeatherAt(camera), hour);
        double now = _clock.Elapsed.TotalSeconds;
        float step = (float)Math.Clamp(now - _last, 0.0, 0.5);
        _last = now;
        _shown = _shown is { } shown && ReferenceEquals(cycle, _cycle) && MathF.Abs(shown.Hour - target.Hour) < 0.05f
            ? ClassicTimeCycle.Blend(shown, target, 1f - MathF.Exp(-step * 1.5f))
            : target;
        _cycle = cycle;
        var key = _shown.Value;

        return withoutCycle with
        {
            Zenith = key.SkyTop,
            Horizon = key.SkyBottom,
            ClassicAmbient = game == ClassicGame.SanAndreas ? Vector3.Zero : key.Ambient,
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
