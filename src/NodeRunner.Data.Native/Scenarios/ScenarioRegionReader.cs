using System.Numerics;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Data.Native.Scenarios;

// reader for scenario region files (binary Meta .ymt, root structure CScenarioPointRegion).
public sealed class ScenarioRegionReader : IScenarioRegionReader
{
    // Structure name hashes as they appear in the files.
    private const uint RegionStructure = 1492970064;
    internal const uint PointStructure = 4103049490;
    private const uint ClusterStructure = 750308016;

    private const int RegionSize = 376;
    internal const int PointSize = 64;
    internal const int ClusterSize = 80;
    private const int ArrayFieldSize = 16;

    // CScenarioPointRegion fields.
    internal const int RegionMyPoints = 8 + 16;  // Points (container) -> MyPoints
    internal const int RegionClusters = 264;
    internal const int RegionChainNodes = 96;
    internal const int RegionChainEdges = 96 + 16;
    internal const int RegionChains = 96 + 32;
    internal const int RegionGrid = 184;
    internal const int RegionGridCells = 248;
    internal const int RegionTypeNames = 280;    // LookUps -> TypeNames
    private const int RegionPedModelSets = RegionTypeNames + 16;
    private const int RegionVehicleModelSets = RegionTypeNames + 32;
    private const int RegionGroupNames = RegionTypeNames + 48;
    private const int RegionInteriorNames = RegionTypeNames + 64;
    private const int RegionRequiredMaps = RegionTypeNames + 80;

    // CScenarioPointCluster fields.
    internal const int ClusterMyPoints = 0 + 16; // Points (container) -> MyPoints

    // CScenarioPoint fields.
    internal const int PointType = 21;
    private const int PointModelSet = 22;
    private const int PointInterior = 23;
    private const int PointRequiredMap = 24;
    internal const int PointAvailability = 26;
    internal const int PointRadius = 29;
    internal const int PointTimeTillPedLeaves = 30;
    private const int PointGroup = 32;
    internal const int PointProbability = 25;
    internal const int PointStartHour = 27;
    internal const int PointEndHour = 28;
    internal const int PointFlags = 36;
    internal const int PointPositionAndDirection = 48;

    public ScenarioRegion Read(ReadOnlySpan<byte> data, string name)
    {
        var meta = new MetaResource(data);
        int region = Region(meta);
        var names = new LookUps(
            ReadTypeNames(meta, region + RegionTypeNames),
            ReadTypeNames(meta, region + RegionPedModelSets),
            ReadTypeNames(meta, region + RegionVehicleModelSets),
            ReadTypeNames(meta, region + RegionGroupNames),
            ReadTypeNames(meta, region + RegionInteriorNames),
            ReadTypeNames(meta, region + RegionRequiredMaps));

        var points = new List<ScenarioPoint>();
        foreach (int offset in PointOffsets(meta, region))
        {
            var point = ReadPoint(meta.Data, offset, names);
            point.Source = meta.Data.Bytes(offset, PointSize).ToArray();
            point.FromFile = true;
            points.Add(point);
        }
        var sizes = ClusterSizes(meta, region);
        return new ScenarioRegion
        {
            Name = name, Points = points, TypeHashes = names.Types, OwnPointCount = meta.ReadArray(region + RegionMyPoints, PointSize).Count,
            ClusterSizes = sizes,
        };
    }

    internal static int Region(MetaResource meta)
    {
        int root = meta.RootBlock;
        if (root < 0 || meta.BlockStructure(root) != RegionStructure)
            throw new InvalidDataException("Ce fichier n'est pas une région de scénarios.");
        meta.RequireStructure(RegionStructure, RegionSize, "CScenarioPointRegion");
        return meta.BlockStart(root);
    }

    // Where each point is in the content: the region's own points, then those of each cluster.
    internal static List<int> PointOffsets(MetaResource meta, int region)
    {
        var offsets = new List<int>();
        AddPoints(meta, region + RegionMyPoints, offsets);
        var (clusters, clusterCount) = meta.ReadArray(region + RegionClusters, ClusterSize);
        if (clusterCount > 0)
        {
            meta.RequireStructure(ClusterStructure, ClusterSize, "CScenarioPointCluster");
            for (int i = 0; i < clusterCount; i++)
                AddPoints(meta, clusters + i * ClusterSize + ClusterMyPoints, offsets);
        }
        return offsets;
    }

    private static void AddPoints(MetaResource meta, int arrayField, List<int> offsets)
    {
        var (offset, count) = meta.ReadArray(arrayField, PointSize);
        if (count == 0)
            return;
        meta.RequireStructure(PointStructure, PointSize, "CScenarioPoint");
        for (int i = 0; i < count; i++)
            offsets.Add(offset + i * PointSize);
    }

    // Number of points of each cluster.
    internal static List<int> ClusterSizes(MetaResource meta, int region)
    {
        var (clusters, count) = meta.ReadArray(region + RegionClusters, ClusterSize);
        var sizes = new List<int>(count);
        for (int i = 0; i < count; i++)
            sizes.Add(meta.ReadArray(clusters + i * ClusterSize + ClusterMyPoints, PointSize).Count);
        return sizes;
    }

    // The type list of the region: offset of its first hash and count.
    internal static (int Offset, int Count) TypeList(MetaResource meta, int region) => meta.ReadArray(region + RegionTypeNames, sizeof(uint));

    private static uint[] ReadTypeNames(MetaResource meta, int fieldOffset)
    {
        var (offset, count) = meta.ReadArray(fieldOffset, sizeof(uint));
        var names = new uint[count];
        for (int i = 0; i < count; i++)
            names[i] = meta.Data.UInt32(offset + i * sizeof(uint));
        return names;
    }

    // Hashes the points refer to by index.
    private sealed record LookUps(uint[] Types, uint[] PedModelSets, uint[] VehicleModelSets, uint[] Groups, uint[] Interiors, uint[] RequiredMaps);

    private static uint At(uint[] names, int index) => index < names.Length ? names[index] : 0;

    private static ScenarioPoint ReadPoint(ResourceData data, int o, LookUps names)
    {
        int p = o + PointPositionAndDirection;
        int modelSet = data.Byte(o + PointModelSet);
        return new ScenarioPoint
        {
            Position = new Vector3(Float(data, p), Float(data, p + 4), Float(data, p + 8)),
            Heading = Float(data, p + 12),
            TypeHash = At(names.Types, data.Byte(o + PointType)),
            PedModelSetHash = At(names.PedModelSets, modelSet),
            VehicleModelSetHash = At(names.VehicleModelSets, modelSet),
            GroupHash = At(names.Groups, data.UInt16(o + PointGroup)),
            InteriorHash = At(names.Interiors, data.Byte(o + PointInterior)),
            RequiredMapHash = At(names.RequiredMaps, data.Byte(o + PointRequiredMap)),
            Radius = data.Byte(o + PointRadius),
            TimeTillPedLeaves = data.Byte(o + PointTimeTillPedLeaves),
            Availability = data.Byte(o + PointAvailability),
            Probability = data.Byte(o + PointProbability),
            StartHour = data.Byte(o + PointStartHour),
            EndHour = data.Byte(o + PointEndHour),
            Flags = data.UInt32(o + PointFlags),
        };
    }

    private static float Float(ResourceData data, int offset) => BitConverter.UInt32BitsToSingle(data.UInt32(offset));
}
