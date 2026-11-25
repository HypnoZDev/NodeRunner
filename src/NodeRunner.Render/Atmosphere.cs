using System.Numerics;

namespace NodeRunner.Render;

// Light of the sky worked out from the air (single scattering): the sunlight scattered by the air (Rayleigh: blue
// overhead) and by the haze (Mie: the bright halo around the sun), dimmed along its way (red sunsets). The same
// computation runs in the sky's shader (Source, for SkyRenderer's table); this one gives the colours of the fog and of the
// sky mirrored by the surfaces, so they match the sky drawn.
public static class Atmosphere
{
    // HLSL of the same computation, for the sky's table.
    internal const string Source = @"
static const float EarthRadius = 6360000.0f;
static const float AirRadius = 6420000.0f;
static const float RayleighHeight = 7994.0f;
static const float MieHeight = 1200.0f;
static const float MieG = 0.76f;
static const float3 RayleighScattering = float3(5.8e-6f, 13.5e-6f, 33.1e-6f);
static const float MieScattering = 8e-6f;

// Distance along the ray from p (inside the sphere) to the sphere of radius r.
float ToSphere(float3 p, float3 d, float r)
{
    float b = dot(p, d);
    float c = dot(p, p) - r * r;
    return -b + sqrt(max(b * b - c, 0.0f));
}

float3 SkyRadiance(float3 d, float3 sun)
{
    const int Steps = 16, LightSteps = 8;
    float3 origin = float3(0.0f, 0.0f, EarthRadius + 2.0f);
    float step = ToSphere(origin, d, AirRadius) / Steps;
    float opticalR = 0.0f, opticalM = 0.0f;
    float3 sumR = 0.0f, sumM = 0.0f;
    [loop] for (int i = 0; i < Steps; i++)
    {
        float3 p = origin + d * (step * (i + 0.5f));
        float h = max(length(p) - EarthRadius, 0.0f);
        float hr = exp(-h / RayleighHeight) * step, hm = exp(-h / MieHeight) * step;
        opticalR += hr;
        opticalM += hm;
        float lightLength = ToSphere(p, sun, AirRadius);
        float lightStep = lightLength / LightSteps;
        float lightR = 0.0f, lightM = 0.0f;
        bool lit = true;
        [loop] for (int j = 0; j < LightSteps; j++)
        {
            float3 q = p + sun * (lightStep * (j + 0.5f));
            float hq = length(q) - EarthRadius;
            if (hq < 0.0f) { lit = false; break; }
            lightR += exp(-hq / RayleighHeight) * lightStep;
            lightM += exp(-hq / MieHeight) * lightStep;
        }
        if (lit)
        {
            float3 tau = RayleighScattering * (opticalR + lightR) + MieScattering * 1.1f * (opticalM + lightM);
            float3 attenuation = exp(-tau);
            sumR += attenuation * hr;
            sumM += attenuation * hm;
        }
    }
    float mu = dot(d, sun);
    float phaseR = 3.0f / (16.0f * 3.14159265f) * (1.0f + mu * mu);
    float g2 = MieG * MieG;
    float phaseM = 3.0f / (8.0f * 3.14159265f) * ((1.0f - g2) * (1.0f + mu * mu)) / ((2.0f + g2) * pow(abs(1.0f + g2 - 2.0f * MieG * mu), 1.5f));
    return sumR * RayleighScattering * phaseR + sumM * MieScattering * phaseM;
}";

    private const float EarthRadius = 6360000f;
    private const float AirRadius = 6420000f;
    private const float RayleighHeight = 7994f;
    private const float MieHeight = 1200f;
    private const float MieG = 0.76f;
    private static readonly Vector3 RayleighScattering = new(5.8e-6f, 13.5e-6f, 33.1e-6f);
    private const float MieScattering = 8e-6f;

    private static float ToSphere(Vector3 p, Vector3 d, float r)
    {
        float b = Vector3.Dot(p, d);
        float c = Vector3.Dot(p, p) - r * r;
        return -b + MathF.Sqrt(MathF.Max(b * b - c, 0f));
    }

    // Light of the sky seen in direction d (unit, up = +Z) with the sun towards sun (unit), for a sun of brightness 1.
    public static Vector3 Radiance(Vector3 d, Vector3 sun)
    {
        const int Steps = 16, LightSteps = 8;
        var origin = new Vector3(0f, 0f, EarthRadius + 2f);
        float step = ToSphere(origin, d, AirRadius) / Steps;
        float opticalR = 0f, opticalM = 0f;
        Vector3 sumR = Vector3.Zero, sumM = Vector3.Zero;
        for (int i = 0; i < Steps; i++)
        {
            var p = origin + d * (step * (i + 0.5f));
            float h = MathF.Max(p.Length() - EarthRadius, 0f);
            float hr = MathF.Exp(-h / RayleighHeight) * step, hm = MathF.Exp(-h / MieHeight) * step;
            opticalR += hr;
            opticalM += hm;
            float lightStep = ToSphere(p, sun, AirRadius) / LightSteps;
            float lightR = 0f, lightM = 0f;
            bool lit = true;
            for (int j = 0; j < LightSteps; j++)
            {
                var q = p + sun * (lightStep * (j + 0.5f));
                float hq = q.Length() - EarthRadius;
                if (hq < 0f)
                {
                    lit = false;
                    break;
                }
                lightR += MathF.Exp(-hq / RayleighHeight) * lightStep;
                lightM += MathF.Exp(-hq / MieHeight) * lightStep;
            }
            if (!lit)
                continue;
            var tau = RayleighScattering * (opticalR + lightR) + new Vector3(MieScattering * 1.1f * (opticalM + lightM));
            var attenuation = new Vector3(MathF.Exp(-tau.X), MathF.Exp(-tau.Y), MathF.Exp(-tau.Z));
            sumR += attenuation * hr;
            sumM += attenuation * hm;
        }
        float mu = Vector3.Dot(d, sun);
        float phaseR = 3f / (16f * MathF.PI) * (1f + mu * mu);
        float g2 = MieG * MieG;
        float phaseM = 3f / (8f * MathF.PI) * ((1f - g2) * (1f + mu * mu)) / ((2f + g2) * MathF.Pow(MathF.Abs(1f + g2 - 2f * MieG * mu), 1.5f));
        return sumR * RayleighScattering * phaseR + sumM * MieScattering * phaseM;
    }

    // Share of the sunlight that crosses the air towards sun (unit): the colour of the sun low on the horizon.
    public static Vector3 Transmittance(Vector3 sun)
    {
        const int Steps = 16;
        var origin = new Vector3(0f, 0f, EarthRadius + 2f);
        var d = sun with { Z = MathF.Max(sun.Z, -0.02f) };
        d = Vector3.Normalize(d);
        float step = ToSphere(origin, d, AirRadius) / Steps;
        float opticalR = 0f, opticalM = 0f;
        for (int i = 0; i < Steps; i++)
        {
            float h = MathF.Max((origin + d * (step * (i + 0.5f))).Length() - EarthRadius, 0f);
            opticalR += MathF.Exp(-h / RayleighHeight) * step;
            opticalM += MathF.Exp(-h / MieHeight) * step;
        }
        var tau = RayleighScattering * opticalR + new Vector3(MieScattering * 1.1f * opticalM);
        return new Vector3(MathF.Exp(-tau.X), MathF.Exp(-tau.Y), MathF.Exp(-tau.Z));
    }
}
