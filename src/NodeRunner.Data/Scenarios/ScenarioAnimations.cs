using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Hashing;

namespace NodeRunner.Data.Scenarios;

// A clip set (clip_sets.ymt, clip_sets.xml of the packs): the clip dictionary (.ycd) its clips come from, by name hash.
public sealed record ClipSet(uint Dictionary, IReadOnlyList<uint> Clips);

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

    private static readonly uint BaseClip = Joaat.Hash("base");

    private sealed record Variant(float Probability, bool? Male, bool Excluded, uint ClipSet);

    private readonly Dictionary<uint, List<Variant>> _types = new();
    private readonly Dictionary<uint, ClipSet> _clipSets = new();

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
                var clipSet = anims.Element("BaseAnims")?.Elements("Item").Select(i => i.Element("ClipSet")?.Value.Trim())
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));
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
                variants.Add(new Variant(probability, male, excluded, Joaat.Hash(clipSet)));
            }
            if (variants.Count > 0)
                _types[type] = variants;
        }
    }

    // Clip dictionary (.ycd name hash) and clip of a ped on a point of this type; seed picks among the variants (the
    // same seed gives the same one). Null when the type has no animation.
    public (uint Dictionary, uint Clip)? Choose(uint typeHash, bool male, int seed)
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

        // A clip set missing from the tables: the dictionary is named after it.
        if (!_clipSets.TryGetValue(pick.ClipSet, out var set) || set.Dictionary == 0)
            return (pick.ClipSet, BaseClip);
        uint clip = set.Clips.Contains(BaseClip) || set.Clips.Count == 0 ? BaseClip : set.Clips[0];
        return (set.Dictionary, clip);
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
