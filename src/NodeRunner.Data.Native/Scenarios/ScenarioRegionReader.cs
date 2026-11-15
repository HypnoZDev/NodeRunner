using System.Numerics;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Data.Native.Scenarios;

// reader for scenario region files (binary Meta .ymt, root structure CScenarioPointRegion).
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
    private const int RegionPedModelSets = RegionTypeNames + 16;
    private const int RegionVehicleModelSets = RegionTypeNames + 32;
    private const int RegionGroupNames = RegionTypeNames + 48;
    private const int RegionInteriorNames = RegionTypeNames + 64;
    private const int RegionRequiredMaps = RegionTypeNames + 80;

    // CScenarioPointCluster fields.
    private const int ClusterMyPoints = 0 + 16; // Points (container) -> MyPoints

    // CScenarioPoint fields.
    private const int PointType = 21;
    private const int PointModelSet = 22;
    private const int PointInterior = 23;
    private const int PointRequiredMap = 24;
    private const int PointAvailability = 26;
    private const int PointRadius = 29;
    private const int PointTimeTillPedLeaves = 30;
    private const int PointGroup = 32;
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
        var names = new LookUps(
            ReadTypeNames(meta, region + RegionTypeNames),
            ReadTypeNames(meta, region + RegionPedModelSets),
            ReadTypeNames(meta, region + RegionVehicleModelSets),
            ReadTypeNames(meta, region + RegionGroupNames),
            ReadTypeNames(meta, region + RegionInteriorNames),
            ReadTypeNames(meta, region + RegionRequiredMaps));

        ReadPoints(meta, region + RegionMyPoints, names, points);

        var (clusters, clusterCount) = meta.ReadArray(region + RegionClusters, ClusterSize);
        if (clusterCount > 0)
        {
            meta.RequireStructure(ClusterStructure, ClusterSize, "CScenarioPointCluster");
            for (int i = 0; i < clusterCount; i++)
                ReadPoints(meta, clusters + i * ClusterSize + ClusterMyPoints, names, points);
        }

        return new ScenarioRegion { Name = name, Points = points };
    }

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

    private static void ReadPoints(MetaResource meta, int arrayField, LookUps names, List<ScenarioPoint> points)
    {
        var (offset, count) = meta.ReadArray(arrayField, PointSize);
        if (count == 0)
            return;
        meta.RequireStructure(PointStructure, PointSize, "CScenarioPoint");

        var data = meta.Data;
        for (int i = 0; i < count; i++)
        {
            int o = offset + i * PointSize;
            int p = o + PointPositionAndDirection;
            uint type = At(names.Types, data.Byte(o + PointType));
            int modelSet = data.Byte(o + PointModelSet);
            points.Add(new ScenarioPoint
            {
                Position = new Vector3(Float(data, p), Float(data, p + 4), Float(data, p + 8)),
                Heading = Float(data, p + 12),
                TypeHash = type,
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
            });
        }
    }

    private static float Float(ResourceData data, int offset) => BitConverter.UInt32BitsToSingle(data.UInt32(offset));
}
