namespace NodeRunner.Data.Scenarios;

/// <summary>The content of one scenario region file (e.g. vinewood.ymt).</summary>
public sealed class ScenarioRegion
{
    public required string Name { get; init; }

    public required IReadOnlyList<ScenarioPoint> Points { get; init; }
}

/// <summary>Decodes a scenario region file (.ymt in levels\gta5\scenario).</summary>
public interface IScenarioRegionReader
{
    /// <summary>Parses the decompressed content of the file (as returned by IGameFileSystem.ReadFile).</summary>
    /// <exception cref="InvalidDataException">The data is not a valid scenario region.</exception>
    ScenarioRegion Read(ReadOnlySpan<byte> data, string name);
}
