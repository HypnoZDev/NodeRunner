using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Paths;

namespace NodeRunner.Data.Native.Paths;

// Writes node positions, node flags and link lengths into a copy of the game's .ynd file; everything else (link
// flags, junctions) is kept to the byte. Same layout as YndReader.
public sealed class YndWriter : IPathNodeWriter
{
    public byte[] Write(ReadOnlySpan<byte> source, PathNodeArea area, Func<PathNode, PathLink, int> linkLength)
    {
        var res = new ResourceData(source);
        if (res.Length < YndReader.HeaderSize)
            throw new InvalidDataException("Fichier .ynd trop court.");
        uint nodeCount = res.UInt32(0x18);
        uint linkCount = res.UInt32(0x30);
        if (nodeCount != area.Nodes.Count || linkCount != area.Links.Count)
            throw new InvalidDataException("nœuds ou liens différents du fichier d'origine");
        int nodesOffset = res.ResolveArray(res.UInt64(0x10), nodeCount, YndReader.NodeSize, "les nœuds");
        int linksOffset = res.ResolveArray(res.UInt64(0x28), linkCount, YndReader.LinkSize, "les liens");

        var data = source.ToArray();
        for (int i = 0; i < area.Nodes.Count; i++)
        {
            var node = area.Nodes[i];
            int o = nodesOffset + i * YndReader.NodeSize;
            if (BitConverter.ToUInt16(data, o + 16) != node.Id.AreaId || BitConverter.ToUInt16(data, o + 18) != node.Id.NodeId)
                throw new InvalidDataException($"nœud n° {i} différent du fichier d'origine");
            WriteInt16(data, o + 28, node.Position.X / YndReader.XyScale);
            WriteInt16(data, o + 30, node.Position.Y / YndReader.XyScale);
            WriteInt16(data, o + 34, node.Position.Z / YndReader.ZScale);
            data[o + 32] = node.Flags0;
            data[o + 33] = node.Flags1;
            data[o + 36] = node.Flags2;
            data[o + 37] = (byte)((data[o + 37] & 0xF8) | (node.LinkCountExtra & 7));
            data[o + 38] = node.Flags3;
            data[o + 39] = node.Flags4;

            for (int l = 0; l < node.LinkCount; l++)
            {
                int index = node.FirstLink + l;
                if (index < area.Links.Count)
                    data[linksOffset + index * YndReader.LinkSize + 7] = (byte)Math.Clamp(linkLength(node, area.Links[index]), 0, 255);
            }
        }
        return data;
    }

    private static void WriteInt16(byte[] data, int offset, float value) =>
        BitConverter.TryWriteBytes(data.AsSpan(offset, 2), (short)Math.Clamp(MathF.Round(value), short.MinValue, short.MaxValue));
}
