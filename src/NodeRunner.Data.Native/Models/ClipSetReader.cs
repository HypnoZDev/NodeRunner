using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Native.Resources;
using NodeRunner.Data.Scenarios;

namespace NodeRunner.Data.Native.Models;

// Reads the clip set tables: clip_sets.ymt (PSO, fields known by hash only) and the clip_sets.xml of the packs.
public sealed class ClipSetReader : IClipSetReader
{
    // PSO field names (fwClipSetManager.clipSets, fwClipSet.clipDictionaryName, fwClipSet.clipItems).
    private const string PsoClipSets = "_0x8D0E43EE";
    private const string PsoDictionary = "_0x25DE16DF";
    private const string PsoClipItems = "_0xC4622BBA";

    public IReadOnlyDictionary<uint, ClipSet> Read(byte[] data)
    {
        bool pso = PsoDocument.IsPso(data);
        XElement root;
        if (pso)
        {
            root = PsoDocument.Parse(data, []);
        }
        else
        {
            try
            {
                root = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root ?? throw new InvalidDataException("XML vide.");
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"XML invalide : {ex.Message}", ex);
            }
        }

        var sets = new Dictionary<uint, ClipSet>();
        var list = root.Element(pso ? PsoClipSets : "clipSets");
        foreach (var item in list?.Elements("Item") ?? [])
        {
            uint name = Hash(item.Attribute("key")?.Value);
            uint dictionary = Hash(item.Element(pso ? PsoDictionary : "clipDictionaryName")?.Value);
            if (name == 0)
                continue;
            var clips = (item.Element(pso ? PsoClipItems : "clipItems")?.Elements("Item") ?? [])
                .Select(c => Hash(c.Attribute("key")?.Value)).Where(h => h != 0).ToList();
            sets[name] = new ClipSet(dictionary, clips);
        }
        return sets;
    }

    // A name, or the "hash_XXXXXXXX" the PSO reader gives for a name it does not know.
    private static uint Hash(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;
        if (text.StartsWith("hash_", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
            return hash;
        return Joaat.Hash(text);
    }
}
