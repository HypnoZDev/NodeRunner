using System.Buffers.Binary;
using System.Numerics;

namespace NodeRunner.Data.Native.Gta4;

// A path node of GTA IV. Vehicle nodes come first in each area, then pedestrian nodes.
public readonly record struct Gta4PathNode(ushort Area, ushort Id, Vector3 Position, bool IsVehicle, bool IsBoat, int FirstLink,
    int LinkCount);

public readonly record struct Gta4PathLink(ushort Area, ushort Node, int LanesForward, int LanesBackward);

public sealed record Gta4PathArea(int Index, Gta4PathNode[] Nodes, Gta4PathLink[] Links)
{
    public ReadOnlySpan<Gta4PathLink> LinksOf(in Gta4PathNode node) => Links.AsSpan(node.FirstLink, node.LinkCount);
}

// "nodesN.nod" (one per area of the 8 x 8 grid): header (node count, vehicle node count, pedestrian node count, link
// count), 32-byte nodes, 8-byte links. Node: area and id at 8, first link at 18, position as int16 at 20 (x and y
// in 1/8 m, height in 1/64 m), boat
// flag in bit 1 of byte 31. Link: target area and node, then lanes (forward in the low 3 bits of byte 5, backward in
// the next 3).
public static class Gta4PathNodes
{
    public const string Archive = @"pc\data\cdimages\paths.img";
    public const int AreaCount = 64;
    private const int HeaderSize = 16, NodeSize = 32, LinkSize = 8;

    public static Gta4PathArea Read(ReadOnlySpan<byte> data, int index)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("fichier de pathnodes trop court");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data);
        int vehicles = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int linkCount = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        long linkBase = HeaderSize + (long)count * NodeSize;
        if (count < 0 || linkCount < 0 || linkBase + (long)linkCount * LinkSize > data.Length)
            throw new InvalidDataException("fichier de pathnodes invalide");

        var links = new Gta4PathLink[linkCount];
        for (int i = 0; i < linkCount; i++)
        {
            var l = data.Slice((int)linkBase + i * LinkSize, LinkSize);
            links[i] = new Gta4PathLink(BinaryPrimitives.ReadUInt16LittleEndian(l), BinaryPrimitives.ReadUInt16LittleEndian(l[2..]),
                l[5] & 7, (l[5] >> 3) & 7);
        }

        var nodes = new Gta4PathNode[count];
        for (int i = 0; i < count; i++)
        {
            var n = data.Slice(HeaderSize + i * NodeSize, NodeSize);
            int first = BinaryPrimitives.ReadUInt16LittleEndian(n[18..]);
            int next = i + 1 < count ? BinaryPrimitives.ReadUInt16LittleEndian(data[(HeaderSize + (i + 1) * NodeSize + 18)..]) : linkCount;
            first = Math.Min(first, linkCount);
            var position = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(n[20..]) / 8f, BinaryPrimitives.ReadInt16LittleEndian(n[22..]) / 8f,
                BinaryPrimitives.ReadInt16LittleEndian(n[24..]) / 64f);
            nodes[i] = new Gta4PathNode(BinaryPrimitives.ReadUInt16LittleEndian(n[8..]), BinaryPrimitives.ReadUInt16LittleEndian(n[10..]),
                position, i < vehicles, (n[31] & 2) != 0, first, Math.Clamp(next - first, 0, linkCount - first));
        }
        return new Gta4PathArea(index, nodes, links);
    }

    // Every area of the base game that has nodes; unreadable areas are skipped.
    public static List<Gta4PathArea> ReadAll(Gta4FileSystem files)
    {
        var areas = new List<Gta4PathArea>();
        for (int i = 0; i < AreaCount; i++)
        {
            try
            {
                if (files.ReadFile($@"{Archive}\nodes{i}.nod") is { Length: > HeaderSize } data)
                    areas.Add(Read(data, i));
            }
            catch (InvalidDataException)
            {
                // A damaged (modded) area is left out.
            }
        }
        return areas;
    }
}
