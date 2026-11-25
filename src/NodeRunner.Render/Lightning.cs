namespace NodeRunner.Render;

// Lightning of a thunderstorm: now and then (one chance in two every 7 seconds), a flash flickering two or three times
// within half a second. The same seconds always give the same flashes.
public static class Lightning
{
    private const double Period = 7.0;

    // How bright the flash is at that moment (0 none, about 1 the brightest).
    public static float Flash(double seconds)
    {
        double slot = Math.Floor(seconds / Period);
        uint hash = Scramble((uint)(long)slot);
        if ((hash & 0xFF) < 128)
            return 0f;
        double start = (hash >> 8 & 0xFFFF) / 65536.0 * (Period - 1.0);
        float t = (float)(seconds - slot * Period - start);
        if (t < 0f || t > 0.8f)
            return 0f;
        bool third = (hash >> 24 & 1) != 0;
        float flash = Pulse(t, 0f, 1f) + Pulse(t, 0.13f, 0.6f) + (third ? Pulse(t, 0.32f, 0.85f) : 0f);
        return MathF.Min(flash, 1.2f);
    }

    // A sudden flash at start fading in a few hundredths of a second.
    private static float Pulse(float t, float start, float strength) =>
        t < start ? 0f : strength * MathF.Exp(-(t - start) / 0.06f);

    private static uint Scramble(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }
}
