using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;
using NodeRunner.Data.Models;

namespace NodeRunner.Data.Scenarios;

// A clip set (clip_sets.ymt, clip_sets.xml of the packs): the clip dictionary (.ycd) its clips come from, by name hash.
public sealed record ClipSet(uint Dictionary, IReadOnlyList<uint> Clips);

// What a ped plays on a scenario point: a clip (by hashes of its .ycd and its name) and the object it holds (model
// name, lowercase), in the right hand unless LeftHand.
public sealed record ScenarioClipChoice(uint Dictionary, uint Clip, string? Prop, bool LeftHand);

public interface IClipSetReader
{
    // The clip sets of a clip_sets file (PSO .ymt or XML), by name hash.
    IReadOnlyDictionary<uint, ClipSet> Read(byte[] data);
}

// The clip a ped plays in a loop on a point of a scenario type, picked as the game does: scenarios.meta gives each type
// variants (ConditionalAnims: for men, women, with a probability), each with its base clip set.
public sealed class ScenarioAnimations
{
    private static readonly string[] ScenarioInfoFiles =
    [
        @"update\update.rpf\common\data\ai\scenarios.meta",
        @"common.rpf\data\ai\scenarios.meta",
    ];

    // Objects held in a hand (propsets.meta: CIGARETTES, PHONES, COFFEECUPS...).
    private static readonly string[] PropSetFiles =
    [
        @"update\update.rpf\common\data\ai\propsets.meta",
        @"common.rpf\data\ai\propsets.meta",
    ];

    private static readonly uint BaseClip = Joaat.Hash("base");

    // PropSet: hash of the objects it holds, 0 for none.
    private sealed record Variant(float Probability, bool? Male, bool Excluded, uint ClipSet, uint PropSet, bool LeftHand);

    private readonly Dictionary<uint, List<Variant>> _types = new();
    private readonly Dictionary<uint, ClipSet> _clipSets = new();
    private ModelSets _propSets = ModelSets.Empty;

    public static readonly ScenarioAnimations Empty = new();

    public int TypeCount => _types.Count;
    public int ClipSetCount => _clipSets.Count;
    public List<string> Errors { get; } = new();

