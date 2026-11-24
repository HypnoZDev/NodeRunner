using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Native.Archives;
using NodeRunner.Data.Native.Resources;

namespace NodeRunner.Data.Native.Maps;

// Map files in the XML form exported by Sollumz and CodeWalker (<CMapData>), written as a .ymap.
public static class YmapXml
{
    public const int Version = 2;

    // A complete .ymap file (RSC7). schema: descriptions read from the game's map files.
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
        if (root.Name.LocalName != "CMapData")
            throw new InvalidDataException("Balise <CMapData> introuvable : ce n'est pas un fichier de carte .ymap.");

        var document = MetaXml.Build(root, schema);
        document.Name = root.Attribute("name")?.Value is { Length: > 0 } name ? name : null;
        var (content, pageFlags) = document.Write();
        try
        {
            new YmapReader().Read(content);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"fichier écrit illisible : {ex.Message}");
        }
        uint systemFlags = ((uint)(Version >> 4) & 0xF) << 28 | pageFlags;
        uint graphicsFlags = ((uint)Version & 0xF) << 28;
        return ResourceFile.Write(new ResourceHeader(Version, systemFlags, graphicsFlags), content);
    }

    // A game file (decompressed content) written out as XML. values: names of the models and other hashes it holds.
    public static XmlConversion Export(string fileName, byte[] data, ResourceHeader header, Func<uint, string?> values) =>
        new(fileName + ".xml", MetaXml.Write(MetaDocument.Read(data, ResourcePages.Size(header.SystemFlags)), MapMetaNames.Find, values), []);
}
