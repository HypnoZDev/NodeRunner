using System.Globalization;
using System.Xml.Linq;
using NodeRunner.Data.Audio;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Audio;

// Writes an audio occlusion file (<proxy hash>.ymt, PSO). The structures are described by a game occlusion file.
public static class AudioOcclusionWriter
{
    private static readonly Dictionary<string, uint> Hashes =
        AudioOcclusionReader.PsoNames.ToDictionary(p => p.Value, p => p.Key);

    // Descriptions of the structures, from the game's occlusion files: a file only describes the structures it uses (no
    // door, no door structure), so files are read until all are known. Null when they are not.
    public static PsoSchema? Schema(IEnumerable<byte[]> gameFiles)
    {
        var schema = new PsoSchema();
        foreach (var file in gameFiles)
        {
            schema.Add(file);
            if (IsComplete(schema, Hashes["naOcclusionInteriorMetadata"], []))
                return schema;
        }
        return null;
    }

    private const byte StructureType = 0x0C, ArrayType = 0x0D;

    private static bool IsComplete(PsoSchema schema, uint structure, HashSet<uint> seen)
    {
        if (!seen.Add(structure))
            return true;
        if (schema.Structure(structure) is not { } info)
            return false;
        foreach (var field in info.Fields)
        {
            if (field.Type == StructureType && field.Reference != 0 && !IsComplete(schema, field.Reference, seen))
                return false;
        }
        return true;
    }

    public static byte[] Write(AudioOcclusion occlusion, PsoSchema schema)
    {
        var links = occlusion.Links.Select(Link);
        var paths = occlusion.Paths.Select(n => Item(
            V("Key", n.Key),
            E("PathNodeChildList", n.Children.Select(c => Item(V("PathParentKey", c.ParentKey), V("PortalInfoIdx", (uint)c.PortalLinkIndex))))));
        var root = E("naOcclusionInteriorMetadata", E("PortalInfoList", links), E("PathNodeList", paths));
        return PsoXml.Build(root, schema);
    }

    private static XElement Link(AudioPortalLink link)
    {
        var entities = link.Entities.Select(e => Item(
            V("LinkType", e.LinkType),
            T("MaxOcclusion", e.MaxOcclusion.ToString("R", CultureInfo.InvariantCulture)),
            V("EntityModelHashkey", e.ModelHash),
            T("IsDoor", e.IsDoor ? "true" : "false"),
            T("IsGlass", e.IsGlass ? "true" : "false")));
        return Item(
            V("InteriorProxyHash", link.InteriorProxyHash),
            V("PortalIdx", (uint)link.PortalIndex),
            V("RoomIdx", (uint)link.RoomIndex),
            V("DestInteriorHash", link.DestInteriorProxyHash),
            V("DestRoomIdx", (uint)link.DestRoomIndex),
            E("PortalEntityList", entities));
    }

    private static string Name(string field) => $"hash_{Hashes[field]:X8}";

    private static XElement E(string field, params object[] content) => new(Name(field), content);

    private static XElement Item(params object[] content) => new("Item", content);

    private static XElement V(string field, uint value) => T(field, value.ToString(CultureInfo.InvariantCulture));

    private static XElement T(string field, string value) => new(Name(field), new XAttribute("value", value));
}
