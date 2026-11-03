using System.Numerics;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Data.Native.Scenarios;

/// <summary>
/// NodeRunner's own reader for scenario region files (binary Meta .ymt, root structure CScenarioPointRegion).
/// Layout documented by the GTA V modding community (CodeWalker was used as a reference to understand it).
/// Reads the points of the region and of its clusters. Chaining graph, entity overrides and the
/// acceleration grid are not read yet.
/// </summary>
public sealed class ScenarioRegionReader : IScenarioRegionReader
{
    // Structure name hashes as they appear in the files.
    private const uint RegionStructure = 1492970064;
    private const uint PointStructure = 4103049490;
    private const uint ClusterStructure = 750308016;

    private const int RegionSize = 376;
    private const int PointSize = 64;
    private const int ClusterSize = 80;
    private const int ArrayFieldSize = 16;

    // CScenarioPointRegion fields.
    private const int RegionMyPoints = 8 + 16;  // Points (container) -> MyPoints
    private const int RegionClusters = 264;
    private const int RegionTypeNames = 280;    // LookUps -> TypeNames

    // CScenarioPointCluster fields.
    private const int ClusterMyPoints = 0 + 16; // Points (container) -> MyPoints

    // CScenarioPoint fields.
    private const int PointType = 21;
    private const int PointProbability = 25;
    private const int PointStartHour = 27;
    private const int PointEndHour = 28;
    private const int PointFlags = 36;
    private const int PointPositionAndDirection = 48;

    public ScenarioRegion Read(ReadOnlySpan<byte> data, string name)
    {
        var meta = new MetaResource(data);
        int root = meta.RootBlock;
        if (root < 0 || meta.BlockStructure(root) != RegionStructure)
            throw new InvalidDataException("Ce fichier n'est pas une région de scénarios.");
        meta.RequireStructure(RegionStructure, RegionSize, "CScenarioPointRegion");

        var points = new List<ScenarioPoint>();
        int region = meta.BlockStart(root);
        var typeNames = ReadTypeNames(meta, region + RegionTypeNames);

        ReadPoints(meta, region + RegionMyPoints, typeNames, points);

        var (clusters, clusterCount) = meta.ReadArray(region + RegionClusters, ClusterSize);
        if (clusterCount > 0)
        {
            meta.RequireStructure(ClusterStructure, ClusterSize, "CScenarioPointCluster");
            for (int i = 0; i < clusterCount; i++)
                ReadPoints(meta, clusters + i * ClusterSize + ClusterMyPoints, typeNames, points);
        }

        return new ScenarioRegion { Name = name, Points = points };
    }

    /// <summary>Points store an index into this table rather than the type hash itself.</summary>
    private static uint[] ReadTypeNames(MetaResource meta, int fieldOffset)
    {
        var (offset, count) = meta.ReadArray(fieldOffset, sizeof(uint));
        var names = new uint[count];
        for (int i = 0; i < count; i++)
            names[i] = meta.Data.UInt32(offset + i * sizeof(uint));
        return names;
    }

    private static void ReadPoints(MetaResource meta, int arrayField, uint[] typeNames, List<ScenarioPoint> points)
    {
        var (offset, count) = meta.ReadArray(arrayField, PointSize);
        if (count == 0)
            return;
        meta.RequireStructure(PointStructure, PointSize, "CScenarioPoint");

        var data = meta.Data;
        for (int i = 0; i < count; i++)
        {
            int o = offset + i * PointSize;
            int typeIndex = data.Byte(o + PointType);
            int p = o + PointPositionAndDirection;
            points.Add(new ScenarioPoint
            {
                Position = new Vector3(Float(data, p), Float(data, p + 4), Float(data, p + 8)),
                Heading = Float(data, p + 12),
                TypeHash = typeIndex < typeNames.Length ? typeNames[typeIndex] : 0,
                Probability = data.Byte(o + PointProbability),
                StartHour = data.Byte(o + PointStartHour),
                EndHour = data.Byte(o + PointEndHour),
                Flags = data.UInt32(o + PointFlags),
            });
        }
    }

    private static float Float(ResourceData data, int offset) => BitConverter.UInt32BitsToSingle(data.UInt32(offset));
}
