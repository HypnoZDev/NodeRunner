using System.Globalization;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Textures;

namespace NodeRunner.Data.Native.Textures;

// Texture dictionaries in the XML form exported by Sollumz (and CodeWalker): <TextureDictionary><Item>... with one .dds
// per texture in a folder named after the file ("name.ytd.xml" + "name/texture.dds").
public static class YtdXml
{
    internal static readonly string[] UsageNames =
    [
        "UNKNOWN", "DEFAULT", "TERRAIN", "CLOUDDENSITY", "CLOUDNORMAL", "CABLE", "FENCE", "ENVEFF", "SCRIPT", "WATERFLOW",
        "WATERFOAM", "WATERFOG", "WATEROCEAN", "WATER", "FOAMOPACITY", "FOAM", "DIFFUSEMIPSHARPEN", "DIFFUSEDETAIL",
        "DIFFUSEDARK", "DIFFUSEALPHAOPAQUE", "DIFFUSE", "DETAIL", "NORMAL", "SPECULAR", "EMISSIVE", "TINTPALETTE",
        "SKIPPROCESSING", "DONOTOPTIMIZE", "TEST",
    ];

    internal static readonly string[] UsageFlagNames =
    [
        "NOT_HALF", "HD_SPLIT", "X2", "X4", "Y4", "X8", "X16", "X32", "X64", "Y64", "X128", "X256", "X512", "Y512",
        "X1024", "Y1024", "X2048", "Y2048", "EMBEDDEDSCRIPTRT", "UNK19", "UNK20", "UNK21", "FLAG_FULL", "MAPS_HALF", "UNK24",
    ];

    // A complete .ytd file (RSC7).
    public static byte[] Convert(XmlExport export)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(export.Text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}");
        }
        var root = document.Root ?? throw new InvalidDataException("XML vide.");
        if (root.Name.LocalName != "TextureDictionary")
            throw new InvalidDataException("Balise <TextureDictionary> introuvable : ce n'est pas un dictionnaire de textures.");
        var textures = ReadTextures(root, export);
        if (textures.Count == 0)
            throw new InvalidDataException("Le dictionnaire ne contient aucune texture.");
        return YtdBuilder.Build(textures);
    }

    // The <Item> textures of a dictionary element, pixels read from their .dds file.
    internal static List<XmlTexture> ReadTextures(XElement dictionary, XmlExport export)
    {
        var textures = new List<XmlTexture>();
        var names = new HashSet<uint>();
        foreach (var item in dictionary.Elements("Item"))
        {
            string name = item.Element("Name")?.Value.Trim() ?? "";
            if (name.Length == 0)
                throw new InvalidDataException("Texture sans nom.");
            string file = item.Element("FileName")?.Value.Trim() is { Length: > 0 } f ? f : name + ".dds";
            byte[] dds = ReadDds(export, file)
                ?? throw new InvalidDataException($"Texture {name} : image {export.BaseName}/{file} introuvable à côté du XML.");
            var data = DdsFile.Read(dds, name);
            if (!names.Add(data.NameHash))
                throw new InvalidDataException($"Texture {name} présente deux fois.");

            uint usage = Enum(item.Element("Usage")?.Value, UsageNames, "Usage");
            if (usage > 0x1F)
                throw new InvalidDataException($"Texture {name} : Usage {usage} invalide.");
            textures.Add(new XmlTexture
            {
                Data = data,
                Unknown32 = (ushort)Value(item, "Unk32", 128),
                UsageData = usage | Flags(item.Element("UsageFlags")?.Value, name) << 5,
                ExtraFlags = (uint)Value(item, "ExtraFlags", 0),
            });
        }
        return textures;
    }

    // Sollumz and CodeWalker put the images in the folder named after the file; some people move them next to it.
    private static byte[]? ReadDds(XmlExport export, string file)
    {
        if (Path.IsPathRooted(file) || file.Split('/', '\\').Contains(".."))
            return null;
        return export.ReadFile(export.BaseName + "/" + file) ?? export.ReadFile(file);
    }

    private static uint Enum(string? text, string[] names, string what)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number))
            return number;
        int index = Array.IndexOf(names, text.ToUpperInvariant());
        return index >= 0 ? (uint)index : throw new InvalidDataException($"{what} inconnu : « {text} ».");
    }

    private static uint Flags(string? text, string name)
    {
        uint flags = 0;
        foreach (var part in (text ?? "").Split([',', ' ', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number))
                flags |= number;
            else if (Array.IndexOf(UsageFlagNames, part.ToUpperInvariant()) is >= 0 and var bit)
                flags |= 1u << bit;
            else
                throw new InvalidDataException($"Texture {name} : UsageFlags inconnu « {part} ».");
        }
        if (flags >= 1u << 27)
            throw new InvalidDataException($"Texture {name} : UsageFlags invalides.");
        return flags;
    }

    private static long Value(XElement item, string element, long fallback) =>
        item.Element(element)?.Attribute("value") is { } a && long.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v : fallback;
}

internal sealed class XmlTexture
{
    public required TextureData Data;
    public ushort Unknown32;
    public uint UsageData;
    public uint ExtraFlags;
}
