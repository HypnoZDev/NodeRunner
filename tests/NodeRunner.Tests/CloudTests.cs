using NodeRunner.Render;

namespace NodeRunner.Tests;

public class CloudTests
{
    [Fact]
    public void CloudShapes_WrapAroundWithoutASeam()
    {
        var shape = CloudNoise.Shape();
        const int n = CloudNoise.ShapeSize;
        Assert.Equal(n * n * n * 2, shape.Length);
        int Value(int x, int y, int z, int channel) => shape[((z * n + y) * n + x) * 2 + channel];

        for (int channel = 0; channel < 2; channel++)
        {
            Assert.True(Enumerable.Range(0, n * n * n).Select(i => shape[i * 2 + channel]).Distinct().Count() > 60);
            // From the last column to the first, the values change no more than between two columns inside.
            double Step(int x0, int x1)
            {
                double sum = 0;
                for (int z = 0; z < n; z++)
                    for (int y = 0; y < n; y++)
                        sum += Math.Abs(Value(x0, y, z, channel) - Value(x1, y, z, channel));
                return sum / (n * n);
            }
            double inside = Enumerable.Range(0, n - 1).Average(x => Step(x, x + 1));
            Assert.True(Step(n - 1, 0) < inside * 1.5 + 1, $"channel {channel}: seam {Step(n - 1, 0):0.0}, inside {inside:0.0}");
        }
    }

    [Fact]
    public void BadWeather_DimsTheSunAndBringsTheFogNearer()
    {
        var clear = SceneLighting.ForHour(13f, 4000f).ForHdr();
        var storm = clear.WithWeather(CloudWeather.For(Weather.Thunder));

        Assert.Equal(clear, clear.WithWeather(CloudWeather.For(Weather.Clear)));
        Assert.True(storm.SunColor.Length() < clear.SunColor.Length() * 0.5f);
        Assert.True(storm.FogDistance < clear.FogDistance);
        Assert.True(storm.Grey > 0.5f);
        Assert.True(CloudWeather.For(Weather.Thunder).Coverage > CloudWeather.For(Weather.ExtraSunny).Coverage);
    }

    [Fact]
    public void Lightning_FlashesNowAndThen()
    {
        var flashes = Enumerable.Range(0, 600 * 50).Select(i => Lightning.Flash(i / 50.0)).ToArray();
        Assert.All(flashes, f => Assert.InRange(f, 0f, 1.2f));
        // About one flash in two periods of 7 s, each lasting well under a second.
        int starts = flashes.Where((f, i) => f > 0.3f && (i == 0 || flashes[i - 1] <= 0f)).Count();
        Assert.InRange(starts, 25, 80);
        Assert.InRange(flashes.Count(f => f > 0.05f) / 50.0, 2.0, 60.0);
        Assert.Equal(Lightning.Flash(123.45), Lightning.Flash(123.45));

        var storm = SceneLighting.ForHour(13f, 4000f).WithWeather(CloudWeather.For(Weather.Thunder));
        var lit = storm.WithFlash(1f);
        Assert.True(lit.SkyAmbient.X > storm.SkyAmbient.X && lit.Zenith.Z > storm.Zenith.Z);
        Assert.Equal(storm, storm.WithFlash(0f));
    }
}
