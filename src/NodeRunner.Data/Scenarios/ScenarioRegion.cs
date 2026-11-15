namespace NodeRunner.Data.Scenarios;

// The content of one scenario region file (e.g. vinewood.ymt).
public sealed class ScenarioRegion
{
    public required string Name { get; init; }

    public required IReadOnlyList<ScenarioPoint> Points { get; init; }

    // Path of the file in the game's archives.
    public string? Path { get; set; }

    // The scenario types the file lists (its points refer to them by index): those a point of this file can take.
    public IReadOnlyList<uint> TypeHashes { get; init; } = [];

    // Content of the user's saved file the region was read from (null: the game's).
    public byte[]? SavedContent { get; set; }
}

// Decodes a scenario region file (.ymt in levels\gta5\scenario).
public interface IScenarioRegionReader
{
    // Parses the decompressed content of the file (as returned by IGameFileSystem.ReadFile).
    ScenarioRegion Read(ReadOnlySpan<byte> data, string name);
}
