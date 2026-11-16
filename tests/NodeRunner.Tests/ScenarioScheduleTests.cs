using System.Numerics;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Tests;

public class ScenarioScheduleTests
{
    private static ScenarioPoint Point(byte start, byte end, byte availability = 0, byte probability = 0, string type = "WORLD_HUMAN_SMOKING") => new()
    {
        Position = Vector3.Zero,
        StartHour = start,
        EndHour = end,
        Availability = availability,
        Probability = probability,
        TypeHash = Joaat.Hash(type),
        TypeName = type,
    };

    [Fact]
    public void HoursWrapPastMidnightAndSameMeansAllDay()
    {
        Assert.True(ScenarioSchedule.IsOpenAt(Point(0, 24), 3));
        Assert.True(ScenarioSchedule.IsOpenAt(Point(5, 5), 17));
        Assert.True(ScenarioSchedule.IsOpenAt(Point(7, 11), 7));
        Assert.False(ScenarioSchedule.IsOpenAt(Point(7, 11), 11));
        Assert.True(ScenarioSchedule.IsOpenAt(Point(20, 6), 23));
        Assert.True(ScenarioSchedule.IsOpenAt(Point(20, 6), 2));
        Assert.False(ScenarioSchedule.IsOpenAt(Point(20, 6), 12));
    }

    [Fact]
    public void AvailabilityFollowsTheMapVersion()
    {
        Assert.True(ScenarioSchedule.IsAvailable(Point(0, 0, 0), online: true));
        Assert.True(ScenarioSchedule.IsAvailable(Point(0, 0, 1), online: true));
        Assert.False(ScenarioSchedule.IsAvailable(Point(0, 0, 1), online: false));
        Assert.False(ScenarioSchedule.IsAvailable(Point(0, 0, 2), online: true));
    }

    [Fact]
    public void ProbabilityComesFromThePointThenItsType()
    {
        var files = new GameContentTests.FakeGameFiles();
        files.SetContent(@"update\update.rpf\common\data\ai\scenarios.meta", """
            <CScenarioInfoManager><Scenarios>
              <Item type="CScenarioPlayAnimsInfo"><Name>WORLD_HUMAN_SMOKING</Name><Models ref="NULL" /><SpawnProbability value="0.250000" />
                <ConditionalAnimsGroup><Name>X</Name><ConditionalAnims><Item><Name>Y</Name><Probability value="1.0" /></Item></ConditionalAnims></ConditionalAnimsGroup>
              </Item>
              <Item type="CScenarioPlayAnimsInfo"><Name>WORLD_HUMAN_JOG</Name></Item>
            </Scenarios></CScenarioInfoManager>
            """);
        var scenarios = ScenarioSet.Load(files, new NoRegions());
        Assert.Equal(0.25f, ScenarioSchedule.Probability(Point(0, 0), scenarios));
        Assert.Equal(1f, ScenarioSchedule.Probability(Point(0, 0, type: "WORLD_HUMAN_JOG"), scenarios));
        Assert.Equal(0.2f, ScenarioSchedule.Probability(Point(0, 0, probability: 20), scenarios));

        // About a quarter of the points are used, always the same ones for a given hour.
        var region = new ScenarioRegion { Name = "test", Points = Enumerable.Range(0, 2000).Select(_ => Point(0, 0)).ToList() };
        region.OwnPointCount = region.Points.Count;
        foreach (var (p, i) in region.Points.Select((p, i) => (p, i)))
            (p.Region, p.Index) = (region, i);
        int used = region.Points.Count(p => ScenarioSchedule.IsUsed(p, scenarios, 12, online: true));
        Assert.InRange(used, 400, 600);
        Assert.Equal(used, region.Points.Count(p => ScenarioSchedule.IsUsed(p, scenarios, 12, online: true)));
        Assert.NotEqual(region.Points.Select(p => ScenarioSchedule.IsUsed(p, scenarios, 12, true)),
            region.Points.Select(p => ScenarioSchedule.IsUsed(p, scenarios, 13, true)));
        Assert.False(ScenarioSchedule.IsUsed(Point(0, 0, type: "Walk"), scenarios, 12, true));
    }

    private sealed class NoRegions : IScenarioRegionReader
    {
        public ScenarioRegion Read(ReadOnlySpan<byte> data, string name) => throw new InvalidDataException();
    }
}
