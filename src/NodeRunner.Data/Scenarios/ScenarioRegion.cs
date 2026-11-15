namespace NodeRunner.Data.Scenarios;

// The content of one scenario region file (e.g. vinewood.ymt).
public sealed class ScenarioRegion
{
    public required string Name { get; init; }

    private List<ScenarioPoint> _points = [];

    // The region's own points first (OwnPointCount), then those of its clusters.
    public required IReadOnlyList<ScenarioPoint> Points { get => _points; init => _points = value.ToList(); }

    public int OwnPointCount { get; set; }

    // Points added or deleted: the file is laid out again when saved (else its values are written in place).
    public bool Restructured { get; set; }

    internal void Insert(int index, ScenarioPoint point)
    {
        _points.Insert(index, point);
        OwnPointCount++;
        Renumber(index);
    }

    internal void RemoveAt(int index)
    {
        _points.RemoveAt(index);
        OwnPointCount--;
        Renumber(index);
    }

    private void Renumber(int from)
    {
        for (int i = from; i < _points.Count; i++)
            (_points[i].Region, _points[i].Index) = (this, i);
        Restructured = true;
    }

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
