using System.Numerics;

namespace NodeRunner.Render;

// Light and atmosphere of the 3D scene: sun (or moon), ambient light from the sky and the ground, sky colours and
// distance fog.
public readonly record struct SceneLighting
{
    public Vector3 SunDirection { get; init; }
    public Vector3 SunColor { get; init; }
    public Vector3 SkyAmbient { get; init; }
    public Vector3 GroundAmbient { get; init; }

    public Vector3 Zenith { get; init; }
    public Vector3 Horizon { get; init; }
    public float SunDisc { get; init; }

    public float NightLights { get; init; }

    public float FogDistance { get; init; }

    // First trilogy pre-lit surfaces (ModelMesh.ClassicPrelit): ambient light added to their colours, blend towards
    // their night colours (0 day, 1 night), how much the sun shadows darken them.
    public Vector3 ClassicAmbient { get; init; }
    public float ClassicNight { get; init; }
    public float ClassicShadow { get; init; }

    // Studio lights of the model viewers (none on the map): a soft fill from the other side, and a light from behind
    // that outlines the silhouette.
    public Vector3 FillDirection { get; init; }
    public Vector3 FillColor { get; init; }
    public Vector3 RimDirection { get; init; }
    public Vector3 RimColor { get; init; }

    // A photo studio around a model seen from a camera: key light from keyDirection, fill on the opposite side, rim
    // light from behind and above, less ambient light than outdoors so the shapes stand out.
    public static SceneLighting Studio(Vector3 keyDirection, Vector3 cameraForward, Vector3 cameraRight) => Neutral with
    {
        SunDirection = Vector3.Normalize(keyDirection),
        SunColor = new Vector3(0.78f, 0.76f, 0.72f),
        SkyAmbient = new Vector3(0.34f, 0.35f, 0.38f),
        GroundAmbient = new Vector3(0.16f, 0.15f, 0.15f),
        FillDirection = Vector3.Normalize(-cameraForward + cameraRight * 0.9f + Vector3.UnitZ * 0.2f),
        FillColor = new Vector3(0.20f, 0.22f, 0.26f),
        RimDirection = Vector3.Normalize(cameraForward + Vector3.UnitZ * 0.6f),
        RimColor = new Vector3(0.55f, 0.55f, 0.52f),
    };

    public static SceneLighting Neutral { get; } = new()
    {
        SunDirection = Vector3.Normalize(new Vector3(0.35f, 0.25f, 0.9f)),
        SunColor = new Vector3(0.6f),
        SkyAmbient = new Vector3(0.55f),
        GroundAmbient = new Vector3(0.25f),
        Zenith = new Vector3(0.25f, 0.45f, 0.78f),
        Horizon = new Vector3(0.70f, 0.80f, 0.90f),
        SunDisc = 1f,
    };

    // Key moments, blended by the height of the sun.
    private static readonly SceneLighting Day = Neutral with
    {
        SunColor = new Vector3(0.62f, 0.60f, 0.56f),
        SkyAmbient = new Vector3(0.52f, 0.55f, 0.60f),
        GroundAmbient = new Vector3(0.26f, 0.25f, 0.23f),
        Zenith = new Vector3(0.22f, 0.42f, 0.76f),
        Horizon = new Vector3(0.68f, 0.78f, 0.90f),
    };

    private static readonly SceneLighting Golden = Neutral with
    {
        SunColor = new Vector3(0.75f, 0.48f, 0.28f),
        SkyAmbient = new Vector3(0.42f, 0.38f, 0.42f),
        GroundAmbient = new Vector3(0.20f, 0.16f, 0.14f),
        Zenith = new Vector3(0.26f, 0.32f, 0.55f),
        Horizon = new Vector3(0.95f, 0.60f, 0.36f),
    };

    private static readonly SceneLighting Night = Neutral with
    {
        SunColor = new Vector3(0.20f, 0.25f, 0.38f),
        SkyAmbient = new Vector3(0.16f, 0.19f, 0.28f),
        GroundAmbient = new Vector3(0.07f, 0.07f, 0.10f),
        Zenith = new Vector3(0.015f, 0.025f, 0.06f),
        Horizon = new Vector3(0.06f, 0.08f, 0.14f),
        SunDisc = 0f,
        NightLights = 1f,
    };

    // Lighting at hour (0 to 24): the sun rises in the east (+X) at 6:00, is high in the south (-Y) at noon and sets
    // in the west at 18:00; at night the moon lights the scene faintly, opposite the sun.
    public static SceneLighting ForHour(float hour, float fogDistance)
    {
        float angle = (hour - 6f) / 12f * MathF.PI;
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), -0.45f * MathF.Sin(angle), 0.9f * MathF.Sin(angle)));

        // Height of the sun: below -0.15 night, around 0.05 sunrise / sunset, above 0.35 full day.
        float height = sun.Z;
        var lighting = height < 0.05f
            ? Blend(Night, Golden, SmoothStep(-0.15f, 0.05f, height))
            : Blend(Golden, Day, SmoothStep(0.05f, 0.35f, height));

        // At night the moon, opposite the sun, gives the light its direction; a low sun lights from just above the horizon.
        var direction = height < -0.05f ? Vector3.Normalize(-sun with { Z = MathF.Max(-sun.Z, 0.3f) })
            : Vector3.Normalize(sun with { Z = MathF.Max(sun.Z, 0.08f) });
        return lighting with { SunDirection = direction, FogDistance = fogDistance, Sun = sun };
    }

    // Where the sun really is (below the horizon at night), for the sky; zero when not worked out.
    public Vector3 Sun { get; init; }

    // Sky worked out from the air (Atmosphere, HDR image only): the sky drawn from it, its colours given to the fog
    // (brighter towards the sun: FogSun) and to the sky mirrored by the surfaces; SunTint: the sun's colour once through
    // the air. Night keeps the colours above.
    public bool PhysicalSky { get; init; }
    public Vector3 FogSun { get; init; }
    public Vector3 SunTint { get; init; }

    public SceneLighting WithPhysicalSky()
    {
        if (!IsHdr || Sun == Vector3.Zero)
            return this;
        var scale = SkyBalance;
        var zenith = Atmosphere.Radiance(Vector3.UnitZ, Sun) * scale;
        var horizon = HorizonAround(Sun) * scale;
        var flat = new Vector2(Sun.X, Sun.Y);
        flat = flat.LengthSquared() < 1e-6f ? Vector2.UnitX : Vector2.Normalize(flat);
        var towardSun = Atmosphere.Radiance(new Vector3(flat * MathF.Cos(HorizonHeight), MathF.Sin(HorizonHeight)), Sun) * scale;
        float night = NightLights;
        return this with
        {
            PhysicalSky = true,
            Zenith = Vector3.Lerp(zenith, Zenith, night),
            Horizon = Vector3.Lerp(horizon, Horizon, night),
            FogSun = Vector3.Max(towardSun - horizon, Vector3.Zero) * (1f - night),
            SunTint = Atmosphere.Transmittance(Sun),
        };
    }

    // Sky turned grey by the weather (0 to 1), also for the sky drawn.
    public float Grey { get; init; }

    // Rain on the surfaces (0 dry to 1 soaked): darker, mirroring the sky.
    public float Wetness { get; init; }

    // Snow lying on what faces the sky (0 none to 1 covered).
    public float Snow { get; init; }

    // The weather's light (after ForHdr and WithPhysicalSky): less sun through the clouds, more light from the bright
    // grey sky, greyer colours, fog nearer.
    public SceneLighting WithWeather(CloudWeather weather)
    {
        float grey = Math.Clamp(weather.Grey, 0f, 1f), sun = Math.Clamp(weather.Sun, 0f, 1f);
        if (grey <= 0f && sun >= 1f && weather.Fog >= 1f && weather.Wet <= 0f && weather.Snow <= 0f)
            return this;
        float skyGrey = float.Lerp(Luminance(Zenith), Luminance(Horizon), 0.7f);
        Vector3 Dull(Vector3 c, float luminance) => Vector3.Lerp(c, new Vector3(luminance), grey);
        var result = this with
        {
            SunColor = SunColor * sun,
            SkyAmbient = Dull(SkyAmbient, Luminance(SkyAmbient)) * (1f + (1f - sun) * 0.35f),
            GroundAmbient = Dull(GroundAmbient, Luminance(GroundAmbient)),
            Zenith = Dull(Zenith, skyGrey),
            Horizon = Dull(Horizon, skyGrey),
            FogSun = FogSun * sun * (1f - grey),
            FogDistance = FogDistance * Math.Clamp(weather.Fog, 0.05f, 1f),
            Grey = grey,
            Wetness = Math.Clamp(weather.Wet, 0f, 1f),
            Snow = Math.Clamp(weather.Snow, 0f, 1f),
        };
        if (IsHdr)
            result = result with { Adaptation = Math.Clamp(MathF.Pow(DayKey / MathF.Max(Key(result), 1e-3f), 0.45f), 1f, 3.5f) };
        return result;
    }

    // A flash of lightning (0 none, about 1 the brightest): bluish white light from the whole sky, for an instant (the
    // eyes have no time to get used to it: Adaptation kept).
    public SceneLighting WithFlash(float flash)
    {
        if (flash <= 0f)
            return this;
        // Against the night's brighter exposure (Adaptation), so a flash looks alike at any hour.
        var light = new Vector3(0.8f, 0.85f, 1f) * (flash * (IsHdr ? 0.6f / MathF.Max(Adaptation, 1f) : 0.3f));
        return this with
        {
            SkyAmbient = SkyAmbient + light * 1.2f,
            GroundAmbient = GroundAmbient + light * 0.4f,
            Zenith = Zenith + light * 1.5f,
            Horizon = Horizon + light * 1.5f,
        };
    }

    // Height of the sky's horizon colour (radians), just above the lowest height of the sky drawn (SkyRenderer).
    private const float HorizonHeight = 0.09f;

    private static Vector3 HorizonAround(Vector3 sun)
    {
        const int Around = 8;
        var sum = Vector3.Zero;
        for (int i = 0; i < Around; i++)
        {
            float a = MathF.Tau * i / Around;
            sum += Atmosphere.Radiance(new Vector3(MathF.Cos(a) * MathF.Cos(HorizonHeight), MathF.Sin(a) * MathF.Cos(HorizonHeight), MathF.Sin(HorizonHeight)), sun);
        }
        return sum / Around;
    }

    // Worked-out sky -> HDR image, per colour: at noon, the horizon of the same hue as the colours above (the game's
    // whitish blue) and the sky overhead as bright.
    internal static Vector3 SkyBalance => _skyBalance ??= MakeSkyBalance();

    private static Vector3? _skyBalance;

    private static Vector3 MakeSkyBalance()
    {
        var noon = ForHour(13f, 0f);
        var target = noon.HdrColours();
        var horizon = HorizonAround(noon.Sun);
        var hue = target.Horizon / MathF.Max(Luminance(target.Horizon), 1e-9f) / (horizon / MathF.Max(Luminance(horizon), 1e-9f));
        var zenith = Atmosphere.Radiance(Vector3.UnitZ, noon.Sun) * hue;
        return hue * (Luminance(target.Zenith) / MathF.Max(Luminance(zenith), 1e-9f));
    }

    // The same light for the HDR scene image (SceneImage): linear colours (the values above are display colours), a
    // sun several times stronger than the sky's light around, as outdoors. The eye gets used to the dark: with less
    // light (dusk, night), the image is made brighter (Adaptation), as in the game.
    public SceneLighting ForHdr()
    {
        var hdr = HdrColours();
        return hdr with { Adaptation = Math.Clamp(MathF.Pow(DayKey / MathF.Max(Key(hdr), 1e-3f), 0.45f), 1f, 3.5f) };
    }

    private SceneLighting HdrColours() => this with
    {
        SunColor = Linear(SunColor) * HdrSun,
        SkyAmbient = Linear(SkyAmbient) * HdrAmbient,
        GroundAmbient = Linear(GroundAmbient) * HdrAmbient,
        Zenith = Linear(Zenith) * HdrSky,
        Horizon = Linear(Horizon) * HdrSky,
        FillColor = Linear(FillColor) * HdrAmbient,
        RimColor = Linear(RimColor) * HdrAmbient,
        IsHdr = true,
    };

    private const float HdrSun = 3f;
    private const float HdrAmbient = 2f;
    private const float HdrSky = 1.25f;

    // How much light there is around (HDR colours), and the same at noon.
    private static float Key(SceneLighting l) =>
        Luminance(l.SkyAmbient) + Luminance(l.SunColor) * MathF.Max(l.SunDirection.Z, 0.1f);

    private static readonly float DayKey = Key(ForHour(13f, 0f).HdrColours());

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // Brightening of the HDR image for the light of the hour (1 by day, 0 when not worked out).
    public float Adaptation { get; init; }

    // Light given off by lit windows and lamps in the HDR image, compared with a white surface in the sun.
    public const float HdrEmission = 3f;

    private static Vector3 Linear(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    // Linear colours for the HDR scene image (ForHdr).
    public bool IsHdr { get; init; }

    private static SceneLighting Blend(SceneLighting a, SceneLighting b, float t) => a with
    {
        SunColor = Vector3.Lerp(a.SunColor, b.SunColor, t),
        SkyAmbient = Vector3.Lerp(a.SkyAmbient, b.SkyAmbient, t),
        GroundAmbient = Vector3.Lerp(a.GroundAmbient, b.GroundAmbient, t),
        Zenith = Vector3.Lerp(a.Zenith, b.Zenith, t),
        Horizon = Vector3.Lerp(a.Horizon, b.Horizon, t),
        SunDisc = float.Lerp(a.SunDisc, b.SunDisc, t),
        NightLights = float.Lerp(a.NightLights, b.NightLights, t),
    };

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
