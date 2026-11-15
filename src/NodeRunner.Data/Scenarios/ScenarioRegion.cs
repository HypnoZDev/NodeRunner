namespace NodeRunner.Data.Scenarios;

// The content of one scenario region file (e.g. vinewood.ymt).
public sealed class ScenarioRegion
{
    public required string Name { get; init; }

    private List<ScenarioPoint> _points = [];

    // The region's own points first (OwnPointCount), then those of its clusters.
    public required IReadOnlyList<ScenarioPoint> Points { get => _points; init => _points = value.ToList(); }

    public int OwnPointCount { get; set; }

    // Number of points of each cluster, in order, after the region's own points (0 once all of a cluster's are deleted:
    // the cluster is then left out of the file).
    public List<int> ClusterSizes { get; init; } = [];

    // Which part a point index falls in: -1 = the region's own points, else the cluster's number.
    public int GroupOf(int index)
    {
        int end = OwnPointCount;
        if (index < end)
            return -1;
        for (int c = 0; c < ClusterSizes.Count; c++)
        {
            end += ClusterSizes[c];
            if (index < end)
                return c;
        }
        return ClusterSizes.Count - 1;
    }

    // Points added or deleted: the file is laid out again when saved (else its values are written in place).
    public bool Restructured { get; set; }

    // group: -1 = among the region's own points, else in that cluster.
    internal void Insert(int index, ScenarioPoint point, int group)
    {
        _points.Insert(index, point);
        if (group < 0)
            OwnPointCount++;
        else
            ClusterSizes[group]++;
        Renumber(index);
    }

    // The group the point was in.
    internal int RemoveAt(int index)
    {
        int group = GroupOf(index);
        _points.RemoveAt(index);
        if (group < 0)
            OwnPointCount--;
        else
            ClusterSizes[group]--;
        Renumber(index);
        return group;
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
