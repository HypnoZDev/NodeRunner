using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.Dlc;

namespace NodeRunner.Data.Native.Dlc;

// reader for the DLC configuration files: dlclist.xml (SMandatoryPacksData), setup2.xml (SSetupData) and content.xml
// (CDataFileMgr__ContentsOfDataFileXml).
public sealed class DlcManifestReader : IDlcManifestReader
{
    public IReadOnlyList<string> ReadPackList(byte[] data)
    {
        var root = Parse(data);
        return Child(root, "Paths")?.Elements()
            .Select(item => PackName(item.Value))
            .Where(name => name.Length > 0)
            .ToList() ?? [];
    }

    public DlcSetup ReadSetup(byte[] data)
    {
        var root = Parse(data);
        var groups = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var group in Child(root, "contentChangeSetGroups")?.Elements() ?? [])
        {
            string? name = Text(group, "NameHash")?.ToUpperInvariant();
            if (string.IsNullOrEmpty(name))
                continue;
            var changeSets = Items(group, "ContentChangeSets").Select(s => s.ToUpperInvariant()).ToList();
            groups[name] = groups.TryGetValue(name, out var existing) ? [.. existing, .. changeSets] : changeSets;
        }

        return new DlcSetup
        {
            DeviceName = Text(root, "deviceName") ?? throw new InvalidDataException("setup2.xml sans deviceName."),
            Name = Text(root, "nameHash"),
            ContentFile = Text(root, "datFile") ?? "content.xml",
            Order = IntValue(root, "order"),
            SubPackCount = IntValue(root, "subPackCount"),
            ChangeSetGroups = groups,
        };
    }

    public DlcContent ReadContent(byte[] data)
    {
        var root = Parse(data);
        var dataFiles = (Child(root, "dataFiles")?.Elements() ?? [])
            .Select(item => new DlcDataFile(
                Text(item, "filename") ?? "", Text(item, "fileType") ?? "", BoolValue(item, "disabled")))
            .Where(file => file.FileName.Length > 0)
            .ToList();

        var changeSets = new Dictionary<string, DlcChangeSet>();
        foreach (var item in Child(root, "contentChangeSets")?.Elements() ?? [])
        {
            string? name = Text(item, "changeSetName")?.ToUpperInvariant();
            if (string.IsNullOrEmpty(name))
                continue;
            var mapData = Child(item, "mapChangeSetData")?.Elements().ToList() ?? [];
            changeSets[name] = new DlcChangeSet
            {
                Name = name,
                FilesToInvalidate = [.. Items(item, "filesToInvalidate"), .. mapData.SelectMany(m => Items(m, "filesToInvalidate"))],
                FilesToDisable = [.. Items(item, "filesToDisable"), .. mapData.SelectMany(m => Items(m, "filesToDisable"))],
                FilesToEnable = [.. Items(item, "filesToEnable"), .. mapData.SelectMany(m => Items(m, "filesToEnable"))],
            };
        }

        return new DlcContent { DataFiles = dataFiles, ChangeSets = changeSets };
    }

    private static string PackName(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/').Split('/', ':').LastOrDefault()?.ToLowerInvariant() ?? "";

    private static XElement Parse(byte[] data)
    {
        try
        {
            return XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root
                ?? throw new InvalidDataException("Fichier XML vide.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML invalide : {ex.Message}", ex);
        }
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Text(XElement parent, string name) => Child(parent, name)?.Value.Trim();

    private static IEnumerable<string> Items(XElement parent, string name) =>
        (Child(parent, name)?.Elements() ?? []).Select(e => e.Value.Trim()).Where(v => v.Length > 0);

    private static int IntValue(XElement parent, string name) =>
        int.TryParse((string?)Child(parent, name)?.Attribute("value"), out int value) ? value : 0;

    private static bool BoolValue(XElement parent, string name) =>
        bool.TryParse((string?)Child(parent, name)?.Attribute("value"), out bool value) && value;
}
