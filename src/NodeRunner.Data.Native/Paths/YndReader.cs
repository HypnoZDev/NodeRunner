using System.Numerics;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Paths;

namespace NodeRunner.Data.Native.Paths;

/// <summary>
/// NodeRunner's own reader for path node files (.ynd).
/// Layout documented by the GTA V modding community (CodeWalker was used as a reference to understand it).
/// </summary>
/// <remarks>
/// Header (after the 16-byte resource file base):
/// 0x10 nodes pointer, 0x18 node count, 0x1C vehicle node count, 0x20 pedestrian node count,
/// 0x28 links pointer, 0x30 link count. Junction data that follows is not read yet.
/// </remarks>
public sealed class YndReader : IPathNodeReader
{
    private const int HeaderSize = 0x70;
    private const int NodeSize = 40;
    private const int LinkSize = 8;

    // Node positions are stored as fixed-point integers.
    private const float XyScale = 1f / 4f;
    private const float ZScale = 1f / 32f;

    public PathNodeArea Read(ReadOnlySpan<byte> data, int areaId)
    {
        var res = new ResourceData(data);
        if (res.Length < HeaderSize)
            throw new InvalidDataException("Fichier .ynd trop court.");

        ulong nodesPointer = res.UInt64(0x10);
        uint nodeCount = res.UInt32(0x18);
        uint vehicleNodeCount = res.UInt32(0x1C);
        uint pedNodeCount = res.UInt32(0x20);
        ulong linksPointer = res.UInt64(0x28);
        uint linkCount = res.UInt32(0x30);

        int nodesOffset = res.ResolveArray(nodesPointer, nodeCount, NodeSize, "les nœuds");
        int linksOffset = res.ResolveArray(linksPointer, linkCount, LinkSize, "les liens");

        var links = new PathLink[linkCount];
        for (int i = 0; i < links.Length; i++)
        {
            int o = linksOffset + i * LinkSize;
            links[i] = new PathLink(
                new PathNodeId(res.UInt16(o), res.UInt16(o + 2)),
                Flags0: res.Byte(o + 4),
                Flags1: res.Byte(o + 5),
                Flags2: res.Byte(o + 6),
                Length: res.Byte(o + 7));
        }

        var nodes = new PathNode[nodeCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            int o = nodesOffset + i * NodeSize;
            byte linkCountFlags = res.Byte(o + 37);
            nodes[i] = new PathNode
            {
                Id = new PathNodeId(res.UInt16(o + 16), res.UInt16(o + 18)),
                StreetNameHash = res.UInt32(o + 20),
                FirstLink = res.UInt16(o + 26),
                Position = new Vector3(res.Int16(o + 28) * XyScale, res.Int16(o + 30) * XyScale, res.Int16(o + 34) * ZScale),
                Flags0 = res.Byte(o + 32),
                Flags1 = res.Byte(o + 33),
                Flags2 = res.Byte(o + 36),
                LinkCount = linkCountFlags >> 3,
                LinkCountExtra = (byte)(linkCountFlags & 7),
                Flags3 = res.Byte(o + 38),
                Flags4 = res.Byte(o + 39),
                IsPedestrian = i >= vehicleNodeCount,
            };
        }

        return new PathNodeArea
        {
            AreaId = areaId,
            Nodes = nodes,
            Links = links,
            VehicleNodeCount = (int)vehicleNodeCount,
            PedestrianNodeCount = (int)pedNodeCount,
        };
    }
}
