using System.Globalization;
using System.Xml.Linq;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Audio;

// Reader for the audio occlusion files of interiors (<proxy hash>.ymt, PSO).
public sealed class AudioOcclusionReader : IAudioOcclusionReader
{
    // Field hashes of the game files (not the hashes of these names).
    internal static readonly Dictionary<uint, string> PsoNames = new()
    {
        [0xDE5DB4C2] = "naOcclusionInteriorMetadata",
        [0xB7E5D57C] = "PortalInfoList",
        [0x44159989] = "PathNodeList",
        [0xDF6B30EF] = "InteriorProxyHash",
        [0x3F480757] = "PortalIdx",
        [0xBAF48C55] = "RoomIdx",
        [0x036D7020] = "DestInteriorHash",
        [0x3D535EDA] = "DestRoomIdx",
        [0xBEF8BC2C] = "PortalEntityList",
        [0x6C0EB7BF] = "LinkType",
        [0xB4E6530E] = "MaxOcclusion",
        [0xE3674005] = "EntityModelHashkey",
        [0x1C4929EB] = "IsDoor",
        [0x3F33CAAD] = "IsGlass",
        [0x6098A50E] = "Key",
        [0xD27114DC] = "PathNodeChildList",
        [0xA23CDE7B] = "PathParentKey",
        [0x40A50F77] = "PortalInfoIdx",
    };

    public AudioOcclusion Read(byte[] data)
    {
        if (!PsoDocument.IsPso(data))
            throw new InvalidDataException("Ce n'est pas un fichier d'occlusion audio (PSO).");
        var root = PsoDocument.Parse(data, ["Item"], PsoNames);
        var links = new List<AudioPortalLink>();
        foreach (var item in Items(root, "PortalInfoList"))
        {
            var entities = Items(item, "PortalEntityList").Select(e => new AudioPortalEntity(
                UInt(e, "LinkType"), Float(e, "MaxOcclusion"), UInt(e, "EntityModelHashkey"), Bool(e, "IsDoor"), Bool(e, "IsGlass")))
                .ToList();
            links.Add(new AudioPortalLink(UInt(item, "InteriorProxyHash"), (int)UInt(item, "PortalIdx"), (int)UInt(item, "RoomIdx"),
                UInt(item, "DestInteriorHash"), (int)UInt(item, "DestRoomIdx"), entities));
        }
        var paths = Items(root, "PathNodeList").Select(n => new AudioPathNode(UInt(n, "Key"),
            Items(n, "PathNodeChildList").Select(c => new AudioPathChild(UInt(c, "PathParentKey"), (int)UInt(c, "PortalInfoIdx"))).ToList()))
            .ToList();
        return new AudioOcclusion(links, paths);
    }

    private static IEnumerable<XElement> Items(XElement parent, string name) => parent.Element(name)?.Elements() ?? [];

    private static string Text(XElement parent, string name) =>
        parent.Element(name)?.Value.Trim() ?? throw new InvalidDataException($"Champ {name} manquant.");

    private static uint UInt(XElement parent, string name)
    {
        string text = Text(parent, name);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return text.StartsWith('-') ? (uint)int.Parse(text, CultureInfo.InvariantCulture) : uint.Parse(text, CultureInfo.InvariantCulture);
    }

    private static float Float(XElement parent, string name) => float.Parse(Text(parent, name), CultureInfo.InvariantCulture);

    private static bool Bool(XElement parent, string name) => Text(parent, name).Equals("true", StringComparison.OrdinalIgnoreCase);
}