    public static ScenarioAnimations Load(GameContent content, IClipSetReader reader)
    {
        var animations = new ScenarioAnimations();
        foreach (var path in ScenarioInfoFiles)
        {
            if (content.ReadFile(path) is not { } data)
                continue;
            try
            {
                using var xml = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                animations.AddTypes(XDocument.Load(xml));
            }
            catch (XmlException ex)
            {
                animations.Errors.Add($"{path} : {ex.Message}");
            }
        }

        // Oldest first: a pack's clip sets replace the game's of the same name.
        var files = content.EnumerateAllVersions(".ymt").Concat(content.EnumerateAllVersions(".xml"))
            .Where(p => Path.GetFileName(p).StartsWith("clip_sets", StringComparison.OrdinalIgnoreCase));
        foreach (var path in files.OrderBy(p => p.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            try
            {
                if (content.ReadFile(path) is { } data)
                {
                    foreach (var (hash, set) in reader.Read(data))
                        animations._clipSets[hash] = set;
                }
            }
            catch (InvalidDataException ex)
            {
                animations.Errors.Add($"{path} : {ex.Message}");
            }
        }

        // The first file found (the update's replaces the base game's).
        foreach (var path in PropSetFiles)
        {
            if (content.ReadFile(path) is not { } data)
                continue;
            try
            {
                animations._propSets = ModelSets.Parse(data);
            }
            catch (InvalidDataException ex)
            {
                animations.Errors.Add($"{path} : {ex.Message}");
            }
            break;
        }
        return animations;
    }

    // The first file listing a type (the newest) is kept.
    private void AddTypes(XDocument document)
    {
        foreach (var group in document.Descendants("ConditionalAnimsGroup"))
        {
            if (group.Parent?.Element("Name")?.Value.Trim() is not { Length: > 0 } typeName)
                continue;
            uint type = Joaat.Hash(typeName);
            if (_types.ContainsKey(type))
                continue;
            var variants = new List<Variant>();
            foreach (var anims in group.Element("ConditionalAnims")?.Elements("Item") ?? [])
            {
                string propSet = anims.Element("PropSet")?.Value.Trim() ?? "";
                bool holding = propSet.Length > 0;
                // The base clip set made for a ped holding the object or not (HasProp / HasNoProp), else the first.
                var bases = (anims.Element("BaseAnims")?.Elements("Item") ?? [])
                    .Select(i => (Name: i.Element("ClipSet")?.Value.Trim(), Item: i)).Where(b => !string.IsNullOrEmpty(b.Name)).ToList();
                var clipSet = bases.Where(b => HoldsProp(b.Item) is not { } wants || wants == holding).Select(b => b.Name).FirstOrDefault()
                    ?? bases.Select(b => b.Name).FirstOrDefault();
                if (clipSet is null)
                    continue;
                bool? male = null;
                bool excluded = false;
                foreach (var condition in anims.Element("Conditions")?.Elements("Item") ?? [])
                {
                    bool result = string.Equals(condition.Element("Result")?.Value.Trim(), "True", StringComparison.OrdinalIgnoreCase);
                    switch (condition.Attribute("type")?.Value)
                    {
                        case "CScenarioConditionIsMale":
                            male = result;
                            break;
                        // Only for some models, the player or a ped in a vehicle: left to the other variants.
                        case "CScenarioConditionModel" or "CScenarioConditionIsPlayer" or "CScenarioConditionIsPlayerInMultiplayerGame"
                            or "CScenarioConditionInStationaryVehicleScenario" or "CScenarioConditionHasHighHeels" when result:
                            excluded = true;
                            break;
                    }
                }
                float probability = float.TryParse(anims.Element("Probability")?.Attribute("value")?.Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float p) ? Math.Max(p, 0f) : 1f;
                bool left = (anims.Element("Flags")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("SpawnPropInLeftHand", StringComparer.OrdinalIgnoreCase);
                variants.Add(new Variant(probability, male, excluded, Joaat.Hash(clipSet), holding ? Joaat.Hash(propSet) : 0, left));
            }
            if (variants.Count > 0)
                _types[type] = variants;
        }
    }

    // Whether a base clip set is for a ped holding its object (true), not holding it (false), or either (null).
    private static bool? HoldsProp(XElement item)
    {
        foreach (var condition in item.Element("Conditions")?.Elements("Item") ?? [])
        {
            bool result = string.Equals(condition.Element("Result")?.Value.Trim(), "True", StringComparison.OrdinalIgnoreCase);
            switch (condition.Attribute("type")?.Value)
            {
                case "CScenarioConditionHasProp":
                    return result;
                case "CScenarioConditionHasNoProp":
                    return !result;
            }
        }
        return null;
    }

    // Clip dictionary (.ycd name hash) and clip of a ped on a point of this type, with the object it holds; seed picks
    // among the variants (the same seed gives the same one). Null when the type has no animation.
    public ScenarioClipChoice? Choose(uint typeHash, bool male, int seed)
    {
        if (!_types.TryGetValue(typeHash, out var variants))
            return null;
        var usable = variants.Where(v => !v.Excluded && (v.Male is null || v.Male == male)).ToList();
        if (usable.Count == 0)
            usable = variants.Where(v => v.Male is null || v.Male == male).ToList();
        if (usable.Count == 0)
            usable = variants;
        float total = usable.Sum(v => v.Probability);
        var pick = usable[0];
        if (total > 0f)
        {
            float target = Mix((uint)seed) / (float)uint.MaxValue * total;
            foreach (var v in usable)
            {
                pick = v;
                target -= v.Probability;
                if (target < 0f)
                    break;
            }
        }
        else
        {
            pick = usable[(int)(Mix((uint)seed) % (uint)usable.Count)];
        }

        var props = pick.PropSet == 0 ? [] : _propSets.ModelsOf(pick.PropSet);
        string? prop = props.Count == 0 ? null : props[(int)(Mix((uint)seed ^ 0x51ED27u) % (uint)props.Count)];

        // A clip set missing from the tables: the dictionary is named after it.
        if (!_clipSets.TryGetValue(pick.ClipSet, out var set) || set.Dictionary == 0)
            return new ScenarioClipChoice(pick.ClipSet, BaseClip, prop, pick.LeftHand);
        uint clip = set.Clips.Contains(BaseClip) || set.Clips.Count == 0 ? BaseClip : set.Clips[0];
        return new ScenarioClipChoice(set.Dictionary, clip, prop, pick.LeftHand);
    }

    private static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }
}
