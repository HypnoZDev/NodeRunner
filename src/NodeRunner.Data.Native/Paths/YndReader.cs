using System.Numerics;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Paths;

namespace NodeRunner.Data.Native.Paths;

// reader for path node files (.ynd). Layout documented by the GTA V modding community. Header (after the 16-byte
// resource file base): 0x10 nodes pointer, 0x18 node count, 0x1C vehicle node count, 0x20 pedestrian node count, 0x28
// links pointer, 0x30 link count, 0x38 junctions pointer, 0x40 height maps pointer, 0x48 always 1, 0x50 junction
// references pointer, 0x58 and 0x5A reference count (twice), 0x60 junction count, 0x64 height map size in bytes.
// Each junction has exactly one reference (node number, junction number), in node order.
public sealed class YndReader : IPathNodeReader
{
    internal const int HeaderSize = 0x70;
    internal const int NodeSize = 40;
    internal const int LinkSize = 8;
    internal const int JunctionSize = 12;
    internal const int JunctionRefSize = 8;

    // Node positions are stored as fixed-point integers.
    internal const float XyScale = 1f / 4f;
    internal const float ZScale = 1f / 32f;

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

        var junctions = ReadJunctions(res);

        var nodes = new PathNode[nodeCount];
        for (int i = 0; i < nodes.Length; i++)
        {
            int o = nodesOffset + i * NodeSize;
            byte linkCountFlags = res.Byte(o + 37);
            var position = new Vector3(res.Int16(o + 28) * XyScale, res.Int16(o + 30) * XyScale, res.Int16(o + 34) * ZScale);
            nodes[i] = new PathNode
            {
                Id = new PathNodeId(res.UInt16(o + 16), res.UInt16(o + 18)),
                StreetNameHash = res.UInt32(o + 20),
                FirstLink = res.UInt16(o + 26),
                Position = position,
                GamePosition = position,
                Flags0 = res.Byte(o + 32),
                Flags1 = res.Byte(o + 33),
                Flags2 = res.Byte(o + 36),
                LinkCount = linkCountFlags >> 3,
                LinkCountExtra = (byte)(linkCountFlags & 7),
                Flags3 = res.Byte(o + 38),
                Flags4 = res.Byte(o + 39),
                IsPedestrian = i >= vehicleNodeCount,
                Junction = junctions.GetValueOrDefault(i),
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

    // Junctions by node number.
    private static Dictionary<int, PathJunction> ReadJunctions(ResourceData res)
    {
        var byNode = new Dictionary<int, PathJunction>();
        int refCount = res.UInt16(0x58);
        uint junctionCount = res.UInt32(0x60), heightSize = res.UInt32(0x64);
        if (refCount == 0)
            return byNode;
        int refs = res.ResolveArray(res.UInt64(0x50), refCount, JunctionRefSize, "les références de carrefour");
        int junctions = res.ResolveArray(res.UInt64(0x38), junctionCount, JunctionSize, "les carrefours");
        int heights = heightSize == 0 ? 0 : res.ResolveArray(res.UInt64(0x40), heightSize, 1, "les hauteurs des carrefours");
        for (int i = 0; i < refCount; i++)
        {
            int r = refs + i * JunctionRefSize;
            int junction = res.UInt16(r + 4);
            if (junction >= junctionCount)
                throw new InvalidDataException($"Carrefour n° {junction} absent.");
            int o = junctions + junction * JunctionSize;
            byte width = res.Byte(o + 10), depth = res.Byte(o + 11);
            int start = res.UInt16(o + 8);
            if (start + width * depth > heightSize)
                throw new InvalidDataException($"Hauteurs du carrefour n° {junction} hors du fichier.");
            byNode[res.UInt16(r + 2)] = new PathJunction(res.Bytes(o, 8).ToArray(), width, depth,
                width * depth == 0 ? [] : res.Bytes(heights + start, width * depth).ToArray());
        }
        return byNode;
    }
}
