using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Archetype files in the XML form exported by Sollumz and CodeWalker (<CMapTypes>), written as a .ytyp.
public static class YtypXml
{
    public const int Version = 2;

    // A complete .ytyp file (RSC7). schema: descriptions read from the game's .ytyp files.
    public static byte[] Convert(XmlExport export, MetaSchema schema)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(export.Text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}");
        }
        var root = xml.Root ?? throw new InvalidDataException("XML vide.");
        if (root.Name.LocalName != "CMapTypes")
            throw new InvalidDataException("Balise <CMapTypes> introuvable : ce n'est pas un fichier de types .ytyp.");

        var document = MetaXml.Build(root, schema);
        // Most game files have no name; CodeWalker writes it as an attribute when there is one.
        document.Name = root.Attribute("name")?.Value is { Length: > 0 } name ? name : null;
        var (content, pageFlags) = document.Write();
        try
        {
            new YtypReader().Read(content);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"fichier écrit illisible : {ex.Message}");
        }
        uint systemFlags = ((uint)(Version >> 4) & 0xF) << 28 | pageFlags;
        uint graphicsFlags = ((uint)Version & 0xF) << 28;
        return ResourceFile.Write(new ResourceHeader(Version, systemFlags, graphicsFlags), content);
    }
}
