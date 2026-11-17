using System.Numerics;
using NodeRunner.Data.Installation;
using NodeRunner.Data.Native.Classic;

namespace NodeRunner.Tests;

// Time cycles of the first trilogy, written by hand in each game's columns.
public class ClassicTimeCycleTests
{
    private static string Line(int ambient, int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(i => i < 3 ? ambient : 10));

    [Fact]
    public void Gta3_HasOneWeatherPer24LinesAndBlendsTheHours()
    {
        var text = "// SUNNY\n" + string.Join('\n', Enumerable.Range(0, 48).Select(h => Line(h < 24 ? h * 10 : 0, 40)));
        var cycle = ClassicTimeCycle.Parse(text, ClassicGame.Gta3);

        Assert.Equal(2, cycle.Weathers.Count);
        var key = cycle.Sample(cycle.Weathers[0], 2.5f);
        Assert.Equal(25f / 255f, key.Ambient.X, 4);
        // Past 23:00, back towards midnight.
        Assert.Equal(115f / 255f, cycle.Sample(cycle.Weathers[0], 23.5f).Ambient.X, 4);
    }

    [Fact]
    public void ViceCity_ReadsTheSkyAfterItsFourAmbients()
    {
        var values = Enumerable.Repeat(0, 52).ToArray();
        values[15] = 255; // Sky top red.
        var text = string.Join('\n', Enumerable.Repeat(string.Join(' ', values), 24));
        var cycle = ClassicTimeCycle.Parse(text, ClassicGame.ViceCity);

        Assert.Equal(new Vector3(1, 0, 0), cycle.Sample(cycle.Weathers[0], 12f).SkyTop);
    }

    [Fact]
    public void SanAndreas_NamesItsWeathersAndPicksThemByArea()
    {
        string Weather(string name, int ambient) =>
            $"//////////// {name}\n//Amb Amb_Obj\n" + string.Join('\n', Enumerable.Repeat(Line(ambient, 51), 8));
        var text = Weather("SUNNY_LA", 30) + "\n" + Weather("SUNNY_SF", 60);
        var cycle = ClassicTimeCycle.Parse(text, ClassicGame.SanAndreas);

        Assert.Equal(["SUNNY_LA", "SUNNY_SF"], cycle.Weathers);
        Assert.Equal("SUNNY_LA", cycle.SunnyWeatherAt(new Vector3(2495, -1680, 13)));
        Assert.Equal("SUNNY_SF", cycle.SunnyWeatherAt(new Vector3(-2000, 300, 30)));
        Assert.Equal(60f / 255f, cycle.Sample("SUNNY_SF", 9f).Ambient.X, 4);
    }
}
