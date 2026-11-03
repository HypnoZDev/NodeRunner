namespace NodeRunner.Data.Scenarios;

// The content of one scenario region file (e.g. vinewood.ymt).
public sealed class ScenarioRegion
{
    public required string Name { get; init; }

    public required IReadOnlyList<ScenarioPoint> Points { get; init; }
}

// Decodes a scenario region file (.ymt in levels\gta5\scenario).
public interface IScenarioRegionReader
{
    // Parses the decompressed content of the file (as returned by IGameFileSystem.ReadFile).
    ScenarioRegion Read(ReadOnlySpan<byte> data, string name);
}
