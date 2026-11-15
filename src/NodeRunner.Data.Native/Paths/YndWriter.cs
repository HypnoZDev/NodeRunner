using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Paths;

namespace NodeRunner.Data.Native.Paths;

// Builds a whole .ynd resource from an area: nodes, links, and the junctions of the nodes still there (each with its
// height map). Same layout as YndReader.
public sealed class YndWriter : IPathNodeWriter
{
    private const uint RootType = 0x406203D0;
    private const int MaxLinksPerNode = 31;

    public byte[] Write(PathNodeArea area, Func<PathNode, PathLink, int> linkLength, int version)
    {
        var builder = new ResourceBuilder();
        var root = builder.Add(YndReader.HeaderSize);
        root.U32(0x00, RootType);
        root.U32(0x04, 1);
        root.U32(0x48, 1);

        var nodes = area.Nodes;
        var links = area.Links;
        var nodeBlock = builder.Add(Math.Max(nodes.Count * YndReader.NodeSize, 16));
        var linkBlock = builder.Add(Math.Max(links.Count * YndReader.LinkSize, 16));
        builder.Pointer(root, 0x10, nodeBlock);
        root.U32(0x18, (uint)nodes.Count);
        root.U32(0x1C, (uint)area.VehicleNodeCount);
        root.U32(0x20, (uint)area.PedestrianNodeCount);
        builder.Pointer(root, 0x28, linkBlock);
        root.U32(0x30, (uint)links.Count);

        var junctions = new List<(int Node, PathJunction Junction)>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.LinkCount > MaxLinksPerNode)
                throw new InvalidDataException($"nœud n° {i} : plus de {MaxLinksPerNode} liens");
            if (node.Junction is { } junction)
                junctions.Add((i, junction));
            int o = i * YndReader.NodeSize;
            nodeBlock.U16(o + 16, node.Id.AreaId);
            nodeBlock.U16(o + 18, node.Id.NodeId);
            nodeBlock.U32(o + 20, node.StreetNameHash);
            nodeBlock.U16(o + 26, node.FirstLink);
            nodeBlock.U16(o + 28, Fixed(node.Position.X / YndReader.XyScale));
            nodeBlock.U16(o + 30, Fixed(node.Position.Y / YndReader.XyScale));
            nodeBlock.U8(o + 32, node.Flags0);
            nodeBlock.U8(o + 33, node.Flags1);
            nodeBlock.U16(o + 34, Fixed(node.Position.Z / YndReader.ZScale));
            nodeBlock.U8(o + 36, node.Flags2);
            nodeBlock.U8(o + 37, (node.LinkCount << 3) | (node.LinkCountExtra & 7));
            nodeBlock.U8(o + 38, node.Flags3);
            nodeBlock.U8(o + 39, node.Flags4);

            for (int l = 0; l < node.LinkCount; l++)
            {
                int index = node.FirstLink + l;
                if (index >= links.Count)
                    throw new InvalidDataException($"nœud n° {i} : lien hors de la liste");
                var link = links[index];
                int k = index * YndReader.LinkSize;
                linkBlock.U16(k, link.Target.AreaId);
                linkBlock.U16(k + 2, link.Target.NodeId);
                linkBlock.U8(k + 4, link.Flags0);
                linkBlock.U8(k + 5, link.Flags1);
                linkBlock.U8(k + 6, link.Flags2);
                linkBlock.U8(k + 7, Math.Clamp(linkLength(node, link), 0, 255));
            }
        }

        // The game's files point at an empty reference list when there is no junction.
        var refBlock = builder.Add(Math.Max(junctions.Count * YndReader.JunctionRefSize, 16));
        builder.Pointer(root, 0x50, refBlock);
        root.U16(0x58, junctions.Count);
        root.U16(0x5A, junctions.Count);
        root.U32(0x60, (uint)junctions.Count);
        if (junctions.Count > 0)
        {
            int heightSize = junctions.Sum(j => j.Junction.Heights.Length);
            if (heightSize > ushort.MaxValue)
                throw new InvalidDataException("hauteurs des carrefours trop grandes");
            var junctionBlock = builder.Add(junctions.Count * YndReader.JunctionSize);
            var heightBlock = heightSize > 0 ? builder.Add(heightSize) : null;
            builder.Pointer(root, 0x38, junctionBlock);
            builder.Pointer(root, 0x40, heightBlock);
            root.U32(0x64, (uint)heightSize);
            int start = 0;
            for (int j = 0; j < junctions.Count; j++)
            {
                var (node, junction) = junctions[j];
                int o = j * YndReader.JunctionSize;
                junction.Header.AsSpan(0, 8).CopyTo(junctionBlock.Data.AsSpan(o));
                junctionBlock.U16(o + 8, start);
                junctionBlock.U8(o + 10, junction.Width);
                junctionBlock.U8(o + 11, junction.Depth);
                if (heightBlock is not null)
                    junction.Heights.CopyTo(heightBlock.Data, start);
                start += junction.Heights.Length;

                int r = j * YndReader.JunctionRefSize;
                refBlock.U16(r, area.AreaId);
                refBlock.U16(r + 2, node);
                refBlock.U16(r + 4, j);
            }
        }
        return builder.Build(version);
    }

    private static int Fixed(float value) => (short)Math.Clamp(MathF.Round(value), short.MinValue, short.MaxValue);
}
