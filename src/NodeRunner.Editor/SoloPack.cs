using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace NodeRunner.Editor;

// A single-player DLC pack (dlcpacks\<name>\dlc.rpf) holding the same files as the FiveM export: setup2.xml and
// content.xml at the root, the streamed files in one archive marked "overlay" (its files replace the game's files of the
// same name, as the game's own map patches do), the interiors' audio settings beside it.
public static class SoloPack
{
    public const string ArchiveName = "dlc.rpf";
    public const string SetupFileName = "setup2.xml";
    public const string ContentFileName = "content.xml";

    private const string AudioGameDataExtension = "151.rel";

    // Pack names: those of FiveM resources without '-' (the name also makes the pack's device name).
    public static string ToPackName(string resourceName)
    {
        string name = FiveMResource.ToValidName(resourceName).Replace('-', '_');
        return name.Length > 0 ? name : FiveMResource.DefaultName;
    }

    public static string DeviceName(string name) => "dlc_" + name;

    // Inside dlc.rpf.
    public static string MapArchivePath(string name) => $"x64/levels/gta5/{name}.rpf";

    public static string AudioGameDataPath(string name) => $"x64/audio/{name}_game.dat{AudioGameDataExtension}";

    // The line dlclist.xml needs for the pack to be loaded.
    public static string ListEntry(string name) => $"dlcpacks:/{name}/";

    // order: after every pack of the game, so that the pack's files win.
    public static string BuildSetup(string name, int order, DateTime time) => Serialize(new XElement("SSetupData",
        new XElement("deviceName", DeviceName(name)),
        new XElement("datFile", ContentFileName),
        new XElement("timeStamp", time.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
        new XElement("nameHash", name),
        new XElement("contentChangeSets"),
        new XElement("contentChangeSetGroups",
            new XElement("Item",
                new XElement("NameHash", "GROUP_STARTUP"),
                new XElement("ContentChangeSets", new XElement("Item", ChangeSetName(name))))),
        new XElement("startupScript"),
        new XElement("scriptCallstackSize", new XAttribute("value", 0)),
        new XElement("type", "EXTRACONTENT_COMPAT_PACK"),
        new XElement("order", new XAttribute("value", order)),
        new XElement("minorOrder", new XAttribute("value", 0)),
        new XElement("isLevelPack", new XAttribute("value", false)),
        new XElement("dependencyPackHash"),
        new XElement("requiredVersion"),
        new XElement("subPackCount", new XAttribute("value", 0))));

    // newTypeFiles: .ytyp files that are not game files (made by the user), declared to the game as the FiveM export does.
    public static string BuildContent(string name, IEnumerable<string> newTypeFiles, bool hasAudioGameData)
    {
        string device = DeviceName(name) + ":/%PLATFORM%/";
        string mapArchive = device + MapArchivePath(name)["x64/".Length..];
        var files = new List<XElement>
        {
            DataFile(mapArchive, "RPF_FILE", overlay: true, persistent: true, new XElement("contents", "CONTENTS_DLC_MAP_DATA")),
        };
        foreach (var type in newTypeFiles.Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
            files.Add(DataFile($"{device}levels/gta5/{type.ToLowerInvariant()}.ityp", "DLC_ITYP_REQUEST", overlay: false, persistent: false));
        if (hasAudioGameData)
        {
            // The game adds "151.rel" to the declared name.
            string audio = device + AudioGameDataPath(name)["x64/".Length..^AudioGameDataExtension.Length];
            files.Add(DataFile(audio, "AUDIO_GAMEDATA", overlay: false, persistent: false));
        }

        return Serialize(new XElement("CDataFileMgr__ContentsOfDataFileXml",
            new XElement("disabledFiles"),
            new XElement("includedXmlFiles"),
            new XElement("includedDataFiles"),
            new XElement("dataFiles", files),
            new XElement("contentChangeSets",
                new XElement("Item",
                    new XElement("changeSetName", ChangeSetName(name)),
                    new XElement("filesToInvalidate"),
                    new XElement("filesToDisable"),
                    new XElement("filesToEnable", files.Select(f => new XElement("Item", f.Element("filename")!.Value))),
                    new XElement("txdToLoad"),
                    new XElement("txdToUnload"),
                    new XElement("residentResources"),
                    new XElement("unregisterResources"))),
            new XElement("patchFiles")));
    }

    private static string ChangeSetName(string name) => name.ToUpperInvariant() + "_AUTOGEN";

    private static XElement DataFile(string fileName, string type, bool overlay, bool persistent, params object[] extra) =>
        new("Item",
            new XElement("filename", fileName),
            new XElement("fileType", type),
            new XElement("overlay", new XAttribute("value", overlay)),
            new XElement("disabled", new XAttribute("value", true)),
            new XElement("persistent", new XAttribute("value", persistent)),
            extra);

    private static string Serialize(XElement root)
    {
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var writer = new Utf8StringWriter();
        document.Save(writer);
        return writer.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
